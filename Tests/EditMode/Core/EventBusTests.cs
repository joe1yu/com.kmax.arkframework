using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace ArkFramework.Tests
{
    public sealed class EventBusTests
    {
        private FrameworkRuntime _runtime;
        private EventBusModule _module;
        private RecordingLogger _logger;
        private IEventBus _eventBus;

        [SetUp]
        public void SetUp()
        {
            _logger = new RecordingLogger();
            _runtime = new FrameworkRuntime(_logger);
            _module = new EventBusModule();
            Await(
                _runtime.StartAsync(
                    new[]
                    {
                        new ModuleDescriptor(
                            "EventBus",
                            Array.Empty<string>(),
                            0,
                            () => _module)
                    },
                    CancellationToken.None));
            _eventBus = _runtime.Services.Resolve<IEventBus>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_runtime != null)
            {
                Await(_runtime.StopAsync(CancellationToken.None));
                Await(_runtime.DisposeAsync());
            }
        }

        [Test]
        public void Publish_InvokesSubscribersInSubscriptionOrder()
        {
            var received = new List<string>();
            _eventBus.Subscribe<NumberEvent>(_ => received.Add("first"));
            _eventBus.Subscribe<NumberEvent>(_ => received.Add("second"));

            _eventBus.Publish(new NumberEvent(7));

            CollectionAssert.AreEqual(new[] { "first", "second" }, received);
        }

        [Test]
        public void SubscriptionDispose_RemovesHandler()
        {
            var invocationCount = 0;
            var subscription =
                _eventBus.Subscribe<NumberEvent>(_ => invocationCount++);

            subscription.Dispose();
            subscription.Dispose();
            _eventBus.Publish(new NumberEvent(1));

            Assert.That(invocationCount, Is.Zero);
        }

        [Test]
        public void Publish_UsesSnapshotWhenHandlersMutateSubscriptions()
        {
            var received = new List<string>();
            IDisposable second = null;
            var mutated = false;
            _eventBus.Subscribe<NumberEvent>(_ =>
            {
                received.Add("first");
                if (!mutated)
                {
                    mutated = true;
                    _eventBus.Subscribe<NumberEvent>(
                        __ => received.Add("late"));
                    second.Dispose();
                }
            });
            second = _eventBus.Subscribe<NumberEvent>(
                _ => received.Add("second"));

            _eventBus.Publish(new NumberEvent(1));
            CollectionAssert.AreEqual(
                new[] { "first", "second" },
                received);

            received.Clear();
            _eventBus.Publish(new NumberEvent(2));
            CollectionAssert.AreEqual(
                new[] { "first", "late" },
                received);
        }

        [Test]
        public void Enqueue_DeliversFifoOnNextUpdate()
        {
            var received = new List<string>();
            _eventBus.Subscribe<NumberEvent>(
                value => received.Add($"number:{value.Value}"));
            _eventBus.Subscribe<TextEvent>(
                value => received.Add($"text:{value.Value}"));

            _eventBus.Enqueue(new NumberEvent(1));
            _eventBus.Enqueue(new TextEvent("two"));
            _eventBus.Enqueue(new NumberEvent(3));

            Assert.That(received, Is.Empty);
            _runtime.Update(0.1f);

            CollectionAssert.AreEqual(
                new[] { "number:1", "text:two", "number:3" },
                received);
        }

        [Test]
        public void EnqueueDuringFlush_WaitsUntilFollowingUpdate()
        {
            var received = new List<int>();
            _eventBus.Subscribe<NumberEvent>(value =>
            {
                received.Add(value.Value);
                if (value.Value == 1)
                {
                    _eventBus.Enqueue(new NumberEvent(2));
                }
            });
            _eventBus.Enqueue(new NumberEvent(1));

            _runtime.Update(0.1f);
            CollectionAssert.AreEqual(new[] { 1 }, received);

            _runtime.Update(0.1f);
            CollectionAssert.AreEqual(new[] { 1, 2 }, received);
        }

        [Test]
        public void HandlerException_IsLoggedAndDoesNotStopLaterHandlers()
        {
            var failure = new TestHandlerException();
            var laterHandlerCalled = false;
            _eventBus.Subscribe<NumberEvent>(_ => throw failure);
            _eventBus.Subscribe<NumberEvent>(_ => laterHandlerCalled = true);

            Assert.DoesNotThrow(
                () => _eventBus.Publish(new NumberEvent(1)));

            Assert.That(laterHandlerCalled, Is.True);
            Assert.That(_logger.Errors.Count, Is.EqualTo(1));
            Assert.That(_logger.Errors[0].ModuleId, Is.EqualTo("EventBus"));
            Assert.That(
                _logger.Errors[0].Category,
                Is.EqualTo(typeof(NumberEvent).FullName));
            Assert.That(_logger.Errors[0].Exception, Is.SameAs(failure));
        }

        [Test]
        public void LoggerException_DoesNotStopLaterHandlers()
        {
            var laterHandlerCalled = false;
            _logger.ThrowOnError = true;
            _eventBus.Subscribe<NumberEvent>(
                _ => throw new TestHandlerException());
            _eventBus.Subscribe<NumberEvent>(
                _ => laterHandlerCalled = true);

            Assert.DoesNotThrow(
                () => _eventBus.Publish(new NumberEvent(1)));

            Assert.That(laterHandlerCalled, Is.True);
            Assert.That(
                _eventBus.Diagnostics.Get<NumberEvent>().ExceptionCount,
                Is.EqualTo(1));
        }

        [Test]
        public void DisposingOwnerScope_AutomaticallyUnsubscribes()
        {
            var owner = _runtime.Services.CreateScope("owner");
            var invocationCount = 0;
            var subscription = _eventBus.Subscribe<NumberEvent>(
                owner,
                _ => invocationCount++);

            Await(owner.DisposeAsync());
            _eventBus.Publish(new NumberEvent(1));

            Assert.That(invocationCount, Is.Zero);
            Assert.DoesNotThrow(subscription.Dispose);
        }

        [UnityTest]
        public IEnumerator EventBusModule_RegistersServiceAndRuntimeUpdateFlushesQueue()
        {
            var received = new List<int>();
            Assert.That(_module.Id, Is.EqualTo("EventBus"));
            Assert.That(_module.Dependencies, Is.Empty);
            Assert.That(_eventBus, Is.SameAs(_runtime.Services.Resolve<IEventBus>()));
            _eventBus.Subscribe<NumberEvent>(
                value => received.Add(value.Value));
            _eventBus.Enqueue(new NumberEvent(42));

            yield return null;
            Assert.That(received, Is.Empty);

            _runtime.Update(0.1f);
            CollectionAssert.AreEqual(new[] { 42 }, received);
        }

        [Test]
        public void Stop_ClearsQueuedEventsAndSubscriptions()
        {
            var invocationCount = 0;
            var subscription =
                _eventBus.Subscribe<NumberEvent>(_ => invocationCount++);
            _eventBus.Enqueue(new NumberEvent(1));

            Await(_runtime.StopAsync(CancellationToken.None));
            Assert.DoesNotThrow(
                () => _eventBus.Publish(new NumberEvent(2)));
            Assert.DoesNotThrow(
                () => _eventBus.Enqueue(new NumberEvent(3)));
            Assert.DoesNotThrow(() => _module.Update(0.1f));
            Assert.DoesNotThrow(subscription.Dispose);

            Assert.That(invocationCount, Is.Zero);
            Assert.That(
                _eventBus.Diagnostics.Get<NumberEvent>().ListenerCount,
                Is.Zero);
        }

        [Test]
        public void Stop_WithLiveToken_ReleasesHandlerTarget()
        {
            var subscription = SubscribeHandlerTarget(
                _eventBus,
                out var handlerTarget);

            Await(_module.StopAsync(CancellationToken.None));

            AssertCollected(handlerTarget);
            Assert.DoesNotThrow(subscription.Dispose);
            Assert.DoesNotThrow(subscription.Dispose);
        }

        [Test]
        public void Stop_ReleasesQueuedPayload()
        {
            var payload = EnqueuePayload(_eventBus);

            Await(_module.StopAsync(CancellationToken.None));

            AssertCollected(payload);
        }

        [Test]
        public void ModuleDispose_LeavesBusOwnedByScopeUntilScopeDisposal()
        {
            var invocationCount = 0;
            var subscription =
                _eventBus.Subscribe<NumberEvent>(_ => invocationCount++);

            Await(_module.DisposeAsync());
            _eventBus.Publish(new NumberEvent(1));

            Assert.That(invocationCount, Is.EqualTo(1));
            Assert.That(
                _eventBus.Diagnostics.Get<NumberEvent>().DispatchCount,
                Is.EqualTo(1));

            Await(_runtime.StopAsync(CancellationToken.None));
            _eventBus.Publish(new NumberEvent(2));

            Assert.That(invocationCount, Is.EqualTo(1));
            Assert.That(
                _eventBus.Diagnostics.Get<NumberEvent>().DispatchCount,
                Is.Zero);
            Assert.DoesNotThrow(subscription.Dispose);
        }

        [Test]
        public void Diagnostics_ReportListenerDispatchAndExceptionCounts()
        {
            var failure = new TestHandlerException();
            var first = _eventBus.Subscribe<NumberEvent>(_ => { });
            _eventBus.Subscribe<NumberEvent>(_ => throw failure);
            var beforeDispatch = DateTime.UtcNow;

            _eventBus.Publish(new NumberEvent(1));
            _eventBus.Publish(new NumberEvent(2));
            var snapshot = _eventBus.Diagnostics;
            var diagnostics = snapshot.Get<NumberEvent>();

            Assert.That(diagnostics.ListenerCount, Is.EqualTo(2));
            Assert.That(diagnostics.DispatchCount, Is.EqualTo(2));
            Assert.That(diagnostics.ExceptionCount, Is.EqualTo(2));
            Assert.That(diagnostics.LastDispatchUtc, Is.GreaterThanOrEqualTo(beforeDispatch));

            first.Dispose();
            Assert.That(diagnostics.ListenerCount, Is.EqualTo(2));
            Assert.That(
                _eventBus.Diagnostics.Get<NumberEvent>().ListenerCount,
                Is.EqualTo(1));
        }

        private static void Await(ValueTask operation)
        {
            operation.AsTask().GetAwaiter().GetResult();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IDisposable SubscribeHandlerTarget(
            IEventBus eventBus,
            out WeakReference targetReference)
        {
            var target = new HandlerTarget();
            targetReference = new WeakReference(target);
            return eventBus.Subscribe<NumberEvent>(target.Handle);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference EnqueuePayload(IEventBus eventBus)
        {
            var payload = new object();
            eventBus.Enqueue(payload);
            return new WeakReference(payload);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AssertCollected(WeakReference reference)
        {
            for (var attempt = 0; attempt < 5 && reference.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.That(reference.IsAlive, Is.False);
        }

        private readonly struct NumberEvent
        {
            public NumberEvent(int value)
            {
                Value = value;
            }

            public int Value { get; }
        }

        private readonly struct TextEvent
        {
            public TextEvent(string value)
            {
                Value = value;
            }

            public string Value { get; }
        }

        private sealed class TestHandlerException : Exception
        {
        }

        private sealed class TestLoggerException : Exception
        {
        }

        private sealed class HandlerTarget
        {
            public void Handle(NumberEvent value)
            {
            }
        }

        private sealed class RecordingLogger : IFrameworkLogger
        {
            public List<ErrorRecord> Errors { get; } = new List<ErrorRecord>();

            public bool ThrowOnError { get; set; }

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
                Errors.Add(new ErrorRecord(moduleId, category, exception));
                if (ThrowOnError)
                {
                    throw new TestLoggerException();
                }
            }
        }

        private sealed class ErrorRecord
        {
            public ErrorRecord(
                string moduleId,
                string category,
                Exception exception)
            {
                ModuleId = moduleId;
                Category = category;
                Exception = exception;
            }

            public string ModuleId { get; }

            public string Category { get; }

            public Exception Exception { get; }
        }
    }
}
