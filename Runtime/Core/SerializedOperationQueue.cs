using System;
using System.Threading.Tasks;

namespace ArkFramework
{
    /// <summary>
    /// 串行执行会改变共享生命周期状态的异步操作。
    /// </summary>
    internal sealed class SerializedOperationQueue
    {
        private readonly object _sync = new object();
        private Task _tail = Task.CompletedTask;

        public Task Enqueue(Func<Task> operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            Task predecessor;
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                predecessor = _tail;
                _tail = completion.Task;
            }

            _ = CompleteAsync(predecessor, operation, completion);
            return completion.Task;
        }

        public Task<TResult> Enqueue<TResult>(
            Func<Task<TResult>> operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            Task predecessor;
            var completion = new TaskCompletionSource<TResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_sync)
            {
                predecessor = _tail;
                _tail = completion.Task;
            }

            _ = CompleteAsync(predecessor, operation, completion);
            return completion.Task;
        }

        private static async Task CompleteAsync(
            Task predecessor,
            Func<Task> operation,
            TaskCompletionSource<bool> completion)
        {
            try
            {
                await IgnorePredecessorFailureAsync(predecessor);
                await operation();
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private static async Task CompleteAsync<TResult>(
            Task predecessor,
            Func<Task<TResult>> operation,
            TaskCompletionSource<TResult> completion)
        {
            try
            {
                await IgnorePredecessorFailureAsync(predecessor);
                completion.TrySetResult(await operation());
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private static async Task IgnorePredecessorFailureAsync(
            Task predecessor)
        {
            try
            {
                await predecessor;
            }
            catch
            {
                // 前序失败只属于前序调用，不能阻止后续清理或生命周期操作。
            }
        }
    }
}
