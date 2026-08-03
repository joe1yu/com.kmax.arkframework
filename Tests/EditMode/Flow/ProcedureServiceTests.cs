using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace ArkFramework.Tests
{
    public sealed class ProcedureServiceTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [UnityTest]
        public IEnumerator RegistrationIsExplicitValidatedOrdinalAndFrozen()
        {
            return RunAsync(
                RegistrationIsExplicitValidatedOrdinalAndFrozenAsync());
        }

        private async Task
            RegistrationIsExplicitValidatedOrdinalAndFrozenAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var enterGate = NewCompletion();
            var upper = new RecordingProcedure("A")
            {
                Enter = (context, token) => enterGate.Task
            };

            Assert.Throws<ArgumentNullException>(() => service.Register(null));
            Assert.Throws<ArgumentException>(
                () => service.Register(new RecordingProcedure(" ")));
            service.Register(upper);
            service.Register(new RecordingProcedure("a"));
            Assert.Throws<InvalidOperationException>(
                () => service.Register(new RecordingProcedure("A")));
            Assert.Throws<ArgumentException>(
                () => service.StartAsync(" ").AsTask());

            var start = service.StartAsync("A").AsTask();
            Assert.Throws<InvalidOperationException>(
                () => service.Register(new RecordingProcedure("B")));
            Assert.Throws<InvalidOperationException>(
                () => service.ChangeAsync("a").AsTask());
            enterGate.SetResult(true);
            await AwaitWithin(start);
            await service.ChangeAsync("a");
            Assert.That(service.CurrentProcedureId, Is.EqualTo("a"));
            await service.DisposeAsync();
            await fsm.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator StartUsesContextAndReportsReadOnlySnapshots()
        {
            return RunAsync(StartUsesContextAndReportsReadOnlySnapshotsAsync());
        }

        private async Task StartUsesContextAndReportsReadOnlySnapshotsAsync()
        {
            var container = new ServiceContainer();
            var markerScope = container.CreateScope("marker");
            var marker = new Marker();
            markerScope.RegisterInstance<IMarker>(marker);
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, container);
            IMarker resolved = null;
            var a = new RecordingProcedure("A")
            {
                Enter = (context, token) =>
                {
                    resolved = context.Resolve<IMarker>();
                    Assert.That(
                        context.TryResolve<IMarker>(out var found),
                        Is.True);
                    Assert.That(found, Is.SameAs(marker));
                    Assert.That(
                        context.TryResolve<IMissingMarker>(out var missing),
                        Is.False);
                    Assert.That(missing, Is.Null);
                    return Task.CompletedTask;
                }
            };

            service.Register(a);
            var registeredSnapshot =
                service.Diagnostics.RegisteredProcedureIds;
            service.Register(new RecordingProcedure("B"));
            await service.StartAsync("A");

            Assert.That(resolved, Is.SameAs(marker));
            Assert.That(service.IsStarted, Is.True);
            Assert.That(service.CurrentProcedureId, Is.EqualTo("A"));
            Assert.That(service.PreviousProcedureId, Is.Null);
            Assert.That(service.IsFaulted, Is.False);
            Assert.That(registeredSnapshot, Is.EqualTo(new[] { "A" }));
            var diagnostics = service.Diagnostics;
            Assert.That(
                diagnostics.MachineId,
                Is.EqualTo(ProcedureService.MainMachineId));
            Assert.That(
                diagnostics.RegisteredProcedureIds,
                Is.EqualTo(new[] { "A", "B" }));
            Assert.That(diagnostics.History.Count, Is.EqualTo(1));
            Assert.That(diagnostics.History[0].From, Is.Null);
            Assert.That(diagnostics.History[0].To, Is.EqualTo("A"));
            Assert.That(diagnostics.History[0].Trigger, Is.EqualTo("Start"));
            Assert.Throws<NotSupportedException>(
                () => ((IList<string>)diagnostics.RegisteredProcedureIds)
                    .Add("mutate"));
            Assert.Throws<NotSupportedException>(
                () => ((IList<StateHistoryEntry>)diagnostics.History)
                    .Add(diagnostics.History[0]));

            await service.DisposeAsync();
            await fsm.DisposeAsync();
            Assert.That(marker.DisposeCount, Is.Zero);
            await markerScope.DisposeAsync();
            Assert.That(marker.DisposeCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ChangeRejectsUnknownAndSameCurrentIsNoOp()
        {
            return RunAsync(ChangeRejectsUnknownAndSameCurrentIsNoOpAsync());
        }

        private async Task ChangeRejectsUnknownAndSameCurrentIsNoOpAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var a = new RecordingProcedure("A");
            var b = new RecordingProcedure("B");
            service.Register(a);
            service.Register(b);

            await AssertThrowsAsync<InvalidOperationException>(
                () => service.ChangeAsync("B").AsTask());
            await AssertThrowsAsync<KeyNotFoundException>(
                () => service.StartAsync("missing").AsTask());
            Assert.That(service.IsStarted, Is.False);
            service.Register(new RecordingProcedure("C"));
            await service.StartAsync("A");
            var history = service.Diagnostics.History;

            await AssertThrowsAsync<KeyNotFoundException>(
                () => service.ChangeAsync("missing").AsTask());
            await service.ChangeAsync("A");
            Assert.That(a.EnterCount, Is.EqualTo(1));
            Assert.That(a.ExitCount, Is.Zero);
            Assert.That(service.CurrentProcedureId, Is.EqualTo("A"));
            Assert.That(service.PreviousProcedureId, Is.Null);
            Assert.That(service.Diagnostics.History.Count, Is.EqualTo(1));
            Assert.That(history.Count, Is.EqualTo(1));

            await service.DisposeAsync();
            await fsm.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator FailedOrCanceledInitialEnterCanRetryButFreezesRegister()
        {
            return RunAsync(
                FailedOrCanceledInitialEnterCanRetryButFreezesRegisterAsync());
        }

        private async Task
            FailedOrCanceledInitialEnterCanRetryButFreezesRegisterAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var expected = new TestEnterException();
            var attempts = 0;
            var a = new RecordingProcedure("A")
            {
                Enter = (context, token) =>
                {
                    attempts++;
                    return attempts == 1
                        ? Task.FromException(expected)
                        : Task.CompletedTask;
                }
            };
            service.Register(a);

            var failure = await AssertThrowsAsync<TestEnterException>(
                () => service.StartAsync("A").AsTask());
            Assert.That(failure, Is.SameAs(expected));
            Assert.That(service.IsStarted, Is.False);
            Assert.Throws<InvalidOperationException>(
                () => service.Register(new RecordingProcedure("B")));
            await service.StartAsync("A");
            Assert.That(service.IsStarted, Is.True);
            Assert.That(a.EnterCount, Is.EqualTo(2));
            await service.DisposeAsync();
            await fsm.DisposeAsync();

            fsm = new FsmService();
            service = new ProcedureService(fsm, new ServiceContainer());
            a = new RecordingProcedure("A");
            service.Register(a);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await AssertThrowsAsync<TaskCanceledException>(
                    () => service.StartAsync("A", cancellation.Token)
                        .AsTask());
            }

            Assert.That(a.EnterCount, Is.Zero);
            Assert.Throws<InvalidOperationException>(
                () => service.Register(new RecordingProcedure("B")));
            await service.StartAsync("A");
            Assert.That(a.EnterCount, Is.EqualTo(1));
            Assert.That(service.IsStarted, Is.True);
            await service.DisposeAsync();
            await fsm.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator ConcurrentChangesAreStrictFifoWithoutOverlap()
        {
            return RunAsync(ConcurrentChangesAreStrictFifoWithoutOverlapAsync());
        }

        private async Task ConcurrentChangesAreStrictFifoWithoutOverlapAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var tracker = new CallbackTracker();
            var log = new List<string>();
            var firstExitStarted = NewCompletion();
            var firstExitGate = NewCompletion();
            var a = new RecordingProcedure("A", tracker)
            {
                Enter = (context, token) =>
                {
                    log.Add("enter:A");
                    return Task.CompletedTask;
                },
                Exit = async (context, token) =>
                {
                    log.Add("exit:A");
                    firstExitStarted.TrySetResult(true);
                    await firstExitGate.Task;
                }
            };
            var b = new RecordingProcedure("B", tracker)
            {
                Enter = (context, token) =>
                {
                    log.Add("enter:B");
                    return Task.CompletedTask;
                },
                Exit = (context, token) =>
                {
                    log.Add("exit:B");
                    return Task.CompletedTask;
                }
            };
            var c = new RecordingProcedure("C", tracker)
            {
                Enter = (context, token) =>
                {
                    log.Add("enter:C");
                    return Task.CompletedTask;
                }
            };
            service.Register(a);
            service.Register(b);
            service.Register(c);
            await service.StartAsync("A");

            var first = service.ChangeAsync("B").AsTask();
            await AwaitWithin(firstExitStarted.Task);
            var second = service.ChangeAsync("C").AsTask();
            Assert.That(second.IsCompleted, Is.False);
            Assert.That(c.EnterCount, Is.Zero);
            firstExitGate.SetResult(true);
            await AwaitWithin(Task.WhenAll(first, second));

            Assert.That(
                log,
                Is.EqualTo(
                    new[]
                    {
                        "enter:A",
                        "exit:A",
                        "enter:B",
                        "exit:B",
                        "enter:C"
                    }));
            Assert.That(tracker.MaximumConcurrent, Is.EqualTo(1));
            Assert.That(service.CurrentProcedureId, Is.EqualTo("C"));
            Assert.That(service.PreviousProcedureId, Is.EqualTo("B"));

            await service.DisposeAsync();
            await fsm.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator EnterFailureRollsBackAndKeepsHistoryUnchanged()
        {
            return RunAsync(EnterFailureRollsBackAndKeepsHistoryUnchangedAsync());
        }

        private async Task EnterFailureRollsBackAndKeepsHistoryUnchangedAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var expected = new TestEnterException();
            var a = new RecordingProcedure("A");
            var b = new RecordingProcedure("B")
            {
                Enter = (context, token) => Task.FromException(expected)
            };
            service.Register(a);
            service.Register(b);
            await service.StartAsync("A");

            var thrown = await AssertThrowsAsync<TestEnterException>(
                () => service.ChangeAsync("B").AsTask());
            Assert.That(thrown, Is.SameAs(expected));
            Assert.That(a.EnterCount, Is.EqualTo(2));
            Assert.That(a.ExitCount, Is.EqualTo(1));
            Assert.That(b.EnterCount, Is.EqualTo(1));
            Assert.That(service.CurrentProcedureId, Is.EqualTo("A"));
            Assert.That(service.PreviousProcedureId, Is.Null);
            Assert.That(service.IsFaulted, Is.False);
            Assert.That(service.Diagnostics.History.Count, Is.EqualTo(1));
            Assert.That(service.Diagnostics.LastException, Is.SameAs(expected));

            await service.DisposeAsync();
            await fsm.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator RollbackFailureFaultsProcedure()
        {
            return RunAsync(RollbackFailureFaultsProcedureAsync());
        }

        private async Task RollbackFailureFaultsProcedureAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var old = new RecordingProcedure("A")
            {
                Enter = (context, token) =>
                    Task.FromException(new TestRollbackException())
            };
            var initialEnter = true;
            old.Enter = (context, token) =>
            {
                if (initialEnter)
                {
                    initialEnter = false;
                    return Task.CompletedTask;
                }

                return Task.FromException(new TestRollbackException());
            };
            var targetFailure = new TestEnterException();
            var target = new RecordingProcedure("B")
            {
                Enter = (context, token) => Task.FromException(targetFailure)
            };
            service.Register(old);
            service.Register(target);
            await service.StartAsync("A");

            var aggregate = await AssertThrowsAsync<AggregateException>(
                () => service.ChangeAsync("B").AsTask());
            Assert.That(
                aggregate.InnerExceptions.Any(
                    exception => exception is TestEnterException),
                Is.True);
            Assert.That(
                aggregate.InnerExceptions.Any(
                    exception => exception is TestRollbackException),
                Is.True);
            Assert.That(service.IsFaulted, Is.True);
            Assert.That(service.CurrentProcedureId, Is.EqualTo("A"));
            Assert.That(service.Diagnostics.LastException, Is.SameAs(aggregate));
            var fault = await AssertThrowsAsync<InvalidOperationException>(
                () => service.ChangeAsync("B").AsTask());
            Assert.That(fault.InnerException, Is.SameAs(aggregate));

            await service.DisposeAsync();
            await fsm.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator ActiveCancellationRollsBackWithoutReplacingLastError()
        {
            return RunAsync(
                ActiveCancellationRollsBackWithoutReplacingLastErrorAsync());
        }

        private async Task
            ActiveCancellationRollsBackWithoutReplacingLastErrorAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var enterStarted = NewCompletion();
            var a = new RecordingProcedure("A");
            var b = new RecordingProcedure("B")
            {
                Enter = async (context, token) =>
                {
                    enterStarted.TrySetResult(true);
                    await Task.Delay(
                        System.Threading.Timeout.InfiniteTimeSpan,
                        token);
                }
            };
            service.Register(a);
            service.Register(b);
            await service.StartAsync("A");
            using (var cancellation = new CancellationTokenSource())
            {
                var change = service.ChangeAsync("B", cancellation.Token)
                    .AsTask();
                await AwaitWithin(enterStarted.Task);
                cancellation.Cancel();
                await AssertThrowsAsync<OperationCanceledException>(
                    () => change);
            }

            Assert.That(service.CurrentProcedureId, Is.EqualTo("A"));
            Assert.That(service.PreviousProcedureId, Is.Null);
            Assert.That(service.IsFaulted, Is.False);
            Assert.That(service.Diagnostics.History.Count, Is.EqualTo(1));
            Assert.That(service.Diagnostics.LastException, Is.Null);
            Assert.That(a.EnterCount, Is.EqualTo(2));

            await service.DisposeAsync();
            await fsm.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator QueuedCancellationDoesNotDisturbEarlierChange()
        {
            return RunAsync(
                QueuedCancellationDoesNotDisturbEarlierChangeAsync());
        }

        private async Task
            QueuedCancellationDoesNotDisturbEarlierChangeAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var firstExitStarted = NewCompletion();
            var firstExitGate = NewCompletion();
            var a = new RecordingProcedure("A")
            {
                Exit = async (context, token) =>
                {
                    firstExitStarted.TrySetResult(true);
                    await firstExitGate.Task;
                }
            };
            var b = new RecordingProcedure("B");
            var c = new RecordingProcedure("C");
            service.Register(a);
            service.Register(b);
            service.Register(c);
            await service.StartAsync("A");

            var first = service.ChangeAsync("B").AsTask();
            await AwaitWithin(firstExitStarted.Task);
            using (var cancellation = new CancellationTokenSource())
            {
                var second = service.ChangeAsync("C", cancellation.Token)
                    .AsTask();
                cancellation.Cancel();
                firstExitGate.SetResult(true);
                await AwaitWithin(first);
                await AssertThrowsAsync<OperationCanceledException>(
                    () => second);
            }

            Assert.That(service.CurrentProcedureId, Is.EqualTo("B"));
            Assert.That(service.PreviousProcedureId, Is.EqualTo("A"));
            Assert.That(b.EnterCount, Is.EqualTo(1));
            Assert.That(c.EnterCount, Is.Zero);
            Assert.That(service.Diagnostics.History.Count, Is.EqualTo(2));

            await service.DisposeAsync();
            await fsm.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator UpdateFailureFaultsMachineAndIsDiagnosable()
        {
            return RunAsync(UpdateFailureFaultsMachineAndIsDiagnosableAsync());
        }

        private async Task UpdateFailureFaultsMachineAndIsDiagnosableAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var expected = new TestUpdateException();
            var a = new RecordingProcedure("A")
            {
                UpdateAction =
                    (context, deltaTime) => throw expected
            };
            service.Register(a);
            service.Register(new RecordingProcedure("B"));
            await service.StartAsync("A");

            Assert.DoesNotThrow(() => fsm.Update(0.5f));
            Assert.That(service.IsFaulted, Is.True);
            Assert.That(service.CurrentProcedureId, Is.EqualTo("A"));
            Assert.That(service.Diagnostics.LastException, Is.SameAs(expected));
            var fault = await AssertThrowsAsync<InvalidOperationException>(
                () => service.ChangeAsync("B").AsTask());
            Assert.That(fault.InnerException, Is.SameAs(expected));

            await service.DisposeAsync();
            await fsm.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator PreCanceledStopStillCleansUpBeforeCancellation()
        {
            return RunAsync(
                PreCanceledStopStillCleansUpBeforeCancellationAsync());
        }

        private async Task
            PreCanceledStopStillCleansUpBeforeCancellationAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var a = new RecordingProcedure("A");
            service.Register(a);
            await service.StartAsync("A");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await AssertThrowsAsync<OperationCanceledException>(
                    () => service.StopAsync(cancellation.Token).AsTask());
            }

            Assert.That(a.ExitCount, Is.EqualTo(1));
            Assert.That(
                fsm.TryGet(
                    ProcedureService.MainMachineId,
                    out IStateMachine<ProcedureContext> removed),
                Is.False);
            Assert.That(removed, Is.Null);
            await service.DisposeAsync();
            await fsm.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator DisposeWaitsForActiveChangeIsCanonicalAndReleasesId()
        {
            return RunAsync(
                DisposeWaitsForActiveChangeIsCanonicalAndReleasesIdAsync());
        }

        private async Task
            DisposeWaitsForActiveChangeIsCanonicalAndReleasesIdAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var targetEnterStarted = NewCompletion();
            var targetEnterGate = NewCompletion();
            var a = new RecordingProcedure("A");
            var b = new RecordingProcedure("B")
            {
                Enter = async (context, token) =>
                {
                    targetEnterStarted.TrySetResult(true);
                    await targetEnterGate.Task;
                }
            };
            service.Register(a);
            service.Register(b);
            await service.StartAsync("A");
            var change = service.ChangeAsync("B").AsTask();
            await AwaitWithin(targetEnterStarted.Task);

            var firstDispose = service.DisposeAsync().AsTask();
            var secondDispose = service.StopAsync().AsTask();
            Assert.That(secondDispose, Is.SameAs(firstDispose));
            Assert.That(firstDispose.IsCompleted, Is.False);
            Assert.Throws<ObjectDisposedException>(
                () => service.ChangeAsync("A").AsTask());
            Assert.That(
                fsm.TryGet(
                    ProcedureService.MainMachineId,
                    out IStateMachine<ProcedureContext> owned),
                Is.True);
            Assert.That(owned, Is.Not.Null);

            targetEnterGate.SetResult(true);
            await AwaitWithin(Task.WhenAll(change, firstDispose));
            Assert.That(a.ExitCount, Is.EqualTo(1));
            Assert.That(b.EnterCount, Is.EqualTo(1));
            Assert.That(b.ExitCount, Is.EqualTo(1));
            Assert.That(
                fsm.TryGet(
                    ProcedureService.MainMachineId,
                    out IStateMachine<ProcedureContext> removed),
                Is.False);
            Assert.That(removed, Is.Null);
            await service.DisposeAsync();
            await fsm.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator CallbackReentryChangeAndDisposeAlwaysRejectsQuickly()
        {
            return RunAsync(
                CallbackReentryChangeAndDisposeAlwaysRejectsQuicklyAsync());
        }

        private async Task
            CallbackReentryChangeAndDisposeAlwaysRejectsQuicklyAsync()
        {
            foreach (var callback in new[] { "Enter", "Exit", "Update" })
            {
                var fsm = new FsmService();
                var service = new ProcedureService(
                    fsm,
                    new ServiceContainer());
                var failures = new List<Exception>();
                Action reenter = () =>
                {
                    failures.Add(
                        Capture(() => service.ChangeAsync("B")));
                    failures.Add(
                        Capture(() => service.DisposeAsync()));
                };
                var a = new RecordingProcedure("A");
                var b = new RecordingProcedure("B");
                if (callback == "Enter")
                {
                    a.Enter = (context, token) =>
                    {
                        reenter();
                        return Task.CompletedTask;
                    };
                }
                else if (callback == "Exit")
                {
                    a.Exit = (context, token) =>
                    {
                        reenter();
                        return Task.CompletedTask;
                    };
                }
                else
                {
                    a.UpdateAction = (context, deltaTime) => reenter();
                }

                service.Register(a);
                service.Register(b);
                await service.StartAsync("A");
                if (callback == "Exit")
                {
                    await service.ChangeAsync("B");
                }
                else if (callback == "Update")
                {
                    fsm.Update(0.5f);
                }

                Assert.That(failures.Count, Is.EqualTo(2), callback);
                Assert.That(
                    failures.All(
                        failure => failure is InvalidOperationException),
                    Is.True,
                    callback);
                Assert.That(
                    fsm.TryGet(
                        ProcedureService.MainMachineId,
                        out IStateMachine<ProcedureContext> remainedOwned),
                    Is.True,
                    callback);
                Assert.That(remainedOwned, Is.Not.Null);
                await service.DisposeAsync();
                await fsm.DisposeAsync();
            }
        }

        [UnityTest]
        public IEnumerator DisposeFailureIsSharedAndMachineRemainsOwned()
        {
            return RunAsync(DisposeFailureIsSharedAndMachineRemainsOwnedAsync());
        }

        private async Task DisposeFailureIsSharedAndMachineRemainsOwnedAsync()
        {
            var fsm = new FsmService();
            var service = new ProcedureService(fsm, new ServiceContainer());
            var expected = new TestExitException();
            var a = new RecordingProcedure("A")
            {
                Exit = (context, token) => Task.FromException(expected)
            };
            service.Register(a);
            await service.StartAsync("A");

            var first = service.DisposeAsync().AsTask();
            var second = service.DisposeAsync().AsTask();
            Assert.That(second, Is.SameAs(first));
            var firstFailure = await AssertThrowsAsync<TestExitException>(
                () => first);
            var secondFailure = await AssertThrowsAsync<TestExitException>(
                () => second);
            Assert.That(firstFailure, Is.SameAs(expected));
            Assert.That(secondFailure, Is.SameAs(expected));
            Assert.That(
                fsm.TryGet(
                    ProcedureService.MainMachineId,
                    out IStateMachine<ProcedureContext> remainedOwned),
                Is.True);
            Assert.That(remainedOwned, Is.Not.Null);

            var cleanupFailure =
                await AssertThrowsAsync<TestExitException>(
                    () => fsm.DisposeAsync().AsTask());
            Assert.That(cleanupFailure, Is.SameAs(expected));
        }

        [UnityTest]
        public IEnumerator ModuleUsesExactDependenciesScopeOwnershipAndFsmUpdate()
        {
            return RunAsync(
                ModuleUsesExactDependenciesScopeOwnershipAndFsmUpdateAsync());
        }

        private async Task
            ModuleUsesExactDependenciesScopeOwnershipAndFsmUpdateAsync()
        {
            var module = new ProcedureModule();
            Assert.That(module.Id, Is.EqualTo("Procedure"));
            Assert.That(
                module.Dependencies,
                Is.EqualTo(new[] { "FSM", "Config", "Scene", "UI", "Audio" }));
            Assert.That(module, Is.Not.InstanceOf<IUpdateModule>());

            var runtime = new FrameworkRuntime();
            var fsmModule = new FsmModule();
            var config = new StubModule("Config");
            var scene = new StubModule("Scene");
            var ui = new StubModule("UI");
            var audio = new StubModule("Audio");
            var descriptors = new[]
            {
                Describe(fsmModule, 0),
                Describe(config, 1),
                Describe(scene, 2),
                Describe(ui, 3),
                Describe(audio, 4),
                Describe(module, 5)
            };
            await runtime.StartAsync(descriptors, CancellationToken.None);
            var service = runtime.Services.Resolve<IProcedureService>();
            var procedure = new RecordingProcedure("A");
            Assert.That(service.IsStarted, Is.False);
            Assert.That(
                service.Diagnostics.RegisteredProcedureIds,
                Is.Empty);
            service.Register(procedure);
            await service.StartAsync("A");

            runtime.Update(0.25f);
            Assert.That(procedure.UpdateCount, Is.EqualTo(1));
            await runtime.StopAsync(CancellationToken.None);
            Assert.That(procedure.ExitCount, Is.EqualTo(1));
            Assert.Throws<InvalidOperationException>(
                () => runtime.Services.Resolve<IProcedureService>());

            await runtime.DisposeAsync();
            Assert.That(procedure.ExitCount, Is.EqualTo(1));
        }

        private static ModuleDescriptor Describe(
            IFrameworkModule module,
            int order)
        {
            return new ModuleDescriptor(
                module.Id,
                module.Dependencies,
                order,
                () => module);
        }

        private static Exception Capture(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        private static TaskCompletionSource<bool> NewCompletion()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static async Task AwaitWithin(Task task)
        {
            var completed = await Task.WhenAny(task, Task.Delay(Timeout));
            if (!ReferenceEquals(completed, task))
            {
                throw new TimeoutException(
                    $"Operation did not complete within {Timeout}.");
            }

            await task;
        }

        private static async Task<TException> AssertThrowsAsync<TException>(
            Func<Task> action)
            where TException : Exception
        {
            try
            {
                await AwaitWithin(action());
            }
            catch (TException exception)
            {
                return exception;
            }

            Assert.Fail(
                $"Expected exception of type {typeof(TException).FullName}.");
            return null;
        }

        private static IEnumerator RunAsync(Task task)
        {
            task = AwaitWithin(task);
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsCanceled)
            {
                throw new OperationCanceledException();
            }

            if (task.IsFaulted)
            {
                ExceptionDispatchInfo.Capture(
                    task.Exception.InnerException).Throw();
            }
        }

        private interface IMarker
        {
        }

        private interface IMissingMarker
        {
        }

        private sealed class Marker : IMarker, IAsyncDisposable
        {
            public int DisposeCount { get; private set; }

            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                return default;
            }
        }

        private sealed class RecordingProcedure : ProcedureBase
        {
            private readonly CallbackTracker _tracker;

            public RecordingProcedure(
                string id,
                CallbackTracker tracker = null)
            {
                Id = id;
                _tracker = tracker;
            }

            public override string Id { get; }
            public Func<ProcedureContext, CancellationToken, Task> Enter
                { get; set; } =
                (context, token) => Task.CompletedTask;
            public Action<ProcedureContext, float> UpdateAction
                { get; set; } =
                (context, deltaTime) => { };
            public Func<ProcedureContext, CancellationToken, Task> Exit
                { get; set; } =
                (context, token) => Task.CompletedTask;
            public int EnterCount { get; private set; }
            public int UpdateCount { get; private set; }
            public int ExitCount { get; private set; }

            public override async ValueTask EnterAsync(
                ProcedureContext context,
                CancellationToken token)
            {
                EnterCount++;
                _tracker?.Enter();
                try
                {
                    await Enter(context, token);
                }
                finally
                {
                    _tracker?.Leave();
                }
            }

            public override void Update(
                ProcedureContext context,
                float deltaTime)
            {
                UpdateCount++;
                _tracker?.Enter();
                try
                {
                    UpdateAction(context, deltaTime);
                }
                finally
                {
                    _tracker?.Leave();
                }
            }

            public override async ValueTask ExitAsync(
                ProcedureContext context,
                CancellationToken token)
            {
                ExitCount++;
                _tracker?.Enter();
                try
                {
                    await Exit(context, token);
                }
                finally
                {
                    _tracker?.Leave();
                }
            }
        }

        private sealed class CallbackTracker
        {
            private int _active;

            public int MaximumConcurrent { get; private set; }

            public void Enter()
            {
                _active++;
                MaximumConcurrent = Math.Max(MaximumConcurrent, _active);
            }

            public void Leave()
            {
                _active--;
            }
        }

        private sealed class StubModule : IFrameworkModule
        {
            public StubModule(string id)
            {
                Id = id;
            }

            public string Id { get; }
            public IReadOnlyCollection<string> Dependencies =>
                Array.Empty<string>();

            public ValueTask InitializeAsync(
                ModuleContext context,
                CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return default;
            }

            public ValueTask StartAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return default;
            }

            public ValueTask StopAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return default;
            }

            public ValueTask DisposeAsync()
            {
                return default;
            }
        }

        private sealed class TestEnterException : Exception
        {
        }

        private sealed class TestRollbackException : Exception
        {
        }

        private sealed class TestExitException : Exception
        {
        }

        private sealed class TestUpdateException : Exception
        {
        }
    }
}
