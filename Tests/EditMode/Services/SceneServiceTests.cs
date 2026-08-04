using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace ArkFramework.Tests
{
    public sealed class SceneServiceTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private FakeSceneBackend _backend;
        private RecordingEventBus _events;
        private SceneService _service;

        [SetUp]
        public void SetUp()
        {
            _backend = new FakeSceneBackend("Bootstrap");
            _events = new RecordingEventBus();
            _service = new SceneService(_backend, _events);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_service != null)
            {
                var dispose = _service.DisposeAsync().AsTask();
                yield return WaitForTask(dispose);
                dispose.GetAwaiter().GetResult();
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void RequestRejectsInvalidKey(string key)
        {
            Assert.Throws<ArgumentException>(
                () => new SceneRequest(
                    key == null ? default : new ResourceKey(key),
                    SceneLoadMode.Single,
                    true));
        }

        [Test]
        public void RequestRejectsInvalidModeAndInactiveSingle()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SceneRequest(
                    new ResourceKey("scene"),
                    (SceneLoadMode)99,
                    true));
            Assert.Throws<ArgumentException>(
                () => new SceneRequest(
                    new ResourceKey("scene"),
                    SceneLoadMode.Single,
                    false));
        }

        [Test]
        public void CameraSyncOptionsTreatRigSelectionAsAnEnabledPolicy()
        {
            Assert.That(
                new SceneCameraSyncOptions(
                    "XR",
                    SceneCameraSyncFlags.None).Enabled,
                Is.True);
            Assert.That(
                new SceneCameraSyncOptions(
                    null,
                    SceneCameraSyncFlags.None).Enabled,
                Is.False);
        }

        [UnityTest]
        public IEnumerator LoadByIdUsesSceneTableAndPublishesCameraPolicy()
        {
            return RunAsync(LoadByIdUsesSceneTableAndPublishesCameraPolicyAsync());
        }

        private async Task LoadByIdUsesSceneTableAndPublishesCameraPolicyAsync()
        {
            var tableText =
                "#class,ArkFramework.SceneTableRow\n" +
                "#fields,Id,Address,Mode,ActivateOnLoad,RigId," +
                "SyncRigPose,SyncCameraSettings,SyncComponents," +
                "ComponentTypes,DisableSceneCameras\n" +
                "#types,string,string,SceneLoadMode,bool,string,bool,bool," +
                "bool,string[],bool\n" +
                "#key,Id\n" +
                ",scene.game,scene/address,Single,true,Main,true,true,true," +
                "Example.First|Example.Second,true\n";
            var tables = new TableService(new SceneTableSource(tableText));
            var catalog = await tables.LoadAsync<SceneTableRow>("Scenes.csv");
            tables.Dispose();
            await _service.DisposeAsync();
            _service = SceneService.CreateWithCatalog(
                _backend,
                _events,
                catalog);

            await _service.LoadByIdAsync("scene.game");

            Assert.That(_service.ActiveSceneId, Is.EqualTo("scene.game"));
            Assert.That(
                _service.ActiveSceneKey,
                Is.EqualTo(new ResourceKey("scene/address")));
            Assert.That(
                _service.TryGetDefinition("scene.game", out var definition),
                Is.True);
            Assert.That(definition.RigId, Is.EqualTo("Main"));
            var completed = _events.Transitions.Last(
                value => value.Stage == SceneTransitionStage.Completed);
            Assert.That(completed.SceneId, Is.EqualTo("scene.game"));
            Assert.That(completed.CameraSync.RigId, Is.EqualTo("Main"));
            Assert.That(
                completed.CameraSync.Flags,
                Is.EqualTo(
                    SceneCameraSyncFlags.RigPose |
                    SceneCameraSyncFlags.CameraSettings |
                    SceneCameraSyncFlags.Components));
            Assert.That(
                completed.CameraSync.ComponentTypeNames,
                Is.EqualTo(new[] { "Example.First", "Example.Second" }));
            Assert.That(completed.CameraSync.DisableSceneCameras, Is.True);
        }

        [Test]
        public void LifetimeCancellationCompletionPreventsBoundaryCommit()
        {
            var arbiter = new SceneRequestCancellationArbiter();
            using (var lifetime = new CancellationTokenSource())
            using (arbiter.RegisterLifetimeCancellation(lifetime.Token))
            {
                lifetime.Cancel();

                Assert.That(
                    arbiter.TryCrossIrreversibleBoundary(),
                    Is.False);
                Assert.That(arbiter.HasCrossedBoundary, Is.False);
            }
        }

        [Test]
        public void BoundaryCommitBeforeLifetimeCancellationRemainsCommitted()
        {
            var arbiter = new SceneRequestCancellationArbiter();
            using (var lifetime = new CancellationTokenSource())
            using (arbiter.RegisterLifetimeCancellation(lifetime.Token))
            {
                Assert.That(
                    arbiter.TryCrossIrreversibleBoundary(),
                    Is.True);

                lifetime.Cancel();

                Assert.That(arbiter.HasCrossedBoundary, Is.True);
            }
        }

        [Test]
        public void QueuedStopDecisionPreventsSubsequentStart()
        {
            var arbiter = new SceneRequestCancellationArbiter();

            Assert.That(
                arbiter.TryCancelQueuedForStop(),
                Is.True);
            Assert.That(
                arbiter.TryStart(false),
                Is.False);
        }

        [UnityTest]
        public IEnumerator ConcurrentLifetimeCancellationIsLinearizedWithBoundaryCommit()
        {
            return RunAsync(
                ConcurrentLifetimeCancellationIsLinearizedWithBoundaryCommitAsync());
        }

        private async Task
            ConcurrentLifetimeCancellationIsLinearizedWithBoundaryCommitAsync()
        {
            var boundaryEntered = NewCompletion();
            using (var releaseBoundary = new ManualResetEventSlim(false))
            using (var lifetime = new CancellationTokenSource())
            {
                var arbiter = new SceneRequestCancellationArbiter(
                    () =>
                    {
                        boundaryEntered.TrySetResult(true);
                        if (!releaseBoundary.Wait(Timeout))
                        {
                            throw new TimeoutException(
                                "Boundary release timed out.");
                        }
                    });
                using (arbiter.RegisterLifetimeCancellation(lifetime.Token))
                {
                    var boundary = Task.Run(
                        () => arbiter.TryCrossIrreversibleBoundary());
                    await AwaitWithin(boundaryEntered.Task);

                    var cancel = Task.Run(() => lifetime.Cancel());
                    while (!lifetime.IsCancellationRequested)
                    {
                        await Task.Yield();
                    }

                    try
                    {
                        Assert.That(
                            cancel.IsCompleted,
                            Is.False,
                            "Cancel returned before the boundary lock committed.");
                    }
                    finally
                    {
                        releaseBoundary.Set();
                    }

                    await AwaitWithin(boundary);
                    Assert.That(await boundary, Is.True);
                    await AwaitWithin(cancel);
                    Assert.That(arbiter.HasCrossedBoundary, Is.True);
                }
            }
        }

        [UnityTest]
        public IEnumerator SingleUsesStableStagesAndTransactionalOrdering()
        {
            return RunAsync(SingleUsesStableStagesAndTransactionalOrderingAsync());
        }

        private async Task SingleUsesStableStagesAndTransactionalOrderingAsync()
        {
            await _service.LoadAsync(
                new SceneRequest(
                    new ResourceKey("game"),
                    SceneLoadMode.Single,
                    true));

            Assert.That(
                _backend.Calls,
                Is.EqualTo(
                    new[]
                    {
                        "capture:Bootstrap",
                        "load:game:additive:inactive",
                        "activate:game",
                        "set-active:game",
                        "unload:Bootstrap"
                    }));
            Assert.That(_service.ActiveSceneKey.Value, Is.EqualTo("game"));
            Assert.That(_service.ActiveSceneName, Is.EqualTo("game"));
            Assert.That(_service.IsTransitioning, Is.False);
            Assert.That(_service.QueueLength, Is.Zero);
            Assert.That(
                _events.Transitions.Select(value => value.Stage),
                Is.EqualTo(
                    new[]
                    {
                        SceneTransitionStage.Started,
                        SceneTransitionStage.ShowLoading,
                        SceneTransitionStage.Loading,
                        SceneTransitionStage.Progress,
                        SceneTransitionStage.Progress,
                        SceneTransitionStage.Activating,
                        SceneTransitionStage.Activated,
                        SceneTransitionStage.SettingActive,
                        SceneTransitionStage.UnloadingPrevious,
                        SceneTransitionStage.HideLoading,
                        SceneTransitionStage.Completed
                    }));
            Assert.That(
                _events.Transitions.Select(value => value.RequestId).Distinct().Count(),
                Is.EqualTo(1));
            Assert.That(
                _events.Transitions.Where(
                    value => value.Stage == SceneTransitionStage.Progress)
                    .Select(value => value.Progress),
                Is.EqualTo(new[] { 0f, 1f }));
        }

        [UnityTest]
        public IEnumerator RequestsAreStrictFifoAndNeverOverlap()
        {
            return RunAsync(RequestsAreStrictFifoAndNeverOverlapAsync());
        }

        private async Task RequestsAreStrictFifoAndNeverOverlapAsync()
        {
            var firstGate = _backend.BlockNextLoad();
            var first = _service.LoadAsync(
                Additive("first", false)).AsTask();
            await firstGate.Entered;
            var second = _service.LoadAsync(
                Additive("second", false)).AsTask();

            Assert.That(_service.QueueLength, Is.EqualTo(1));
            Assert.That(_backend.MaximumConcurrentOperations, Is.EqualTo(1));
            firstGate.Release();
            await AwaitWithin(first);
            await AwaitWithin(second);

            Assert.That(
                _backend.Calls.Where(call => call.StartsWith("load:")),
                Is.EqualTo(
                    new[]
                    {
                        "load:first:additive:inactive",
                        "load:second:additive:inactive"
                    }));
            Assert.That(_backend.MaximumConcurrentOperations, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AdditiveActiveModesPreservePrevious()
        {
            return RunAsync(AdditiveActiveModesPreservePreviousAsync());
        }

        private async Task AdditiveActiveModesPreservePreviousAsync()
        {
            await _service.LoadAsync(Additive("background", false));
            Assert.That(_service.ActiveSceneName, Is.EqualTo("Bootstrap"));
            Assert.That(_backend.Unloaded, Is.Empty);

            await _service.LoadAsync(Additive("overlay", true));
            Assert.That(_service.ActiveSceneName, Is.EqualTo("overlay"));
            Assert.That(_backend.Unloaded, Is.Empty);
            Assert.That(
                _service.Diagnostics.OwnedSceneKeys.Select(key => key.Value),
                Is.EquivalentTo(new[] { "background", "overlay" }));
        }

        [UnityTest]
        public IEnumerator PreCanceledAndQueuedCanceledDoNotStartBackend()
        {
            return RunAsync(PreCanceledAndQueuedCanceledDoNotStartBackendAsync());
        }

        private async Task PreCanceledAndQueuedCanceledDoNotStartBackendAsync()
        {
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await AssertCanceledAsync(
                    () => _service.LoadAsync(
                        Additive("pre", false),
                        canceled.Token).AsTask());
            }

            var gate = _backend.BlockNextLoad();
            var first = _service.LoadAsync(Additive("first", false)).AsTask();
            await gate.Entered;
            using (var canceled = new CancellationTokenSource())
            {
                var second = _service.LoadAsync(
                    Additive("queued", false),
                    canceled.Token).AsTask();
                var third = _service.LoadAsync(
                    Additive("third", false)).AsTask();
                canceled.Cancel();
                await AssertCanceledAsync(() => second);
                gate.Release();
                await AwaitWithin(first);
                await AwaitWithin(third);
            }

            Assert.That(
                _backend.Loaded,
                Is.EqualTo(new[] { "first", "third" }));
        }

        [UnityTest]
        public IEnumerator CancellationBeforeActivationCleansTargetOnce()
        {
            return RunAsync(CancellationBeforeActivationCleansTargetOnceAsync());
        }

        private async Task CancellationBeforeActivationCleansTargetOnceAsync()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                _events.OnTransition = value =>
                {
                    if (value.Stage == SceneTransitionStage.Progress &&
                        value.Progress == 1f)
                    {
                        cancellation.Cancel();
                    }
                };
                var task = _service.LoadAsync(
                    new SceneRequest(
                        new ResourceKey("cancel"),
                        SceneLoadMode.Single,
                        true),
                    cancellation.Token).AsTask();
                await AssertCanceledAsync(() => task);
            }

            Assert.That(_backend.Unloaded, Is.EqualTo(new[] { "cancel" }));
            Assert.That(_backend.ActiveName, Is.EqualTo("Bootstrap"));
            Assert.That(
                _events.Transitions.Count(
                    value => value.Stage == SceneTransitionStage.HideLoading),
                Is.EqualTo(1));
            Assert.That(
                _events.Transitions.Last().Stage,
                Is.EqualTo(SceneTransitionStage.Canceled));
        }

        [UnityTest]
        public IEnumerator InactiveAdditiveCancellationFromHideLoadingRollsBackBeforeCommit()
        {
            return RunAsync(
                InactiveAdditiveCancellationFromHideLoadingRollsBackBeforeCommitAsync());
        }

        private async Task
            InactiveAdditiveCancellationFromHideLoadingRollsBackBeforeCommitAsync()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                _events.OnTransition = value =>
                {
                    if (value.Request.Key.Value == "cancel-at-hide" &&
                        value.Stage == SceneTransitionStage.HideLoading)
                    {
                        cancellation.Cancel();
                    }
                };

                var canceled = _service.LoadAsync(
                    Additive("cancel-at-hide", false),
                    cancellation.Token).AsTask();
                var exception =
                    await AssertThrowsAsync<TaskCanceledException>(
                        () => canceled);

                Assert.That(
                    exception.CancellationToken,
                    Is.EqualTo(cancellation.Token));
            }

            _events.OnTransition = null;
            await _service.LoadAsync(Additive("after-cancel", false));

            Assert.That(
                _backend.Unloaded,
                Is.EqualTo(new[] { "cancel-at-hide" }));
            Assert.That(
                _service.Diagnostics.OwnedSceneKeys
                    .Select(key => key.Value)
                    .ToArray(),
                Is.EqualTo(new[] { "after-cancel" }));
            Assert.That(
                _events.Transitions.Any(
                    value =>
                        value.Request.Key.Value == "cancel-at-hide" &&
                        value.Stage == SceneTransitionStage.Completed),
                Is.False);
            Assert.That(
                _events.Transitions.Last(
                    value =>
                        value.Request.Key.Value == "cancel-at-hide").Stage,
                Is.EqualTo(SceneTransitionStage.Canceled));
            Assert.That(
                _backend.Loaded,
                Is.EqualTo(new[] { "cancel-at-hide", "after-cancel" }));
        }

        [UnityTest]
        public IEnumerator InactiveAdditiveCancellationAfterCommitCancelsCallerWithoutRollback()
        {
            return RunAsync(
                InactiveAdditiveCancellationAfterCommitCancelsCallerWithoutRollbackAsync());
        }

        private async Task
            InactiveAdditiveCancellationAfterCommitCancelsCallerWithoutRollbackAsync()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                _events.OnTransition = value =>
                {
                    if (value.Request.Key.Value == "commit-wins" &&
                        value.Stage == SceneTransitionStage.Completed)
                    {
                        cancellation.Cancel();
                    }
                };

                var load = _service.LoadAsync(
                    Additive("commit-wins", false),
                    cancellation.Token).AsTask();
                var exception =
                    await AssertThrowsAsync<TaskCanceledException>(
                        () => load);

                Assert.That(
                    exception.CancellationToken,
                    Is.EqualTo(cancellation.Token));
            }

            Assert.That(_backend.Unloaded, Is.Empty);
            Assert.That(
                _service.Diagnostics.OwnedSceneKeys
                    .Select(key => key.Value)
                    .ToArray(),
                Is.EqualTo(new[] { "commit-wins" }));
            Assert.That(
                _events.Transitions.Last().Stage,
                Is.EqualTo(SceneTransitionStage.Completed));
        }

        [UnityTest]
        public IEnumerator StopBeforeActivationUsesCanceledLifetimeToken()
        {
            return RunAsync(StopBeforeActivationUsesCanceledLifetimeTokenAsync());
        }

        private async Task StopBeforeActivationUsesCanceledLifetimeTokenAsync()
        {
            var gate = _backend.BlockNextLoad();
            using (var caller = new CancellationTokenSource())
            {
                var load = _service.LoadAsync(
                    Additive("stop-token", false),
                    caller.Token).AsTask();
                await gate.Entered;

                var stop = _service.StopAsync().AsTask();
                var exception =
                    await AssertThrowsAsync<TaskCanceledException>(
                        () => load);
                await AwaitWithin(stop);

                Assert.That(caller.IsCancellationRequested, Is.False);
                Assert.That(
                    exception.CancellationToken.IsCancellationRequested,
                    Is.True);
                Assert.That(
                    exception.CancellationToken,
                    Is.Not.EqualTo(caller.Token));
            }
        }

        [UnityTest]
        public IEnumerator QueuedStopUsesAlreadyCanceledLifetimeToken()
        {
            return RunAsync(
                QueuedStopUsesAlreadyCanceledLifetimeTokenAsync());
        }

        private async Task QueuedStopUsesAlreadyCanceledLifetimeTokenAsync()
        {
            await _service.DisposeAsync();
            var queuedCancellationCompleted = NewCompletion();
            using (var releaseStop = new ManualResetEventSlim(false))
            {
                _service = new SceneService(
                    _backend,
                    _events,
                    () =>
                    {
                        queuedCancellationCompleted.TrySetResult(true);
                        if (!releaseStop.Wait(Timeout))
                        {
                            throw new TimeoutException(
                                "Stop release timed out.");
                        }
                    });

                var activeGate = _backend.BlockNextLoad();
                var active = _service.LoadAsync(
                    Additive("active-before-stop", false)).AsTask();
                await activeGate.Entered;

                using (var caller = new CancellationTokenSource())
                {
                    var queued = _service.LoadAsync(
                        Additive("queued-before-stop", false),
                        caller.Token).AsTask();
                    var stop = Task.Run(
                        async () => await _service.StopAsync());
                    await AwaitWithin(queuedCancellationCompleted.Task);

                    try
                    {
                        var exception =
                            await AssertThrowsAsync<TaskCanceledException>(
                                () => queued);
                        Assert.That(
                            caller.IsCancellationRequested,
                            Is.False);
                        Assert.That(
                            exception.CancellationToken
                                .IsCancellationRequested,
                            Is.True);
                        Assert.That(
                            exception.CancellationToken,
                            Is.Not.EqualTo(caller.Token));
                    }
                    finally
                    {
                        releaseStop.Set();
                    }

                    await AssertCanceledAsync(() => active);
                    await AwaitWithin(stop);
                }
            }
        }

        [UnityTest]
        public IEnumerator BackendCancellationWithoutCanceledTokenUsesCanceledFallback()
        {
            return RunAsync(
                BackendCancellationWithoutCanceledTokenUsesCanceledFallbackAsync());
        }

        private async Task
            BackendCancellationWithoutCanceledTokenUsesCanceledFallbackAsync()
        {
            using (var backendToken = new CancellationTokenSource())
            {
                _backend.LoadFailure =
                    new OperationCanceledException(backendToken.Token);

                var exception =
                    await AssertThrowsAsync<TaskCanceledException>(
                        () => _service.LoadAsync(
                            Additive("backend-canceled", false)).AsTask());

                Assert.That(backendToken.IsCancellationRequested, Is.False);
                Assert.That(
                    exception.CancellationToken.IsCancellationRequested,
                    Is.True);
            }
        }

        [UnityTest]
        public IEnumerator PreActivationCallerWaitsForTargetCleanup()
        {
            return RunAsync(PreActivationCallerWaitsForTargetCleanupAsync());
        }

        private async Task PreActivationCallerWaitsForTargetCleanupAsync()
        {
            var unloadGate = _backend.BlockNextUnload();
            using (var cancellation = new CancellationTokenSource())
            {
                _events.OnTransition = value =>
                {
                    if (value.Stage == SceneTransitionStage.Progress &&
                        value.Progress == 1f)
                    {
                        cancellation.Cancel();
                    }
                };
                var task = _service.LoadAsync(
                    new SceneRequest(
                        new ResourceKey("cleanup-first"),
                        SceneLoadMode.Single,
                        true),
                    cancellation.Token).AsTask();

                await unloadGate.Entered;
                var completedBeforeCleanup = task.IsCompleted;
                unloadGate.Release();
                await AssertCanceledAsync(() => task);
                Assert.That(
                    completedBeforeCleanup,
                    Is.False,
                    "Pre-activation cancellation completed before cleanup.");
            }
        }

        [UnityTest]
        public IEnumerator CancellationAfterActivationCancelsCallerButFinishesBeforeNext()
        {
            return RunAsync(
                CancellationAfterActivationCancelsCallerButFinishesBeforeNextAsync());
        }

        private async Task
            CancellationAfterActivationCancelsCallerButFinishesBeforeNextAsync()
        {
            var gate = _backend.BlockNextActivation();
            using (var cancellation = new CancellationTokenSource())
            {
                var first = _service.LoadAsync(
                    new SceneRequest(
                        new ResourceKey("first"),
                        SceneLoadMode.Single,
                        true),
                    cancellation.Token).AsTask();
                await gate.Entered;
                var second = _service.LoadAsync(
                    Additive("second", false)).AsTask();
                cancellation.Cancel();
                await AssertCanceledAsync(() => first);
                Assert.That(second.IsCompleted, Is.False);
                gate.Release();
                await AwaitWithin(second);
            }

            Assert.That(
                _backend.Calls.IndexOf("unload:Bootstrap"),
                Is.LessThan(_backend.Calls.IndexOf("load:second:additive:inactive")));
            Assert.That(
                _events.Transitions.Any(
                    value =>
                        value.Request.Key.Value == "first" &&
                        value.Stage == SceneTransitionStage.Completed),
                Is.True);
        }

        [UnityTest]
        public IEnumerator FailuresPreservePrimaryAndPublishExactStage()
        {
            return RunAsync(FailuresPreservePrimaryAndPublishExactStageAsync());
        }

        private async Task FailuresPreservePrimaryAndPublishExactStageAsync()
        {
            var primary = new TestSceneException("set active failed");
            var cleanup = new TestCleanupException();
            _backend.SetActiveFailure = primary;
            _backend.UnloadFailure = cleanup;

            var failure = await AssertThrowsAsync<AggregateException>(
                () => _service.LoadAsync(
                    new SceneRequest(
                        new ResourceKey("broken"),
                        SceneLoadMode.Single,
                        true)).AsTask());

            Assert.That(failure.InnerExceptions[0], Is.SameAs(primary));
            Assert.That(failure.InnerExceptions[1], Is.SameAs(cleanup));
            Assert.That(_backend.ActiveName, Is.EqualTo("Bootstrap"));
            var failed = _events.Transitions.Last();
            Assert.That(failed.Stage, Is.EqualTo(SceneTransitionStage.Failed));
            Assert.That(failed.FailureStage, Is.EqualTo(
                SceneTransitionStage.SettingActive));
            Assert.That(failed.Exception, Is.SameAs(failure));
            Assert.That(
                _events.Transitions.Count(
                    value => value.Stage == SceneTransitionStage.HideLoading),
                Is.EqualTo(1));
            Assert.That(
                _service.Diagnostics.OwnedSceneKeys
                    .Select(key => key.Value)
                    .ToArray(),
                Is.EqualTo(new[] { "broken" }));
            _backend.UnloadFailure = null;
            await _service.StopAsync();
            Assert.That(
                _backend.Unloaded.Count(value => value == "broken"),
                Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator LoadAndActivationFailuresPreserveOldScene()
        {
            return RunAsync(LoadAndActivationFailuresPreserveOldSceneAsync());
        }

        private async Task LoadAndActivationFailuresPreserveOldSceneAsync()
        {
            var loadFailure = new TestSceneException("load failed");
            _backend.LoadFailure = loadFailure;
            var observedLoad = await AssertThrowsAsync<TestSceneException>(
                () => _service.LoadAsync(
                    new SceneRequest(
                        new ResourceKey("load-broken"),
                        SceneLoadMode.Single,
                        true)).AsTask());
            Assert.That(observedLoad, Is.SameAs(loadFailure));
            Assert.That(_backend.ActiveName, Is.EqualTo("Bootstrap"));
            Assert.That(
                _events.Transitions.Last().FailureStage,
                Is.EqualTo(SceneTransitionStage.Loading));

            _backend.LoadFailure = null;
            _events.Transitions.Clear();
            var activateFailure =
                new TestSceneException("activate failed");
            _backend.ActivateFailure = activateFailure;
            var observedActivation =
                await AssertThrowsAsync<TestSceneException>(
                    () => _service.LoadAsync(
                        new SceneRequest(
                            new ResourceKey("activate-broken"),
                            SceneLoadMode.Single,
                            true)).AsTask());
            Assert.That(observedActivation, Is.SameAs(activateFailure));
            Assert.That(_backend.ActiveName, Is.EqualTo("Bootstrap"));
            Assert.That(
                _backend.Unloaded,
                Does.Contain("activate-broken"));
            Assert.That(
                _events.Transitions.Last().FailureStage,
                Is.EqualTo(SceneTransitionStage.Activating));
        }

        [UnityTest]
        public IEnumerator PreviousUnloadFailureKeepsActivatedTargetOwned()
        {
            return RunAsync(PreviousUnloadFailureKeepsActivatedTargetOwnedAsync());
        }

        private async Task PreviousUnloadFailureKeepsActivatedTargetOwnedAsync()
        {
            var failure = new TestSceneException("old unload failed");
            _backend.UnloadFailure = failure;
            var observed = await AssertThrowsAsync<TestSceneException>(
                () => _service.LoadAsync(
                    new SceneRequest(
                        new ResourceKey("activated"),
                        SceneLoadMode.Single,
                        true)).AsTask());

            Assert.That(observed, Is.SameAs(failure));
            Assert.That(_backend.ActiveName, Is.EqualTo("activated"));
            Assert.That(
                _service.Diagnostics.OwnedSceneKeys.Select(key => key.Value),
                Is.EqualTo(new[] { "activated" }));
            Assert.That(
                _events.Transitions.Last().FailureStage,
                Is.EqualTo(SceneTransitionStage.UnloadingPrevious));
            _backend.UnloadFailure = null;
        }

        [UnityTest]
        public IEnumerator DiagnosticsAreImmutableSnapshots()
        {
            return RunAsync(DiagnosticsAreImmutableSnapshotsAsync());
        }

        private async Task DiagnosticsAreImmutableSnapshotsAsync()
        {
            var before = _service.Diagnostics;
            await _service.LoadAsync(Additive("owned", false));
            var after = _service.Diagnostics;

            Assert.That(before.OwnedSceneKeys, Is.Empty);
            Assert.That(after.OwnedSceneKeys.Count, Is.EqualTo(1));
            var list = (IList<ResourceKey>)after.OwnedSceneKeys;
            Assert.Throws<NotSupportedException>(
                () => list.Add(new ResourceKey("mutate")));
            await _service.StopAsync();
            Assert.That(after.OwnedSceneKeys.Count, Is.EqualTo(1));
            Assert.That(_service.Diagnostics.OwnedSceneKeys, Is.Empty);
        }

        [UnityTest]
        public IEnumerator StopIsCanonicalRejectsNewAndCleansOwnedExactlyOnce()
        {
            return RunAsync(StopIsCanonicalRejectsNewAndCleansOwnedExactlyOnceAsync());
        }

        private async Task StopIsCanonicalRejectsNewAndCleansOwnedExactlyOnceAsync()
        {
            await _service.LoadAsync(Additive("one", false));
            await _service.LoadAsync(Additive("two", false));

            var first = _service.StopAsync().AsTask();
            var second = _service.StopAsync().AsTask();
            Assert.That(second, Is.SameAs(first));
            await AwaitWithin(first);
            Assert.Throws<ObjectDisposedException>(
                () => _service.LoadAsync(Additive("rejected", false)));
            Assert.That(
                _backend.Unloaded,
                Is.EquivalentTo(new[] { "one", "two" }));
            Assert.That(
                _backend.Unloaded.Count(value => value == "one"),
                Is.EqualTo(1));

            var dispose = _service.DisposeAsync().AsTask();
            await AwaitWithin(dispose);
            _service = null;
            Assert.That(
                _backend.Unloaded.Count(value => value == "one"),
                Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator StopDuringTransitionCancelsBeforeBoundaryAndWaitsAfterIt()
        {
            return RunAsync(
                StopDuringTransitionCancelsBeforeBoundaryAndWaitsAfterItAsync());
        }

        private async Task
            StopDuringTransitionCancelsBeforeBoundaryAndWaitsAfterItAsync()
        {
            var loadGate = _backend.BlockNextLoad();
            var preBoundary = _service.LoadAsync(
                new SceneRequest(
                    new ResourceKey("pre-stop"),
                    SceneLoadMode.Single,
                    true)).AsTask();
            await loadGate.Entered;
            var stop = _service.StopAsync().AsTask();
            await AssertCanceledAsync(() => preBoundary);
            await AwaitWithin(stop);
            loadGate.Release();
            Assert.That(_backend.ActiveName, Is.EqualTo("Bootstrap"));

            await _service.DisposeAsync();
            _service = null;
            _backend = new FakeSceneBackend("Bootstrap");
            _events = new RecordingEventBus();
            _service = new SceneService(_backend, _events);
            var activationGate = _backend.BlockNextActivation();
            var postBoundary = _service.LoadAsync(
                new SceneRequest(
                    new ResourceKey("post-stop"),
                    SceneLoadMode.Single,
                    true)).AsTask();
            await activationGate.Entered;
            stop = _service.StopAsync().AsTask();
            Assert.That(stop.IsCompleted, Is.False);
            activationGate.Release();
            await AwaitWithin(postBoundary);
            await AwaitWithin(stop);
            Assert.That(
                _backend.Calls,
                Does.Contain("unload:Bootstrap"));
        }

        [UnityTest]
        public IEnumerator EventCallbackStopFastRejectsWithoutSelfWait()
        {
            return RunAsync(EventCallbackStopFastRejectsWithoutSelfWaitAsync());
        }

        private async Task EventCallbackStopFastRejectsWithoutSelfWaitAsync()
        {
            Exception callbackFailure = null;
            _events.OnTransition = value =>
            {
                if (value.Stage == SceneTransitionStage.Started)
                {
                    callbackFailure = Capture(
                        () => _ = _service.StopAsync());
                }
            };

            var request = _service.LoadAsync(
                Additive("reentry", false)).AsTask();
            await AwaitWithin(request);
            Assert.That(
                callbackFailure,
                Is.TypeOf<InvalidOperationException>());
            Assert.That(
                _service.Diagnostics.OwnedSceneKeys.Select(key => key.Value),
                Is.EqualTo(new[] { "reentry" }));
            var first = _service.StopAsync().AsTask();
            var second = _service.StopAsync().AsTask();
            Assert.That(second, Is.SameAs(first));
            await AwaitWithin(first);
        }

        [UnityTest]
        public IEnumerator DetachedCallbackContextDoesNotRemainReentrant()
        {
            return RunAsync(DetachedCallbackContextDoesNotRemainReentrantAsync());
        }

        private async Task DetachedCallbackContextDoesNotRemainReentrantAsync()
        {
            var gate = NewCompletion();
            Task detachedStop = null;
            _events.OnTransition = value =>
            {
                if (value.Stage == SceneTransitionStage.Started)
                {
                    detachedStop = StopAfterGateAsync(_service, gate.Task);
                }
            };

            await _service.LoadAsync(Additive("detached", false));
            Assert.That(detachedStop, Is.Not.Null);
            gate.TrySetResult(true);
            await AwaitWithin(detachedStop);
        }

        private static async Task StopAfterGateAsync(
            SceneService service,
            Task gate)
        {
            await gate;
            await service.StopAsync();
        }

        [UnityTest]
        public IEnumerator EventCallbackDisposeFastRejectsWithoutDoubleCleanup()
        {
            return RunAsync(
                EventCallbackDisposeFastRejectsWithoutDoubleCleanupAsync());
        }

        private async Task
            EventCallbackDisposeFastRejectsWithoutDoubleCleanupAsync()
        {
            Exception callbackFailure = null;
            _events.OnTransition = value =>
            {
                if (value.Stage == SceneTransitionStage.Started)
                {
                    callbackFailure = Capture(
                        () => _ = _service.DisposeAsync());
                }
            };

            var request = _service.LoadAsync(
                Additive("dispose-reentry", false)).AsTask();
            await AwaitWithin(request);
            Assert.That(
                callbackFailure,
                Is.TypeOf<InvalidOperationException>());
            var dispose = _service.DisposeAsync().AsTask();
            var stop = _service.StopAsync().AsTask();
            Assert.That(stop, Is.SameAs(dispose));
            await dispose;
            Assert.That(
                _backend.Unloaded.Count(
                    value => value == "dispose-reentry"),
                Is.EqualTo(1));
            _service = null;
        }

        [UnityTest]
        public IEnumerator ModuleUsesExactDependenciesAndScopeOwnership()
        {
            return RunAsync(ModuleUsesExactDependenciesAndScopeOwnershipAsync());
        }

        [UnityTest]
        public IEnumerator ProductionBackendForwardsPreActivationCancellation()
        {
            return RunAsync(
                ProductionBackendForwardsPreActivationCancellationAsync());
        }

        private static async Task
            ProductionBackendForwardsPreActivationCancellationAsync()
        {
            var loader = new BlockingSceneResourceLoader();
            var backend = new ResourceSceneBackend(loader);
            using (var cancellation = new CancellationTokenSource())
            {
                var load = backend.LoadAsync(
                    new ResourceKey("token"),
                    _ => { },
                    cancellation.Token).AsTask();
                Assert.That(loader.ObservedToken.CanBeCanceled, Is.True);
                cancellation.Cancel();
                Assert.That(
                    loader.ObservedToken.IsCancellationRequested,
                    Is.True);
                loader.Cancel();
                await AssertCanceledAsync(() => load);
            }
        }

        [UnityTest]
        public IEnumerator ProductionBackendRetriesOwnedUnloadThroughResource()
        {
            return RunAsync(
                ProductionBackendRetriesOwnedUnloadThroughResourceAsync());
        }

        private static async Task
            ProductionBackendRetriesOwnedUnloadThroughResourceAsync()
        {
            var resourceBackend = new RetrySceneResourceBackend();
            var resources = new ResourceService(resourceBackend);
            var backend = new ResourceSceneBackend(resources);
            var scene = await backend.LoadAsync(
                new ResourceKey("owned-retry"),
                _ => { },
                CancellationToken.None);

            resourceBackend.FailNextUnload(
                new TestSceneException("first unload failed"));
            await AssertThrowsAsync<TestSceneException>(
                () => backend.UnloadAsync(
                    scene,
                    CancellationToken.None).AsTask());
            Assert.That(scene.IsOwned, Is.True);

            resourceBackend.SucceedNextUnload();
            await backend.UnloadAsync(scene, CancellationToken.None);
            Assert.That(resourceBackend.UnloadCallCount, Is.EqualTo(2));
            await resources.DisposeAsync();
        }

        private static async Task ModuleUsesExactDependenciesAndScopeOwnershipAsync()
        {
            var module = new SceneModule();
            Assert.That(module.Id, Is.EqualTo("Scene"));
            Assert.That(
                module.Dependencies,
                Is.EqualTo(new[] { "Resource", "EventBus", "Table" }));

            var runtime = new FrameworkRuntime();
            var loader = new NoopSceneResourceLoader();
            await runtime.StartAsync(
                new[]
                {
                    Describe(new ResourceStubModule(loader), 0),
                    Describe(new EventBusModule(), 1),
                    Describe(new TableModule(), 2),
                    Describe(module, 3)
                },
                CancellationToken.None);
            var service = runtime.Services.Resolve<ISceneService>();
            Assert.That(service.IsTransitioning, Is.False);
            Assert.That(loader.LoadCount, Is.Zero);
            await runtime.StopAsync(CancellationToken.None);
            Assert.That(loader.LoadCount, Is.Zero);
            Assert.Throws<InvalidOperationException>(
                () => runtime.Services.Resolve<ISceneService>());
            await runtime.DisposeAsync();
        }

        private static SceneRequest Additive(string key, bool active)
        {
            return new SceneRequest(
                new ResourceKey(key),
                SceneLoadMode.Additive,
                active);
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

        private static void Await(ValueTask task)
        {
            task.AsTask().GetAwaiter().GetResult();
        }

        private static async Task AwaitWithin(Task task)
        {
            var completed = await Task.WhenAny(task, Task.Delay(Timeout));
            if (!ReferenceEquals(completed, task))
            {
                throw new TimeoutException("Scene test timed out.");
            }

            await task;
        }

        private static async Task AssertCanceledAsync(Func<Task> action)
        {
            try
            {
                await AwaitWithin(action());
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Assert.Fail("Expected cancellation.");
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

            Assert.Fail($"Expected {typeof(TException).Name}.");
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

        private static IEnumerator WaitForTask(Task task)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted && elapsed.Elapsed < Timeout)
            {
                yield return null;
            }

            Assert.That(
                task.IsCompleted,
                Is.True,
                "Scene teardown timed out.");
        }

        private sealed class RecordingEventBus : IEventBus
        {
            public List<SceneTransitionEvent> Transitions { get; } =
                new List<SceneTransitionEvent>();
            public Action<SceneTransitionEvent> OnTransition { get; set; }

            public EventBusDiagnostics Diagnostics => null;

            public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
            {
                return new DisposableAction();
            }

            public IDisposable Subscribe<TEvent>(
                ModuleScope ownerScope,
                Action<TEvent> handler)
            {
                return new DisposableAction();
            }

            public void Publish<TEvent>(TEvent value)
            {
                if (value is SceneTransitionEvent transition)
                {
                    Transitions.Add(transition);
                    OnTransition?.Invoke(transition);
                }
            }

            public void Enqueue<TEvent>(TEvent value)
            {
                Publish(value);
            }
        }

        private sealed class DisposableAction : IDisposable
        {
            public void Dispose()
            {
            }
        }

        private sealed class FakeSceneBackend : ISceneBackend
        {
            private readonly Queue<OperationGate> _loadGates =
                new Queue<OperationGate>();
            private readonly Queue<OperationGate> _activationGates =
                new Queue<OperationGate>();
            private readonly Queue<OperationGate> _unloadGates =
                new Queue<OperationGate>();
            private int _activeOperations;

            public FakeSceneBackend(string activeName)
            {
                ActiveName = activeName;
            }

            public List<string> Calls { get; } = new List<string>();
            public List<string> Loaded { get; } = new List<string>();
            public List<string> Unloaded { get; } = new List<string>();
            public string ActiveName { get; private set; }
            public int MaximumConcurrentOperations { get; private set; }
            public Exception SetActiveFailure { get; set; }
            public Exception UnloadFailure { get; set; }
            public Exception LoadFailure { get; set; }
            public Exception ActivateFailure { get; set; }

            public ISceneBackendScene CaptureActiveScene()
            {
                Calls.Add("capture:" + ActiveName);
                return new FakeScene(ActiveName, default, false);
            }

            public async ValueTask<ISceneBackendScene> LoadAsync(
                ResourceKey key,
                Action<float> progress,
                CancellationToken token)
            {
                EnterOperation();
                try
                {
                    Calls.Add($"load:{key.Value}:additive:inactive");
                    Loaded.Add(key.Value);
                    progress(0f);
                    if (LoadFailure != null)
                    {
                        throw LoadFailure;
                    }

                    if (_loadGates.Count != 0)
                    {
                        var gate = _loadGates.Dequeue();
                        gate.SignalEntered();
                        if (!gate.CreatedBeforeBlock)
                        {
                            await AwaitGate(gate, token);
                        }
                        else
                        {
                            await gate.Released;
                            token.ThrowIfCancellationRequested();
                        }
                    }

                    progress(1f);
                    return new FakeScene(key.Value, key, true);
                }
                finally
                {
                    LeaveOperation();
                }
            }

            public async ValueTask ActivateAsync(
                ISceneBackendScene scene,
                CancellationToken token)
            {
                EnterOperation();
                try
                {
                    Calls.Add("activate:" + scene.Name);
                    if (ActivateFailure != null)
                    {
                        throw ActivateFailure;
                    }

                    if (_activationGates.Count != 0)
                    {
                        var gate = _activationGates.Dequeue();
                        gate.SignalEntered();
                        await AwaitGate(gate, token);
                    }
                }
                finally
                {
                    LeaveOperation();
                }
            }

            public void SetActiveScene(ISceneBackendScene scene)
            {
                Calls.Add("set-active:" + scene.Name);
                if (SetActiveFailure != null)
                {
                    throw SetActiveFailure;
                }

                ActiveName = scene.Name;
            }

            public async ValueTask UnloadAsync(
                ISceneBackendScene scene,
                CancellationToken token)
            {
                Calls.Add("unload:" + scene.Name);
                Unloaded.Add(scene.Name);
                if (_unloadGates.Count != 0)
                {
                    var gate = _unloadGates.Dequeue();
                    gate.SignalEntered();
                    await AwaitGate(gate, token);
                }

                if (UnloadFailure != null)
                {
                    throw UnloadFailure;
                }
            }

            public OperationGate BlockNextLoad()
            {
                var gate = new OperationGate(false);
                _loadGates.Enqueue(gate);
                return gate;
            }

            public OperationGate BlockNextLoadAfterCreation()
            {
                var gate = new OperationGate(true);
                _loadGates.Enqueue(gate);
                return gate;
            }

            public OperationGate BlockNextActivation()
            {
                var gate = new OperationGate(false);
                _activationGates.Enqueue(gate);
                return gate;
            }

            public OperationGate BlockNextUnload()
            {
                var gate = new OperationGate(false);
                _unloadGates.Enqueue(gate);
                return gate;
            }

            private void EnterOperation()
            {
                _activeOperations++;
                MaximumConcurrentOperations = Math.Max(
                    MaximumConcurrentOperations,
                    _activeOperations);
            }

            private void LeaveOperation()
            {
                _activeOperations--;
            }

            private static async Task AwaitGate(
                OperationGate gate,
                CancellationToken token)
            {
                var canceled = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using (token.Register(() => canceled.TrySetResult(true)))
                {
                    if (await Task.WhenAny(gate.Released, canceled.Task) !=
                        gate.Released)
                    {
                        throw new OperationCanceledException(token);
                    }
                }
            }
        }

        private sealed class FakeScene : ISceneBackendScene
        {
            public FakeScene(string name, ResourceKey key, bool owned)
            {
                Name = name;
                Key = key;
                IsOwned = owned;
            }

            public string Name { get; }
            public ResourceKey Key { get; }
            public bool IsOwned { get; }
        }

        private sealed class OperationGate
        {
            private readonly TaskCompletionSource<bool> _entered =
                NewCompletion();
            private readonly TaskCompletionSource<bool> _released =
                NewCompletion();

            public OperationGate(bool createdBeforeBlock)
            {
                CreatedBeforeBlock = createdBeforeBlock;
            }

            public bool CreatedBeforeBlock { get; }
            public Task Entered => _entered.Task;
            public Task Released => _released.Task;
            public void SignalEntered() => _entered.TrySetResult(true);
            public void Release() => _released.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> NewCompletion()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
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

        private sealed class ResourceStubModule : IFrameworkModule
        {
            private readonly ISceneResourceLoader _loader;

            public ResourceStubModule(ISceneResourceLoader loader)
            {
                _loader = loader;
            }

            public string Id => "Resource";
            public IReadOnlyCollection<string> Dependencies =>
                Array.Empty<string>();

            public ValueTask InitializeAsync(
                ModuleContext context,
                CancellationToken token)
            {
                context.ModuleScope.RegisterInstance(_loader);
                return default;
            }

            public ValueTask StartAsync(CancellationToken token) => default;
            public ValueTask StopAsync(CancellationToken token) => default;
            public ValueTask DisposeAsync() => default;
        }

        private sealed class NoopSceneResourceLoader : ISceneResourceLoader
        {
            public int LoadCount { get; private set; }

            public ValueTask<ISceneLease> LoadSceneAsync(
                ResourceKey key,
                UnityEngine.SceneManagement.LoadSceneMode mode,
                bool activateOnLoad,
                CancellationToken token = default)
            {
                LoadCount++;
                throw new NotSupportedException();
            }

            public ValueTask UnloadSceneAsync(
                ISceneLease lease,
                CancellationToken token = default)
            {
                return default;
            }
        }

        private sealed class BlockingSceneResourceLoader :
            ISceneResourceLoader
        {
            private readonly TaskCompletionSource<ISceneLease> _completion =
                new TaskCompletionSource<ISceneLease>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public CancellationToken ObservedToken { get; private set; }

            public ValueTask<ISceneLease> LoadSceneAsync(
                ResourceKey key,
                UnityEngine.SceneManagement.LoadSceneMode mode,
                bool activateOnLoad,
                CancellationToken token = default)
            {
                ObservedToken = token;
                return new ValueTask<ISceneLease>(_completion.Task);
            }

            public ValueTask UnloadSceneAsync(
                ISceneLease lease,
                CancellationToken token = default)
            {
                return default;
            }

            public void Cancel()
            {
                _completion.TrySetCanceled();
            }
        }

        private sealed class RetrySceneResourceBackend : IResourceBackend
        {
            private readonly Queue<SceneResourceOperation> _unloads =
                new Queue<SceneResourceOperation>();

            public int UnloadCallCount { get; private set; }

            public IResourceOperation<T> LoadAssetAsync<T>(ResourceKey key)
                where T : UnityEngine.Object
            {
                throw new NotSupportedException();
            }

            public IResourceOperation<GameObject> InstantiateAsync(
                ResourceKey key,
                Transform parent)
            {
                throw new NotSupportedException();
            }

            public IResourceOperation<IReadOnlyList<T>> LoadByLabelAsync<T>(
                string label)
                where T : UnityEngine.Object
            {
                throw new NotSupportedException();
            }

            public IResourceOperation<SceneInstance> LoadSceneAsync(
                ResourceKey key,
                UnityEngine.SceneManagement.LoadSceneMode mode,
                bool activateOnLoad)
            {
                return SceneResourceOperation.Succeeded();
            }

            public IResourceOperation<SceneInstance> UnloadSceneAsync(
                SceneInstance scene)
            {
                UnloadCallCount++;
                return _unloads.Dequeue();
            }

            public void FailNextUnload(Exception exception)
            {
                _unloads.Enqueue(
                    SceneResourceOperation.Failed(exception));
            }

            public void SucceedNextUnload()
            {
                _unloads.Enqueue(SceneResourceOperation.Succeeded());
            }
        }

        private sealed class SceneResourceOperation :
            IResourceOperation<SceneInstance>
        {
            private int _released;

            private SceneResourceOperation(Task<SceneInstance> task)
            {
                Task = task;
            }

            public Task<SceneInstance> Task { get; }

            public void Release()
            {
                Interlocked.Exchange(ref _released, 1);
            }

            public static SceneResourceOperation Succeeded()
            {
                return new SceneResourceOperation(
                    System.Threading.Tasks.Task.FromResult(default(SceneInstance)));
            }

            public static SceneResourceOperation Failed(Exception exception)
            {
                return new SceneResourceOperation(
                    System.Threading.Tasks.Task.FromException<SceneInstance>(
                        exception));
            }
        }

        private sealed class TestSceneException : Exception
        {
            public TestSceneException(string message) : base(message)
            {
            }
        }

        private sealed class TestCleanupException : Exception
        {
        }

        private sealed class SceneTableSource : ITableTextSource
        {
            private readonly string _text;

            public SceneTableSource(string text)
            {
                _text = text;
            }

            public ValueTask<string> ReadAsync(
                string relativePath,
                CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                return new ValueTask<string>(_text);
            }
        }
    }
}
