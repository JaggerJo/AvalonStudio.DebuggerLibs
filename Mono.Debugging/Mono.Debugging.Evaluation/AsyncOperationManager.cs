// RuntimeInvokeManager.cs
//
// Author:
//   Lluis Sanchez Gual <lluis@novell.com>
//
// Copyright (c) 2008 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mono.Debugging.Client;

namespace Mono.Debugging.Evaluation
{
	public class AsyncOperationManager : IDisposable
	{
		class OperationData
		{
			public IAsyncOperationBase Operation { get; private set; }
			public CancellationTokenSource TokenSource { get; private set; }

			public OperationData (IAsyncOperationBase operation, CancellationTokenSource tokenSource)
			{
				Operation = operation;
				TokenSource = tokenSource;
			}
		}

		readonly HashSet<OperationData> currentOperations = new HashSet<OperationData> ();
		bool disposed = false;
		const int ShortCancelTimeout = 100;
		const int LongCancelTimeout = 1000;

		static bool IsOperationCancelledException (Exception e, int depth = 4)
		{
			if (e is OperationCanceledException)
				return true;
			var aggregateException = e as AggregateException;

			if (depth > 0 && aggregateException != null) {
				foreach (var innerException in aggregateException.InnerExceptions) {
					if (IsOperationCancelledException (innerException, depth - 1))
						return true;
				}
			}
			return false;
		}

		public OperationResult<TValue> Invoke<TValue> (AsyncOperationBase<TValue> mc, int timeout)
		{
			if (timeout <= 0)
				throw new ArgumentOutOfRangeException("timeout", timeout, "timeout must be greater than 0");

			Task<OperationResult<TValue>> task;
			var description = mc.Description;
			var cts = new CancellationTokenSource ();
			var operationData = new OperationData (mc, cts);
			lock (currentOperations) {
				if (disposed)
					throw new ObjectDisposedException ("Already disposed");
				DebuggerLoggingService.LogMessage (string.Format("Starting invoke for {0}", description));
				task = mc.InvokeAsync (cts.Token);
				currentOperations.Add (operationData);
			}

			bool cancelledAfterTimeout = false;
			try {
				if (task.Wait (timeout)) {
					DebuggerLoggingService.LogMessage (string.Format ("Invoke {0} succeeded in {1} ms", description, timeout));
					return task.Result;
				}
				DebuggerLoggingService.LogMessage (string.Format ("Invoke {0} timed out after {1} ms. Cancelling.", description, timeout));
				cts.Cancel ();
				try {
					WaitAfterCancel (mc);
				}
				catch (Exception e) {
					if (IsOperationCancelledException (e)) {
						DebuggerLoggingService.LogMessage (string.Format ("Invoke {0} was cancelled after timeout", description));
						cancelledAfterTimeout = true;
					}
					throw;
				}
				DebuggerLoggingService.LogMessage (string.Format ("{0} cancelling timed out", description));
				throw new TimeOutException ();
			}
			catch (Exception e) {
				if (IsOperationCancelledException (e)) {
					if (cancelledAfterTimeout)
						throw new TimeOutException ();
					DebuggerLoggingService.LogMessage (string.Format ("Invoke {0} was cancelled outside before timeout", description));
					throw new EvaluatorAbortedException ();
				}
				throw;
			}
			finally {
				lock (currentOperations) {
					currentOperations.Remove (operationData);
				}
			}
		}


		public event EventHandler<BusyStateEventArgs> BusyStateChanged = delegate {  };

		void WaitAfterCancel (IAsyncOperationBase op)
		{
			var desc = op.Description;
			DebuggerLoggingService.LogMessage (string.Format ("Waiting for cancel of invoke {0}", desc));
			try {
				if (!op.RawTask.Wait (ShortCancelTimeout)) {
					try {
						BusyStateChanged (this, new BusyStateEventArgs {IsBusy = true, Description = desc});
						op.RawTask.Wait (LongCancelTimeout);
					}
					finally {
						BusyStateChanged (this, new BusyStateEventArgs {IsBusy = false, Description = desc});
					}
				}
			}
			finally {
				DebuggerLoggingService.LogMessage (string.Format ("Calling AfterCancelled() for {0}", desc));
				op.AfterCancelled (ShortCancelTimeout + LongCancelTimeout);
			}
		}


		public void AbortAll ()
		{
			DebuggerLoggingService.LogMessage ("Aborting all the current invocations");
			List<OperationData> copy;
			lock (currentOperations) {
				if (disposed) throw new ObjectDisposedException ("Already disposed");
				copy = currentOperations.ToList ();
				currentOperations.Clear ();
			}

			CancelOperations (copy, true);
		}

		void CancelOperations (List<OperationData> operations, bool wait)
		{
			foreach (var operationData in operations) {
				var taskDescription = operationData.Operation.Description;
				try {
					operationData.TokenSource.Cancel ();
					if (wait) {
						WaitAfterCancel (operationData.Operation);
					}
				}
				catch (Exception e) {
					if (IsOperationCancelledException (e)) {
						DebuggerLoggingService.LogMessage (string.Format ("Invocation of {0} cancelled in CancelOperations()", taskDescription));
					}
					else {
						DebuggerLoggingService.LogError (string.Format ("Invocation of {0} thrown an exception in CancelOperations()", taskDescription), e);
					}
				}
			}
		}


		public void Dispose ()
		{
			List<OperationData> copy;
			lock (currentOperations) {
				if (disposed) throw new ObjectDisposedException ("Already disposed");
				disposed = true;
				copy = currentOperations.ToList ();
				currentOperations.Clear ();
			}
			// don't wait on dispose
			CancelOperations (copy, wait: false);
		}
	}
}