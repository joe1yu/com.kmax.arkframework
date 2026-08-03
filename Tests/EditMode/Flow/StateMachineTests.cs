using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace ArkFramework.Tests
{
    public sealed class StateMachineTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [Test]
        public void ConstructorAndRegistrationValidateIdsAndCapacity()
        {
            var context = new TestContext();

            Assert.Throws<ArgumentException>(
                () => new StateMachine<TestContext>(" ", context));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new StateMachine<TestContext>("machine", context, 0));

            var machine = new StateMachine<TestContext>("machine", context);
            Assert.Throws<ArgumentException>(
                () => machine.RegisterState("", new RecordingState()));
            Assert.Throws<ArgumentNullException>(
                () => machine.RegisterState("A", null));

            machine.RegisterState("A", new RecordingState());
            Assert.Throws<InvalidOperationException>(
                () => machine.RegisterState("A", new RecordingState()));
            Assert.Throws<ArgumentException>(
                () => machine.RegisterTransition(
                    new StateTransition<TestContext>("A", " ", "A")));
        }

        [UnityTest]
        public IEnumerator StartPublishesOnlyAfterEnterAndCanRetryFailure()
        {
            return RunAsync(StartPublishesOnlyAfterEnterAndCanRetryFailureAsync());
        }

        private async Task StartPublishesOnlyAfterEnterAndCanRetryFailureAsync()
        {
            var context = new TestContext();
            var enterGate = NewGate();
            var attempts = 0;
            var state = new RecordingState
            {
                Enter = (ignored, token) =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        return Task.FromException(new TestEnterException());
                    }

                    return enterGate.Task;
                }
            };
            var machine = CreateMachine(context, ("A", state));

            var firstFailure = await AssertThrowsAsync<TestEnterException>(
                () => machine.StartAsync("A").AsTask());
            Assert.That(machine.CurrentStateId, Is.Null);
            Assert.That(machine.History, Is.Empty);
            Assert.That(
                machine.GetDiagnostics().LastException,
                Is.SameAs(firstFailure));

            var start = machine.StartAsync("A").AsTask();
            machine.Update(0.25f);
            Assert.That(machine.CurrentStateId, Is.Null);
            Assert.That(state.UpdateCount, Is.Zero);

            enterGate.SetResult(true);
            await AwaitWithin(start);

            Assert.That(machine.CurrentStateId, Is.EqualTo("A"));
            Assert.That(machine.History.Count, Is.EqualTo(1));
            Assert.That(machine.History[0].From, Is.Null);
            Assert.That(machine.History[0].Trigger, Is.EqualTo("Start"));

            machine.Update(0.25f);
            Assert.That(state.UpdateCount, Is.EqualTo(1));
            await AssertThrowsAsync<InvalidOperationException>(
                () => machine.StartAsync("A").AsTask());
            Assert.Throws<InvalidOperationException>(
                () => machine.RegisterState("B", new RecordingState()));

            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator GuardFalseSkipsCallbacksAndFirstPassingGuardWins()
        {
            return RunAsync(GuardFalseSkipsCallbacksAndFirstPassingGuardWinsAsync());
        }

        private async Task GuardFalseSkipsCallbacksAndFirstPassingGuardWinsAsync()
        {
            var context = new TestContext();
            var a = new RecordingState();
            var b = new RecordingState();
            var c = new RecordingState();
            var guards = new List<string>();
            var machine = CreateMachine(
                context,
                ("A", a),
                ("B", b),
                ("C", c));
            machine.RegisterTransition(
                new StateTransition<TestContext>(
                    "A",
                    "go",
                    "B",
                    ignored =>
                    {
                        guards.Add("first");
                        return false;
                    }));
            machine.RegisterTransition(
                new StateTransition<TestContext>(
                    "A",
                    "go",
                    "C",
                    ignored =>
                    {
                        guards.Add("second");
                        return true;
                    }));

            await machine.StartAsync("A");
            await machine.FireAsync("go");

            Assert.That(guards, Is.EqualTo(new[] { "first", "second" }));
            Assert.That(machine.CurrentStateId, Is.EqualTo("C"));
            Assert.That(a.ExitCount, Is.EqualTo(1));
            Assert.That(b.EnterCount, Is.Zero);
            Assert.That(c.EnterCount, Is.EqualTo(1));
            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator AllFalseGuardsAreANoOpAndGuardFailureDoesNotPoisonQueue()
        {
            return RunAsync(
                AllFalseGuardsAreANoOpAndGuardFailureDoesNotPoisonQueueAsync());
        }

        private async Task
            AllFalseGuardsAreANoOpAndGuardFailureDoesNotPoisonQueueAsync()
        {
            var context = new TestContext();
            var a = new RecordingState();
            var b = new RecordingState();
            var machine = CreateMachine(context, ("A", a), ("B", b));
            var throwGuard = true;
            machine.RegisterTransition(
                new StateTransition<TestContext>(
                    "A",
                    "go",
                    "B",
                    ignored =>
                    {
                        if (throwGuard)
                        {
                            throw new TestGuardException();
                        }

                        return false;
                    }));

            await machine.StartAsync("A");
            await AssertThrowsAsync<TestGuardException>(
                () => machine.FireAsync("go").AsTask());
            var failureDiagnostics = machine.GetDiagnostics();
            Assert.That(failureDiagnostics.IsFaulted, Is.False);
            Assert.That(
                failureDiagnostics.LastException,
                Is.TypeOf<TestGuardException>());

            throwGuard = false;
            await machine.FireAsync("go");

            Assert.That(machine.CurrentStateId, Is.EqualTo("A"));
            Assert.That(a.ExitCount, Is.Zero);
            Assert.That(b.EnterCount, Is.Zero);
            Assert.That(machine.History.Count, Is.EqualTo(1));
            Assert.That(machine.IsFaulted, Is.False);
            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator MissingTriggerHasMachineStateAndTriggerDiagnostics()
        {
            return RunAsync(MissingTriggerHasMachineStateAndTriggerDiagnosticsAsync());
        }

        private async Task MissingTriggerHasMachineStateAndTriggerDiagnosticsAsync()
        {
            var machine = CreateMachine(
                new TestContext(),
                ("A", new RecordingState()));
            await machine.StartAsync("A");

            var exception = await AssertThrowsAsync<InvalidOperationException>(
                () => machine.FireAsync("missing").AsTask());

            Assert.That(exception.Message, Does.Contain("machine"));
            Assert.That(exception.Message, Does.Contain("A"));
            Assert.That(exception.Message, Does.Contain("missing"));
            Assert.That(machine.IsFaulted, Is.False);
            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator ConcurrentFireUsesExplicitFifoWithoutCallbackOverlap()
        {
            return RunAsync(
                ConcurrentFireUsesExplicitFifoWithoutCallbackOverlapAsync());
        }

        private async Task ConcurrentFireUsesExplicitFifoWithoutCallbackOverlapAsync()
        {
            var context = new TestContext();
            var log = new List<string>();
            var enterGate = NewGate();
            var counters = new CallbackCounters();
            var a = SerialState("A", log, null, counters);
            var b = SerialState(
                "B",
                log,
                enterGate.Task,
                counters);
            var c = SerialState("C", log, null, counters);
            var machine = CreateMachine(context, ("A", a), ("B", b), ("C", c));
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "first", "B"));
            machine.RegisterTransition(
                new StateTransition<TestContext>("B", "second", "C"));

            await machine.StartAsync("A");
            log.Clear();

            var first = machine.FireAsync("first").AsTask();
            var second = machine.FireAsync("second").AsTask();
            await YieldUntil(() => log.Contains("B.Enter.begin"));

            Assert.That(first.IsCompleted, Is.False);
            Assert.That(second.IsCompleted, Is.False);
            Assert.That(log, Does.Not.Contain("B.Exit.begin"));
            var activeDiagnostics = machine.GetDiagnostics();
            Assert.That(activeDiagnostics.IsTransitioning, Is.True);
            Assert.That(activeDiagnostics.QueuedRequestCount, Is.EqualTo(1));
            Assert.That(activeDiagnostics.History.Count, Is.EqualTo(1));

            enterGate.SetResult(true);
            await AwaitWithin(Task.WhenAll(first, second));

            Assert.That(
                log,
                Is.EqualTo(new[]
                {
                    "A.Exit.begin",
                    "A.Exit.end",
                    "B.Enter.begin",
                    "B.Enter.end",
                    "B.Exit.begin",
                    "B.Exit.end",
                    "C.Enter.begin",
                    "C.Enter.end"
                }));
            Assert.That(counters.Maximum, Is.EqualTo(1));
            Assert.That(machine.CurrentStateId, Is.EqualTo("C"));
            Assert.That(activeDiagnostics.History.Count, Is.EqualTo(1));
            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator EnterFailureRollsBackAndPropagatesOriginalFailure()
        {
            return RunAsync(EnterFailureRollsBackAndPropagatesOriginalFailureAsync());
        }

        private async Task EnterFailureRollsBackAndPropagatesOriginalFailureAsync()
        {
            var a = new RecordingState();
            var b = new RecordingState
            {
                Enter = (ignored, token) =>
                    Task.FromException(new TestEnterException())
            };
            var machine = CreateMachine(
                new TestContext(),
                ("A", a),
                ("B", b));
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "go", "B"));

            await machine.StartAsync("A");
            var exception = await AssertThrowsAsync<TestEnterException>(
                () => machine.FireAsync("go").AsTask());

            Assert.That(exception, Is.TypeOf<TestEnterException>());
            Assert.That(a.EnterCount, Is.EqualTo(2));
            Assert.That(a.ExitCount, Is.EqualTo(1));
            Assert.That(machine.CurrentStateId, Is.EqualTo("A"));
            Assert.That(machine.IsFaulted, Is.False);
            Assert.That(machine.History.Count, Is.EqualTo(1));
            Assert.That(
                machine.GetDiagnostics().LastException,
                Is.SameAs(exception));
            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator RollbackFailureFaultsMachineAndStopsUpdates()
        {
            return RunAsync(RollbackFailureFaultsMachineAndStopsUpdatesAsync());
        }

        private async Task RollbackFailureFaultsMachineAndStopsUpdatesAsync()
        {
            var aEnterCount = 0;
            var a = new RecordingState
            {
                Enter = (ignored, token) =>
                {
                    aEnterCount++;
                    return aEnterCount == 1
                        ? Task.CompletedTask
                        : Task.FromException(new TestRollbackException());
                }
            };
            var b = new RecordingState
            {
                Enter = (ignored, token) =>
                    Task.FromException(new TestEnterException())
            };
            var machine = CreateMachine(
                new TestContext(),
                ("A", a),
                ("B", b));
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "go", "B"));

            await machine.StartAsync("A");
            var exception = await AssertThrowsAsync<AggregateException>(
                () => machine.FireAsync("go").AsTask());

            Assert.That(
                exception.InnerExceptions.Any(item => item is TestEnterException),
                Is.True);
            Assert.That(
                exception.InnerExceptions.Any(item => item is TestRollbackException),
                Is.True);
            Assert.That(machine.IsFaulted, Is.True);
            machine.Update(1f);
            Assert.That(a.UpdateCount, Is.Zero);
            var fault = await AssertThrowsAsync<InvalidOperationException>(
                () => machine.FireAsync("go").AsTask());
            Assert.That(fault.Message, Does.Contain("machine"));
            Assert.That(fault.InnerException, Is.SameAs(exception));
            Assert.That(
                machine.GetDiagnostics().LastException,
                Is.SameAs(exception));
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var startFault = await AssertThrowsAsync<InvalidOperationException>(
                () => machine.StartAsync("A", cancellation.Token).AsTask());
            Assert.That(startFault.InnerException, Is.SameAs(exception));
            var preCanceledFireFault =
                await AssertThrowsAsync<InvalidOperationException>(
                    () => machine.FireAsync(
                        "go",
                        cancellation.Token).AsTask());
            Assert.That(preCanceledFireFault.Message, Does.Contain("machine"));
            Assert.That(preCanceledFireFault.InnerException, Is.SameAs(exception));
            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator ExitAndUpdateFailuresFaultOnlyTheirMachine()
        {
            return RunAsync(ExitAndUpdateFailuresFaultOnlyTheirMachineAsync());
        }

        private async Task ExitAndUpdateFailuresFaultOnlyTheirMachineAsync()
        {
            var exitState = new RecordingState
            {
                Exit = (ignored, token) =>
                    Task.FromException(new TestExitException())
            };
            var target = new RecordingState();
            var exitMachine = CreateMachine(
                new TestContext(),
                ("A", exitState),
                ("B", target));
            exitMachine.RegisterTransition(
                new StateTransition<TestContext>("A", "go", "B"));
            var healthyState = new RecordingState();
            var healthyMachine = CreateMachine(
                new TestContext(),
                ("Healthy", healthyState));

            await exitMachine.StartAsync("A");
            await healthyMachine.StartAsync("Healthy");
            await AssertThrowsAsync<TestExitException>(
                () => exitMachine.FireAsync("go").AsTask());

            Assert.That(exitMachine.IsFaulted, Is.True);
            Assert.That(exitMachine.CurrentStateId, Is.EqualTo("A"));
            Assert.That(target.EnterCount, Is.Zero);
            healthyMachine.Update(1f);
            Assert.That(healthyState.UpdateCount, Is.EqualTo(1));

            var updateMachine = CreateMachine(
                new TestContext(),
                ("Update", new RecordingState
                {
                    UpdateAction =
                        (ignored, delta) => throw new TestUpdateException()
                }));
            await updateMachine.StartAsync("Update");
            Assert.Throws<TestUpdateException>(() => updateMachine.Update(1f));
            Assert.That(updateMachine.IsFaulted, Is.True);
            Assert.DoesNotThrow(() => updateMachine.Update(1f));

            await AssertThrowsAsync<TestExitException>(
                () => exitMachine.DisposeAsync().AsTask());
            await healthyMachine.DisposeAsync();
            await updateMachine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator ExitAndRollbackFaultsGiveQueuedRequestsTheRootCause()
        {
            return RunAsync(
                ExitAndRollbackFaultsGiveQueuedRequestsTheRootCauseAsync());
        }

        private async Task
            ExitAndRollbackFaultsGiveQueuedRequestsTheRootCauseAsync()
        {
            var exitGate = NewGate();
            var exitRoot = new TestExitException();
            var exitState = new RecordingState
            {
                Exit = async (ignored, token) =>
                {
                    await exitGate.Task;
                    throw exitRoot;
                }
            };
            var exitMachine = CreateMachine(
                new TestContext(),
                ("A", exitState),
                ("B", new RecordingState()));
            exitMachine.RegisterTransition(
                new StateTransition<TestContext>("A", "fail", "B"));
            await exitMachine.StartAsync("A");

            var activeExit = exitMachine.FireAsync("fail").AsTask();
            await YieldUntil(() => exitState.ExitCount == 1);
            var queuedAfterExit = exitMachine.FireAsync("fail").AsTask();
            exitGate.SetResult(true);

            var observedExit = await AssertThrowsAsync<TestExitException>(
                () => activeExit);
            var queuedExit = await AssertThrowsAsync<InvalidOperationException>(
                () => queuedAfterExit);
            Assert.That(observedExit, Is.SameAs(exitRoot));
            Assert.That(queuedExit.InnerException, Is.SameAs(exitRoot));

            exitState.Exit = (ignored, token) => Task.CompletedTask;
            await exitMachine.DisposeAsync();

            var enterGate = NewGate();
            var enterRoot = new TestEnterException();
            var rollbackRoot = new TestRollbackException();
            var rollbackAttempts = 0;
            var rollbackState = new RecordingState
            {
                Enter = (ignored, token) =>
                {
                    rollbackAttempts++;
                    return rollbackAttempts == 1
                        ? Task.CompletedTask
                        : Task.FromException(rollbackRoot);
                }
            };
            var targetState = new RecordingState
            {
                Enter = async (ignored, token) =>
                {
                    await enterGate.Task;
                    throw enterRoot;
                }
            };
            var rollbackMachine = CreateMachine(
                new TestContext(),
                ("A", rollbackState),
                ("B", targetState));
            rollbackMachine.RegisterTransition(
                new StateTransition<TestContext>("A", "fail", "B"));
            await rollbackMachine.StartAsync("A");

            var activeRollback = rollbackMachine.FireAsync("fail").AsTask();
            await YieldUntil(() => targetState.EnterCount == 1);
            var queuedAfterRollback =
                rollbackMachine.FireAsync("fail").AsTask();
            enterGate.SetResult(true);

            var combined = await AssertThrowsAsync<AggregateException>(
                () => activeRollback);
            var queuedRollback =
                await AssertThrowsAsync<InvalidOperationException>(
                    () => queuedAfterRollback);
            Assert.That(
                combined.InnerExceptions,
                Has.Some.SameAs(enterRoot));
            Assert.That(
                combined.InnerExceptions,
                Has.Some.SameAs(rollbackRoot));
            Assert.That(queuedRollback.InnerException, Is.SameAs(combined));
            await rollbackMachine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator QueuedCancellationSkipsOnlyThatRequest()
        {
            return RunAsync(QueuedCancellationSkipsOnlyThatRequestAsync());
        }

        private async Task QueuedCancellationSkipsOnlyThatRequestAsync()
        {
            var gate = NewGate();
            var a = new RecordingState();
            var b = new RecordingState { Enter = (ignored, token) => gate.Task };
            var c = new RecordingState();
            var machine = CreateMachine(
                new TestContext(),
                ("A", a),
                ("B", b),
                ("C", c));
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "first", "B"));
            machine.RegisterTransition(
                new StateTransition<TestContext>("B", "skip", "A"));
            machine.RegisterTransition(
                new StateTransition<TestContext>("B", "last", "C"));
            await machine.StartAsync("A");

            var first = machine.FireAsync("first").AsTask();
            var cancellation = new CancellationTokenSource();
            var skipped = machine.FireAsync("skip", cancellation.Token).AsTask();
            var last = machine.FireAsync("last").AsTask();
            cancellation.Cancel();
            gate.SetResult(true);

            await AwaitWithin(first);
            await AssertThrowsAsync<TaskCanceledException>(() => skipped);
            await AwaitWithin(last);
            Assert.That(machine.CurrentStateId, Is.EqualTo("C"));
            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator PreCanceledStartAndFireNeverEnterCallbacksOrFifo()
        {
            return RunAsync(
                PreCanceledStartAndFireNeverEnterCallbacksOrFifoAsync());
        }

        private async Task
            PreCanceledStartAndFireNeverEnterCallbacksOrFifoAsync()
        {
            var canceledStartState = new RecordingState();
            var canceledStartMachine = CreateMachine(
                new TestContext(),
                ("A", canceledStartState));
            var canceledStartToken = new CancellationTokenSource();
            canceledStartToken.Cancel();

            await AssertThrowsAsync<TaskCanceledException>(
                () => canceledStartMachine
                    .StartAsync("A", canceledStartToken.Token)
                    .AsTask());

            Assert.That(canceledStartState.EnterCount, Is.Zero);
            Assert.That(canceledStartMachine.CurrentStateId, Is.Null);
            Assert.That(
                canceledStartMachine.GetDiagnostics().QueuedRequestCount,
                Is.Zero);
            await canceledStartMachine.DisposeAsync();
            canceledStartToken.Dispose();

            var enterGate = NewGate();
            var a = new RecordingState();
            var b = new RecordingState
            {
                Enter = (ignored, token) => enterGate.Task
            };
            var machine = CreateMachine(
                new TestContext(),
                ("A", a),
                ("B", b));
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "go", "B"));
            await machine.StartAsync("A");

            var active = machine.FireAsync("go").AsTask();
            await YieldUntil(() => b.EnterCount == 1);
            Assert.That(
                machine.GetDiagnostics().QueuedRequestCount,
                Is.Zero);

            var canceledFireToken = new CancellationTokenSource();
            canceledFireToken.Cancel();
            var canceled = machine
                .FireAsync("never-queued", canceledFireToken.Token)
                .AsTask();
            await AssertThrowsAsync<TaskCanceledException>(() => canceled);
            Assert.That(
                machine.GetDiagnostics().QueuedRequestCount,
                Is.Zero);

            enterGate.SetResult(true);
            await AwaitWithin(active);
            await machine.DisposeAsync();
            canceledFireToken.Dispose();
        }

        [UnityTest]
        public IEnumerator CancellationAfterExitRollsBackBeforeNextRequest()
        {
            return RunAsync(CancellationAfterExitRollsBackBeforeNextRequestAsync());
        }

        private async Task CancellationAfterExitRollsBackBeforeNextRequestAsync()
        {
            var cancellation = new CancellationTokenSource();
            var a = new RecordingState
            {
                Exit = (ignored, token) =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                }
            };
            var b = new RecordingState
            {
                Enter = async (ignored, token) =>
                {
                    await Task.Yield();
                    token.ThrowIfCancellationRequested();
                }
            };
            var c = new RecordingState();
            var machine = CreateMachine(
                new TestContext(),
                ("A", a),
                ("B", b),
                ("C", c));
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "cancel", "B"));
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "next", "C"));
            await machine.StartAsync("A");

            var canceled = machine.FireAsync(
                "cancel",
                cancellation.Token).AsTask();
            var next = machine.FireAsync("next").AsTask();

            await AssertThrowsAsync<TaskCanceledException>(() => canceled);
            await AwaitWithin(next);
            Assert.That(a.EnterCount, Is.EqualTo(2));
            Assert.That(machine.CurrentStateId, Is.EqualTo("C"));
            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator HistoryIsBoundedSnapshotAndTracksPreviousState()
        {
            return RunAsync(HistoryIsBoundedSnapshotAndTracksPreviousStateAsync());
        }

        private async Task HistoryIsBoundedSnapshotAndTracksPreviousStateAsync()
        {
            var machine = new StateMachine<TestContext>(
                "machine",
                new TestContext(),
                historyCapacity: 2);
            machine.RegisterState("A", new RecordingState());
            machine.RegisterState("B", new RecordingState());
            machine.RegisterState("C", new RecordingState());
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "one", "B"));
            machine.RegisterTransition(
                new StateTransition<TestContext>("B", "two", "C"));

            await machine.StartAsync("A");
            var oldSnapshot = machine.History;
            await machine.FireAsync("one");
            await machine.FireAsync("two");

            Assert.That(oldSnapshot.Count, Is.EqualTo(1));
            Assert.That(machine.History.Count, Is.EqualTo(2));
            Assert.That(
                machine.History.Select(item => item.Trigger),
                Is.EqualTo(new[] { "one", "two" }));
            Assert.That(machine.PreviousStateId, Is.EqualTo("B"));
            Assert.That(machine.CurrentStateId, Is.EqualTo("C"));
            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator DisposeCancelsQueueWaitsForActiveAndExitsOnce()
        {
            return RunAsync(DisposeCancelsQueueWaitsForActiveAndExitsOnceAsync());
        }

        private async Task DisposeCancelsQueueWaitsForActiveAndExitsOnceAsync()
        {
            var enterGate = NewGate();
            var a = new RecordingState();
            var b = new RecordingState
            {
                Enter = async (ignored, token) =>
                {
                    await enterGate.Task;
                    token.ThrowIfCancellationRequested();
                }
            };
            var machine = CreateMachine(
                new TestContext(),
                ("A", a),
                ("B", b));
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "go", "B"));
            machine.RegisterTransition(
                new StateTransition<TestContext>("B", "back", "A"));
            await machine.StartAsync("A");

            var active = machine.FireAsync("go").AsTask();
            await YieldUntil(() => b.EnterCount == 1);
            var queued = machine.FireAsync("back").AsTask();
            var dispose = machine.DisposeAsync().AsTask();

            Assert.That(dispose.IsCompleted, Is.False);
            await AssertThrowsAsync<TaskCanceledException>(() => queued);
            enterGate.SetResult(true);
            await AssertThrowsAsync<TaskCanceledException>(() => active);
            await AwaitWithin(dispose);
            await machine.DisposeAsync();

            Assert.That(a.ExitCount, Is.EqualTo(2));
            Assert.That(b.ExitCount, Is.Zero);
            Assert.Throws<ObjectDisposedException>(
                () =>
                {
                    machine.FireAsync("go");
                });
        }

        [UnityTest]
        public IEnumerator DisposeWaitsForActiveStartAndExitsPublishedStateOnce()
        {
            return RunAsync(
                DisposeWaitsForActiveStartAndExitsPublishedStateOnceAsync());
        }

        private async Task
            DisposeWaitsForActiveStartAndExitsPublishedStateOnceAsync()
        {
            var enterGate = NewGate();
            var state = new RecordingState
            {
                Enter = (ignored, token) => enterGate.Task
            };
            var machine = CreateMachine(
                new TestContext(),
                ("A", state));

            var start = machine.StartAsync("A").AsTask();
            await YieldUntil(() => state.EnterCount == 1);
            var dispose = machine.DisposeAsync().AsTask();

            Assert.That(dispose.IsCompleted, Is.False);
            enterGate.SetResult(true);
            await AwaitWithin(start);
            await AwaitWithin(dispose);

            Assert.That(state.ExitCount, Is.EqualTo(1));
            await machine.DisposeAsync();
            Assert.That(state.ExitCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CallbackReentryFailsFastInsteadOfSelfWaiting()
        {
            return RunAsync(CallbackReentryFailsFastInsteadOfSelfWaitingAsync());
        }

        private async Task CallbackReentryFailsFastInsteadOfSelfWaitingAsync()
        {
            StateMachine<TestContext> machine = null;
            Exception reentryFailure = null;
            var a = new RecordingState();
            var b = new RecordingState
            {
                Enter = (ignored, token) =>
                {
                    reentryFailure = Assert.Throws<InvalidOperationException>(
                        () =>
                        {
                            machine.FireAsync("back");
                        });
                    Assert.Throws<InvalidOperationException>(
                        () =>
                        {
                            machine.DisposeAsync();
                        });
                    return Task.CompletedTask;
                }
            };
            machine = CreateMachine(
                new TestContext(),
                ("A", a),
                ("B", b));
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "go", "B"));
            machine.RegisterTransition(
                new StateTransition<TestContext>("B", "back", "A"));

            await machine.StartAsync("A");
            await AwaitWithin(machine.FireAsync("go").AsTask());

            Assert.That(reentryFailure.Message, Does.Contain("machine"));
            Assert.That(machine.CurrentStateId, Is.EqualTo("B"));
            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator DetachedCallbackContextExpiresWhenCallbackReturns()
        {
            return RunAsync(DetachedCallbackContextExpiresWhenCallbackReturnsAsync());
        }

        private async Task DetachedCallbackContextExpiresWhenCallbackReturnsAsync()
        {
            StateMachine<TestContext> machine = null;
            var releaseDetached = NewGate();
            var detachedCompletion = new TaskCompletionSource<Exception>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var a = new RecordingState();
            var b = new RecordingState
            {
                Enter = (ignored, token) =>
                {
                    _ = Task.Run(
                        async () =>
                        {
                            await releaseDetached.Task;
                            try
                            {
                                await machine.FireAsync("back");
                                detachedCompletion.TrySetResult(null);
                            }
                            catch (Exception exception)
                            {
                                detachedCompletion.TrySetResult(exception);
                            }
                        });
                    return Task.CompletedTask;
                }
            };
            machine = CreateMachine(
                new TestContext(),
                ("A", a),
                ("B", b));
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "go", "B"));
            machine.RegisterTransition(
                new StateTransition<TestContext>("B", "back", "A"));

            await machine.StartAsync("A");
            await machine.FireAsync("go");
            releaseDetached.SetResult(true);

            var detachedFailure = await AwaitWithin(detachedCompletion.Task);
            Assert.That(detachedFailure, Is.Null);
            Assert.That(machine.CurrentStateId, Is.EqualTo("A"));
            await machine.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator ServiceEnforcesGlobalIdsTypedLookupAndIsolation()
        {
            return RunAsync(ServiceEnforcesGlobalIdsTypedLookupAndIsolationAsync());
        }

        private async Task ServiceEnforcesGlobalIdsTypedLookupAndIsolationAsync()
        {
            var service = new FsmService();
            var firstState = new RecordingState
            {
                UpdateAction =
                    (ignored, delta) => throw new TestUpdateException()
            };
            var secondState = new RecordingState();
            var first = service.Create("first", new TestContext());
            var second = service.Create("second", "context");
            first.RegisterState("A", firstState);
            second.RegisterState("B", new StringState(secondState));
            await first.StartAsync("A");
            await second.StartAsync("B");

            Assert.Throws<InvalidOperationException>(
                () => service.Create("first", 42));
            Assert.That(service.Get<TestContext>("first"), Is.SameAs(first));
            Assert.Throws<InvalidOperationException>(
                () => service.Get<string>("first"));
            Assert.That(
                service.TryGet(
                    "first",
                    out IStateMachine<string> wrongType),
                Is.False);
            Assert.That(wrongType, Is.Null);
            Assert.That(service.TryGet("second", out IStateMachine<string> found), Is.True);
            Assert.That(found, Is.SameAs(second));

            Assert.DoesNotThrow(() => service.Update(1f));
            Assert.That(first.IsFaulted, Is.True);
            service.Update(1f);
            Assert.That(secondState.UpdateCount, Is.EqualTo(2));
            Assert.That(service.Diagnostics.Count, Is.EqualTo(2));
            Assert.That(
                service.Diagnostics.Single(item => item.MachineId == "first")
                    .LastException,
                Is.TypeOf<TestUpdateException>());

            await service.RemoveAsync("first");
            Assert.That(service.TryGet("first", out IStateMachine<TestContext> _), Is.False);
            await service.DisposeAsync();
            await service.DisposeAsync();
        }

        [UnityTest]
        public IEnumerator ServiceDisposeContinuesAfterMachineExitFailure()
        {
            return RunAsync(ServiceDisposeContinuesAfterMachineExitFailureAsync());
        }

        private async Task ServiceDisposeContinuesAfterMachineExitFailureAsync()
        {
            var service = new FsmService();
            var failing = service.Create("failing", new TestContext());
            var failingState = new RecordingState
            {
                Exit = (ignored, token) =>
                    Task.FromException(new TestExitException())
            };
            failing.RegisterState("A", failingState);
            await failing.StartAsync("A");

            var healthy = service.Create("healthy", new TestContext());
            var healthyState = new RecordingState();
            healthy.RegisterState("B", healthyState);
            await healthy.StartAsync("B");

            await AssertThrowsAsync<TestExitException>(
                () => service.DisposeAsync().AsTask());

            Assert.That(failingState.ExitCount, Is.EqualTo(1));
            Assert.That(healthyState.ExitCount, Is.EqualTo(1));
            await AssertThrowsAsync<TestExitException>(
                () => service.DisposeAsync().AsTask());
        }

        [UnityTest]
        public IEnumerator ServiceDisposeRejectsSynchronousAndAsynchronousCallbackReentry()
        {
            return RunAsync(
                ServiceDisposeRejectsSynchronousAndAsynchronousCallbackReentryAsync());
        }

        private async Task
            ServiceDisposeRejectsSynchronousAndAsynchronousCallbackReentryAsync()
        {
            var synchronousService = new FsmService();
            var synchronousMachine =
                synchronousService.Create("sync", new TestContext());
            var synchronousGate = NewGate();
            Exception synchronousFailure = null;
            var synchronousState = new RecordingState
            {
                Exit = async (ignored, token) =>
                {
                    try
                    {
                        _ = synchronousService.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        synchronousFailure = exception;
                    }

                    await synchronousGate.Task;
                }
            };
            synchronousMachine.RegisterState("A", synchronousState);
            await synchronousMachine.StartAsync("A");

            var synchronousOuter = synchronousService.DisposeAsync().AsTask();
            var synchronousConcurrent =
                synchronousService.DisposeAsync().AsTask();
            var synchronousSameTask =
                ReferenceEquals(synchronousOuter, synchronousConcurrent);
            var synchronousRegistryCount =
                synchronousService.Diagnostics.Count;
            synchronousGate.SetResult(true);
            await AwaitWithin(synchronousOuter);

            Assert.That(
                synchronousFailure,
                Is.TypeOf<InvalidOperationException>());
            Assert.That(synchronousSameTask, Is.True);
            Assert.That(synchronousRegistryCount, Is.EqualTo(1));
            Assert.That(synchronousState.ExitCount, Is.EqualTo(1));

            var asynchronousService = new FsmService();
            var asynchronousMachine =
                asynchronousService.Create("async", new TestContext());
            Exception asynchronousFailure = null;
            var asynchronousState = new RecordingState
            {
                Exit = async (ignored, token) =>
                {
                    await Task.Yield();
                    try
                    {
                        _ = asynchronousService.DisposeAsync();
                    }
                    catch (Exception exception)
                    {
                        asynchronousFailure = exception;
                    }
                }
            };
            asynchronousMachine.RegisterState("A", asynchronousState);
            await asynchronousMachine.StartAsync("A");

            await AwaitWithin(asynchronousService.DisposeAsync().AsTask());

            Assert.That(
                asynchronousFailure,
                Is.TypeOf<InvalidOperationException>());
            Assert.That(asynchronousState.ExitCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ServiceDisposeFirstCalledFromCallbackKeepsOwnership()
        {
            return RunAsync(
                ServiceDisposeFirstCalledFromCallbackKeepsOwnershipAsync());
        }

        private async Task
            ServiceDisposeFirstCalledFromCallbackKeepsOwnershipAsync()
        {
            var service = new FsmService();
            var machine = service.Create("machine", new TestContext());
            Exception callbackFailure = null;
            Task returnedDisposal = null;
            var initialState = new RecordingState();
            var targetState = new RecordingState
            {
                Enter = (ignored, token) =>
                {
                    try
                    {
                        returnedDisposal = service.DisposeAsync().AsTask();
                    }
                    catch (Exception exception)
                    {
                        callbackFailure = exception;
                    }

                    return Task.CompletedTask;
                }
            };
            machine.RegisterState("A", initialState);
            machine.RegisterState("B", targetState);
            machine.RegisterTransition(
                new StateTransition<TestContext>("A", "go", "B"));
            await machine.StartAsync("A");

            await machine.FireAsync("go");
            if (returnedDisposal != null)
            {
                await AssertThrowsAsync<InvalidOperationException>(
                    () => returnedDisposal);
            }

            var remainedOwned =
                service.TryGet(
                    "machine",
                    out IStateMachine<TestContext> found);
            if (remainedOwned)
            {
                await service.DisposeAsync();
            }
            else
            {
                await machine.DisposeAsync();
            }

            Assert.That(
                callbackFailure,
                Is.TypeOf<InvalidOperationException>());
            Assert.That(remainedOwned, Is.True);
            Assert.That(found, Is.SameAs(machine));
            Assert.That(initialState.ExitCount, Is.EqualTo(1));
            Assert.That(targetState.ExitCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RemoveFromCallbackFailureKeepsMachineOwned()
        {
            return RunAsync(RemoveFromCallbackFailureKeepsMachineOwnedAsync());
        }

        private async Task RemoveFromCallbackFailureKeepsMachineOwnedAsync()
        {
            foreach (var callback in new[] { "Enter", "Exit", "Guard", "Update" })
            {
                await AssertRemoveCallbackKeepsMachineOwnedAsync(callback);
            }
        }

        private static async Task AssertRemoveCallbackKeepsMachineOwnedAsync(
            string callback)
        {
            var service = new FsmService();
            var machine = service.Create(callback, new TestContext());
            Exception callbackFailure = null;
            Action removeFromCallback = () =>
            {
                try
                {
                    _ = service.RemoveAsync(callback);
                }
                catch (Exception exception)
                {
                    callbackFailure = exception;
                }
            };

            var initialState = new RecordingState();
            var targetState = new RecordingState();
            if (callback == "Enter")
            {
                initialState.Enter = (ignored, token) =>
                {
                    removeFromCallback();
                    return Task.CompletedTask;
                };
            }
            else if (callback == "Exit")
            {
                initialState.Exit = (ignored, token) =>
                {
                    removeFromCallback();
                    return Task.CompletedTask;
                };
            }
            else if (callback == "Update")
            {
                initialState.UpdateAction =
                    (ignored, delta) => removeFromCallback();
            }

            machine.RegisterState("A", initialState);
            machine.RegisterState("B", targetState);
            machine.RegisterTransition(
                new StateTransition<TestContext>(
                    "A",
                    "go",
                    "B",
                    callback == "Guard"
                        ? new Func<TestContext, bool>(ignored =>
                        {
                            removeFromCallback();
                            return true;
                        })
                        : null));
            await machine.StartAsync("A");

            if (callback == "Exit" || callback == "Guard")
            {
                await machine.FireAsync("go");
            }
            else if (callback == "Update")
            {
                service.Update(1f);
            }

            var remainedOwned =
                service.TryGet(
                    callback,
                    out IStateMachine<TestContext> found);
            if (remainedOwned)
            {
                await service.RemoveAsync(callback);
            }
            else
            {
                await machine.DisposeAsync();
            }

            await service.DisposeAsync();

            Assert.That(
                callbackFailure,
                Is.TypeOf<InvalidOperationException>());
            Assert.That(remainedOwned, Is.True);
            Assert.That(found, Is.SameAs(machine));
            Assert.That(
                callback == "Exit" || callback == "Guard"
                    ? targetState.ExitCount
                    : initialState.ExitCount,
                Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator RemoveFailureRestoresOwnershipAndReservesId()
        {
            return RunAsync(RemoveFailureRestoresOwnershipAndReservesIdAsync());
        }

        private async Task RemoveFailureRestoresOwnershipAndReservesIdAsync()
        {
            var service = new FsmService();
            var machine = service.Create("machine", new TestContext());
            var exitGate = NewGate();
            var exitRoot = new TestExitException();
            var state = new RecordingState
            {
                Exit = async (ignored, token) =>
                {
                    await exitGate.Task;
                    throw exitRoot;
                }
            };
            machine.RegisterState("A", state);
            await machine.StartAsync("A");

            var remove = service.RemoveAsync("machine").AsTask();
            await YieldUntil(() => state.ExitCount == 1);

            Exception duplicateFailure = null;
            IStateMachine<TestContext> replacement = null;
            try
            {
                replacement = service.Create("machine", new TestContext());
            }
            catch (Exception exception)
            {
                duplicateFailure = exception;
            }

            exitGate.SetResult(true);
            var removeFailure = await AssertThrowsAsync<TestExitException>(
                () => remove);
            var remainedOwned = service.TryGet(
                "machine",
                out IStateMachine<TestContext> found);

            Exception disposalFailure = null;
            try
            {
                await service.DisposeAsync();
            }
            catch (Exception exception)
            {
                disposalFailure = exception;
            }

            Assert.That(
                duplicateFailure,
                Is.TypeOf<InvalidOperationException>());
            Assert.That(replacement, Is.Null);
            Assert.That(removeFailure, Is.SameAs(exitRoot));
            Assert.That(remainedOwned, Is.True);
            Assert.That(found, Is.SameAs(machine));
            Assert.That(disposalFailure, Is.SameAs(exitRoot));
            Assert.That(state.ExitCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ModuleRegistersScopeOwnedServiceAndForwardsOnlyWhileStarted()
        {
            return RunAsync(
                ModuleRegistersScopeOwnedServiceAndForwardsOnlyWhileStartedAsync());
        }

        private async Task
            ModuleRegistersScopeOwnedServiceAndForwardsOnlyWhileStartedAsync()
        {
            var runtime = new FrameworkRuntime();
            var module = new FsmModule();
            var descriptors = new[]
            {
                new ModuleDescriptor(
                    "FSM",
                    Array.Empty<string>(),
                    0,
                    () => module)
            };

            Assert.DoesNotThrow(() => module.Update(1f));
            await runtime.StartAsync(descriptors, CancellationToken.None);
            var service = runtime.Services.Resolve<IFsmService>();
            var state = new RecordingState();
            var machine = service.Create("module-machine", new TestContext());
            machine.RegisterState("A", state);
            await machine.StartAsync("A");

            runtime.Update(1f);
            Assert.That(state.UpdateCount, Is.EqualTo(1));

            await runtime.StopAsync(CancellationToken.None);
            module.Update(1f);
            runtime.Update(1f);
            Assert.That(state.UpdateCount, Is.EqualTo(1));
            Assert.That(state.ExitCount, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(
                () => service.Create("after-stop", new TestContext()));
            Assert.That(module.Id, Is.EqualTo("FSM"));
            Assert.That(module.Dependencies, Is.Empty);
            await runtime.DisposeAsync();
            Assert.That(state.ExitCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ModulePreCanceledStopStillWaitsForMachineCleanup()
        {
            return RunAsync(ModulePreCanceledStopStillWaitsForMachineCleanupAsync());
        }

        private async Task ModulePreCanceledStopStillWaitsForMachineCleanupAsync()
        {
            var runtime = new FrameworkRuntime();
            var module = new FsmModule();
            var descriptors = new[]
            {
                new ModuleDescriptor(
                    "FSM",
                    Array.Empty<string>(),
                    0,
                    () => module)
            };
            await runtime.StartAsync(descriptors, CancellationToken.None);
            var service = runtime.Services.Resolve<IFsmService>();
            var exitGate = NewGate();
            var state = new RecordingState
            {
                Exit = (ignored, token) => exitGate.Task
            };
            var machine = service.Create("cancel-stop", new TestContext());
            machine.RegisterState("A", state);
            await machine.StartAsync("A");
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Task stop;
            try
            {
                stop = module.StopAsync(cancellation.Token).AsTask();
            }
            catch (Exception exception)
            {
                stop = Task.FromException(exception);
            }

            Assert.That(
                stop.IsCompleted,
                Is.False,
                "A pre-canceled token must not bypass active FSM cleanup.");
            await YieldUntil(() => state.ExitCount == 1);
            exitGate.SetResult(true);
            try
            {
                await AwaitWithin(stop);
            }
            catch (OperationCanceledException)
            {
                // Cancellation may be reported only after mandatory cleanup.
            }

            Assert.That(state.ExitCount, Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(
                () => service.Create("after-canceled-stop", new TestContext()));
            await runtime.DisposeAsync();
            Assert.That(state.ExitCount, Is.EqualTo(1));
        }

        private static StateMachine<TestContext> CreateMachine(
            TestContext context,
            params (string Id, IState<TestContext> State)[] states)
        {
            var machine = new StateMachine<TestContext>("machine", context);
            foreach (var state in states)
            {
                machine.RegisterState(state.Id, state.State);
            }

            return machine;
        }

        private static RecordingState SerialState(
            string id,
            ICollection<string> log,
            Task enterGate,
            CallbackCounters counters)
        {
            return new RecordingState
            {
                Enter = async (ignored, token) =>
                {
                    counters.Begin();
                    log.Add($"{id}.Enter.begin");
                    try
                    {
                        if (enterGate != null)
                        {
                            await enterGate;
                        }

                        log.Add($"{id}.Enter.end");
                    }
                    finally
                    {
                        counters.End();
                    }
                },
                Exit = (ignored, token) =>
                {
                    counters.Begin();
                    log.Add($"{id}.Exit.begin");
                    log.Add($"{id}.Exit.end");
                    counters.End();
                    return Task.CompletedTask;
                }
            };
        }

        private static TaskCompletionSource<bool> NewGate()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static IEnumerator RunAsync(Task task)
        {
            var timeoutAt = DateTime.UtcNow + Timeout;
            while (!task.IsCompleted)
            {
                Assert.That(
                    DateTime.UtcNow,
                    Is.LessThan(timeoutAt),
                    "Asynchronous test timed out.");
                yield return null;
            }

            if (task.IsCanceled)
            {
                throw new TaskCanceledException(task);
            }

            if (task.IsFaulted)
            {
                throw task.Exception.InnerException;
            }
        }

        private static async Task AwaitWithin(Task task)
        {
            var timeout = Task.Delay(Timeout);
            var completed = await Task.WhenAny(task, timeout);
            Assert.That(completed, Is.SameAs(task), "Asynchronous operation timed out.");
            await task;
        }

        private static async Task<T> AwaitWithin<T>(Task<T> task)
        {
            var timeout = Task.Delay(Timeout);
            var completed = await Task.WhenAny(task, timeout);
            Assert.That(completed, Is.SameAs(task), "Asynchronous operation timed out.");
            return await task;
        }

        private static async Task<TException> AssertThrowsAsync<TException>(
            Func<Task> action)
            where TException : Exception
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                Assert.That(exception, Is.TypeOf<TException>());
                return (TException)exception;
            }

            Assert.Fail($"Expected {typeof(TException).Name}.");
            return null;
        }

        private static async Task YieldUntil(Func<bool> predicate)
        {
            var timeoutAt = DateTime.UtcNow + Timeout;
            while (!predicate())
            {
                Assert.That(
                    DateTime.UtcNow,
                    Is.LessThan(timeoutAt),
                    "Expected asynchronous phase was not reached.");
                await Task.Yield();
            }
        }

        private sealed class RecordingState : IState<TestContext>
        {
            public Func<TestContext, CancellationToken, Task> Enter { get; set; } =
                (context, token) => Task.CompletedTask;
            public Action<TestContext, float> UpdateAction { get; set; } =
                (context, delta) => { };
            public Func<TestContext, CancellationToken, Task> Exit { get; set; } =
                (context, token) => Task.CompletedTask;

            public int EnterCount { get; private set; }
            public int UpdateCount { get; private set; }
            public int ExitCount { get; private set; }

            public ValueTask EnterAsync(
                TestContext context,
                CancellationToken token)
            {
                EnterCount++;
                return new ValueTask(Enter(context, token));
            }

            public void Update(TestContext context, float deltaTime)
            {
                UpdateCount++;
                UpdateAction(context, deltaTime);
            }

            public ValueTask ExitAsync(
                TestContext context,
                CancellationToken token)
            {
                ExitCount++;
                return new ValueTask(Exit(context, token));
            }
        }

        private sealed class StringState : IState<string>
        {
            private readonly RecordingState _inner;

            public StringState(RecordingState inner)
            {
                _inner = inner;
            }

            public ValueTask EnterAsync(string context, CancellationToken token)
            {
                return _inner.EnterAsync(null, token);
            }

            public void Update(string context, float deltaTime)
            {
                _inner.Update(null, deltaTime);
            }

            public ValueTask ExitAsync(string context, CancellationToken token)
            {
                return _inner.ExitAsync(null, token);
            }
        }

        private sealed class CallbackCounters
        {
            private int _active;

            public int Maximum { get; private set; }

            public void Begin()
            {
                _active++;
                Maximum = Math.Max(Maximum, _active);
            }

            public void End()
            {
                _active--;
            }
        }

        private sealed class TestContext
        {
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

        private sealed class TestGuardException : Exception
        {
        }
    }
}
