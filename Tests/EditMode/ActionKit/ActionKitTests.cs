using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class ActionKitTests
    {
        private ActionService _service;
        private RecordingLogger _logger;

        [SetUp]
        public void SetUp()
        {
            _logger = new RecordingLogger();
            _service = new ActionService(_logger);
        }

        [TearDown]
        public void TearDown()
        {
            _service.Dispose();
        }

        [Test]
        public void Sequence_DrainsCallbacksAndWaitsForDelay()
        {
            var calls = 0;
            var completed = false;
            var action = ActionKit.Sequence()
                .Callback(() => calls++)
                .Delay(1f)
                .Callback(() => calls++);

            var handle = action.Start(_service, () => completed = true);

            Assert.That(calls, Is.EqualTo(1));
            Assert.That(handle.IsRunning, Is.True);
            _service.Update(0.5f);
            Assert.That(calls, Is.EqualTo(1));
            _service.Update(0.5f);

            Assert.That(calls, Is.EqualTo(2));
            Assert.That(completed, Is.True);
            Assert.That(handle.Status, Is.EqualTo(ActionStatus.Completed));
            Assert.That(_service.RunningCount, Is.Zero);
        }

        [Test]
        public void Parallel_CompletesAfterLongestChild()
        {
            var first = false;
            var second = false;
            var action = ActionKit.Parallel()
                .Delay(1f, () => first = true)
                .Delay(2f, () => second = true);

            var handle = action.Start(_service);
            _service.Update(1f);

            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
            Assert.That(handle.IsRunning, Is.True);

            _service.Update(1f);
            Assert.That(second, Is.True);
            Assert.That(handle.Status, Is.EqualTo(ActionStatus.Completed));
        }

        [Test]
        public void Condition_WaitsUntilPredicateBecomesTrue()
        {
            var ready = false;
            var invoked = false;
            var action = ActionKit.Sequence()
                .Condition(() => ready)
                .Callback(() => invoked = true);

            action.Start(_service);
            _service.Update(0f);
            Assert.That(invoked, Is.False);

            ready = true;
            _service.Update(0f);
            Assert.That(invoked, Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Completed));
        }

        [Test]
        public void Repeat_ExecutesAtMostOneSynchronousIterationPerTick()
        {
            var calls = 0;
            var action = ActionKit.Repeat(3)
                .Callback(() => calls++);

            action.Start(_service);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(action.CompletedIterations, Is.EqualTo(1));

            _service.Update(0f);
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Running));

            _service.Update(0f);
            Assert.That(calls, Is.EqualTo(3));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Completed));
        }

        [Test]
        public void NextFrame_CompletesOnFirstTick()
        {
            var invoked = false;
            var action = ActionKit.NextFrame(() => invoked = true);

            action.Start(_service);
            Assert.That(invoked, Is.False);

            _service.Update(0f);
            Assert.That(invoked, Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Completed));
        }

        [Test]
        public void Cancel_CascadesAndSkipsCompletionCallback()
        {
            var canceled = false;
            var completed = false;
            var action = ActionKit.Sequence()
                .Custom(custom => custom
                    .OnExecute(delta => { })
                    .OnCanceled(() => canceled = true))
                .Callback(() => completed = true);

            var handle = action.Start(_service);
            handle.Cancel();
            _service.Update(1f);

            Assert.That(canceled, Is.True);
            Assert.That(completed, Is.False);
            Assert.That(handle.Status, Is.EqualTo(ActionStatus.Canceled));
            Assert.That(_service.RunningCount, Is.Zero);
        }

        [Test]
        public void DestroyedOwner_CancelsOwnedAction()
        {
            var owner = new GameObject("Action owner");
            var action = new DelayAction(10f);
            var handle = _service.Start(action, owner);

            Object.DestroyImmediate(owner);
            _service.Update(0f);

            Assert.That(handle.Status, Is.EqualTo(ActionStatus.Canceled));
            Assert.That(_service.RunningCount, Is.Zero);
        }

        [Test]
        public void NestedFluentActions_KeepCompositionOrder()
        {
            var log = string.Empty;
            var action = ActionKit.Sequence()
                .Callback(() => log += "A")
                .Parallel(parallel => parallel
                    .NextFrame(() => log += "B")
                    .DelayFrame(2, () => log += "C"))
                .Sequence(sequence => sequence
                    .Callback(() => log += "D"));

            action.Start(_service);
            Assert.That(log, Is.EqualTo("A"));
            _service.Update(0f);
            Assert.That(log, Is.EqualTo("AB"));
            _service.Update(0f);

            Assert.That(log, Is.EqualTo("ABCD"));
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Completed));
        }

        [Test]
        public void Async_WaitsForTaskAndObservesCompletionOnTick()
        {
            var gate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var invoked = false;
            var action = ActionKit.Sequence()
                .Async(token => gate.Task)
                .Callback(() => invoked = true);

            action.Start(_service);
            _service.Update(0f);
            Assert.That(invoked, Is.False);

            gate.SetResult(true);
            _service.Update(0f);
            Assert.That(invoked, Is.True);
            Assert.That(action.Status, Is.EqualTo(ActionStatus.Completed));
        }

        [Test]
        public void FaultedAction_DoesNotStopSiblingAction()
        {
            var root = new TestActionException();
            var siblingCompleted = false;
            var faulted = ActionKit.Custom(custom => custom
                .OnExecute(delta => throw root));
            var sibling = ActionKit.NextFrame(() => siblingCompleted = true);

            var faultedHandle = faulted.Start(_service);
            sibling.Start(_service);
            Assert.DoesNotThrow(() => _service.Update(0f));

            Assert.That(faultedHandle.Status, Is.EqualTo(ActionStatus.Faulted));
            Assert.That(faultedHandle.Exception, Is.SameAs(root));
            Assert.That(siblingCompleted, Is.True);
            Assert.That(_logger.ErrorCount, Is.EqualTo(1));
        }

        [Test]
        public void Dispose_CancelsAllAndRejectsNewActions()
        {
            var first = ActionKit.Delay(1f).Start(_service);
            var second = ActionKit.Condition(() => false).Start(_service);

            _service.Dispose();

            Assert.That(first.Status, Is.EqualTo(ActionStatus.Canceled));
            Assert.That(second.Status, Is.EqualTo(ActionStatus.Canceled));
            Assert.Throws<ObjectDisposedException>(
                () => ActionKit.Callback(() => { }).Start(_service));
        }

        [Test]
        public void CancelAll_FromActionCallbackKeepsUpdateCollectionValid()
        {
            var sibling = ActionKit.Delay(10f).Start(_service);
            var canceling = ActionKit.Custom(custom => custom
                .OnExecute(delta => _service.CancelAll()))
                .Start(_service);

            Assert.DoesNotThrow(() => _service.Update(0f));

            Assert.That(sibling.Status, Is.EqualTo(ActionStatus.Canceled));
            Assert.That(canceling.Status, Is.EqualTo(ActionStatus.Canceled));
            Assert.That(_service.RunningCount, Is.Zero);
        }

        [Test]
        public void Module_RegistersServiceForwardsUpdateAndCancelsOnStop()
        {
            var runtime = new FrameworkRuntime(_logger);
            var module = new ActionKitModule();
            var descriptor = new ModuleDescriptor(
                BuiltInModuleIds.ActionKit,
                Array.Empty<string>(),
                0,
                () => module);

            runtime.StartAsync(
                    new[] { descriptor },
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            var service = runtime.Services.Resolve<IActionService>();
            var invoked = false;
            var handle = ActionKit.NextFrame(() => invoked = true)
                .Start(service);

            runtime.Update(0f);
            Assert.That(invoked, Is.True);
            Assert.That(handle.Status, Is.EqualTo(ActionStatus.Completed));

            var pending = ActionKit.Delay(10f).Start(service);
            runtime.StopAsync(CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            Assert.That(pending.Status, Is.EqualTo(ActionStatus.Canceled));
            Assert.Throws<ObjectDisposedException>(
                () => ActionKit.Delay(1f).Start(service));
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        private sealed class RecordingLogger : IFrameworkLogger
        {
            public int ErrorCount { get; private set; }

            public void Debug(string moduleId, string category, string message)
            {
            }

            public void Info(string moduleId, string category, string message)
            {
            }

            public void Warning(string moduleId, string category, string message)
            {
            }

            public void Error(
                string moduleId,
                string category,
                string message,
                Exception exception)
            {
                ErrorCount++;
            }
        }

        private sealed class TestActionException : Exception
        {
        }
    }
}
