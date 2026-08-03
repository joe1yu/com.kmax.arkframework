using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace ArkFramework.Tests
{
    public sealed class SerializedOperationQueueTests
    {
        [UnityTest]
        public IEnumerator OperationsRunInRequestOrderAfterPreviousFailure()
        {
            return RunAsync(
                OperationsRunInRequestOrderAfterPreviousFailureAsync());
        }

        private static async Task
            OperationsRunInRequestOrderAfterPreviousFailureAsync()
        {
            var queue = new SerializedOperationQueue();
            var order = new List<string>();

            var first = queue.Enqueue(
                async () =>
                {
                    order.Add("first-start");
                    await Task.Yield();
                    order.Add("first-fail");
                    throw new InvalidOperationException("expected");
                });
            var second = queue.Enqueue(
                () =>
                {
                    order.Add("second");
                    return Task.CompletedTask;
                });

            await CaptureAsync<InvalidOperationException>(first);
            await second;

            CollectionAssert.AreEqual(
                new[] { "first-start", "first-fail", "second" },
                order);
        }

        [UnityTest]
        public IEnumerator GenericOperationPropagatesResult()
        {
            return RunAsync(GenericOperationPropagatesResultAsync());
        }

        private static async Task GenericOperationPropagatesResultAsync()
        {
            var queue = new SerializedOperationQueue();

            var result = await queue.Enqueue(() => Task.FromResult(42));

            Assert.That(result, Is.EqualTo(42));
        }

        [UnityTest]
        public IEnumerator CanceledOperationPropagatesItsCancellationToken()
        {
            return RunAsync(
                CanceledOperationPropagatesItsCancellationTokenAsync());
        }

        private static async Task
            CanceledOperationPropagatesItsCancellationTokenAsync()
        {
            var queue = new SerializedOperationQueue();
            using (var source = new CancellationTokenSource())
            {
                source.Cancel();

                var operation = queue.Enqueue<int>(
                    () => Task.FromCanceled<int>(source.Token));

                var exception =
                    await CaptureAsync<TaskCanceledException>(operation);
                Assert.That(
                    exception.CancellationToken,
                    Is.EqualTo(source.Token));
            }
        }

        [UnityTest]
        public IEnumerator ConcurrentEnqueueExecutesEveryOperationExactlyOnce()
        {
            return RunAsync(
                ConcurrentEnqueueExecutesEveryOperationExactlyOnceAsync());
        }

        private static async Task
            ConcurrentEnqueueExecutesEveryOperationExactlyOnceAsync()
        {
            var queue = new SerializedOperationQueue();
            var executed = 0;
            var operations = new Task[32];

            Parallel.For(
                0,
                operations.Length,
                index =>
                {
                    operations[index] = queue.Enqueue(
                        () =>
                        {
                            Interlocked.Increment(ref executed);
                            return Task.CompletedTask;
                        });
                });

            await Task.WhenAll(operations);

            Assert.That(executed, Is.EqualTo(operations.Length));
        }

        private static async Task<TException> CaptureAsync<TException>(
            Task operation)
            where TException : Exception
        {
            try
            {
                await operation;
            }
            catch (TException exception)
            {
                return exception;
            }

            Assert.Fail(
                $"Expected {typeof(TException).Name}, but the operation completed.");
            return null;
        }

        private static IEnumerator RunAsync(Task task)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted)
            {
                throw task.Exception?.InnerException ?? task.Exception;
            }

            if (task.IsCanceled)
            {
                throw new TaskCanceledException(task);
            }
        }
    }
}
