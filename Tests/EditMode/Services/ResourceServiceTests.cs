using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class ResourceServiceTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private FakeResourceBackend _backend;
        private RecordingLogger _logger;
        private ResourceService _service;

        [SetUp]
        public void SetUp()
        {
            _backend = new FakeResourceBackend();
            _logger = new RecordingLogger();
            _service = new ResourceService(_backend, _logger);
        }

        [TearDown]
        public void TearDown()
        {
            if (_service != null)
            {
                Await(_service.StopAsync());
                Await(_service.DisposeAsync());
            }

            for (var index = _objects.Count - 1; index >= 0; index--)
            {
                if (_objects[index] != null)
                {
                    Object.DestroyImmediate(_objects[index]);
                }
            }

            _objects.Clear();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void ResourceKey_RejectsNullEmptyOrWhitespace(string value)
        {
            Assert.Throws<ArgumentException>(() => new ResourceKey(value));
        }

        [Test]
        public void ResourceKey_UsesOrdinalValueEquality()
        {
            var first = new ResourceKey("asset/key");
            var same = new ResourceKey("asset/key");
            var differentCase = new ResourceKey("Asset/Key");

            Assert.That(first, Is.EqualTo(same));
            Assert.That(first.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(first == same, Is.True);
            Assert.That(first != differentCase, Is.True);
            Assert.That(first.ToString(), Is.EqualTo("asset/key"));
        }

        [Test]
        public void UnloadSceneAsync_RejectsAlternativeLeaseBeforeBackendMutation()
        {
            Assert.Throws<ArgumentException>(
                () => _service.UnloadSceneAsync(new AlternativeSceneLease()));
            Assert.That(_backend.StartCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator LoadAsync_ReturnsAssetAndReleasesExactlyOnce()
        {
            var asset = Track(ScriptableObject.CreateInstance<TestAsset>());
            var operation = _backend.EnqueueAsset<TestAsset>();

            var task = _service
                .LoadAsync<TestAsset>(new ResourceKey("asset/test"))
                .AsTask();
            operation.Succeed(asset);
            yield return WaitForTask(task);

            var lease = task.GetAwaiter().GetResult();
            Assert.That(lease.Asset, Is.SameAs(asset));
            Assert.That(lease.Key, Is.EqualTo(new ResourceKey("asset/test")));
            Assert.That(_service.Diagnostics.OutstandingLeases.Count, Is.EqualTo(1));
            Assert.That(operation.ReleaseCallCount, Is.Zero);

            lease.Dispose();

            Assert.That(operation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(operation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(_service.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.Throws<ObjectDisposedException>(() => _ = lease.Asset);
        }

        [Test]
        public void LeaseDispose_IsIdempotent()
        {
            var asset = Track(ScriptableObject.CreateInstance<TestAsset>());
            var operation = _backend.EnqueueCompletedAsset(asset);
            var lease = Await(
                _service.LoadAsync<TestAsset>(new ResourceKey("asset/test")));

            lease.Dispose();
            lease.Dispose();

            Assert.That(operation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(operation.UnderlyingReleaseCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CanceledWait_ReleasesBackendOperationAfterItCompletes()
        {
            var asset = Track(ScriptableObject.CreateInstance<TestAsset>());
            var operation = _backend.EnqueueAsset<TestAsset>();
            var cancellation = new CancellationTokenSource();
            var task = _service
                .LoadAsync<TestAsset>(
                    new ResourceKey("asset/cancel"),
                    cancellation.Token)
                .AsTask();

            cancellation.Cancel();
            yield return WaitForTask(task);

            AssertCanceled(task);
            Assert.That(operation.ReleaseCallCount, Is.Zero);
            Assert.That(_service.Diagnostics.InflightOperationCount, Is.EqualTo(1));

            operation.Succeed(asset);
            yield return WaitUntil(() => operation.ReleaseCallCount == 1);

            Assert.That(operation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(_service.Diagnostics.InflightOperationCount, Is.Zero);
            Assert.That(_service.Diagnostics.OutstandingLeases, Is.Empty);
            cancellation.Dispose();
        }

        [UnityTest]
        public IEnumerator Stop_DuringBlockedCanceledCleanup_WaitsThroughRelease()
        {
            var asset = Track(ScriptableObject.CreateInstance<TestAsset>());
            var operation = _backend.EnqueueAsset<TestAsset>();
            var releaseEntered = new ManualResetEventSlim();
            var releaseGate = new ManualResetEventSlim();
            operation.BlockRelease(releaseEntered, releaseGate);
            var cancellation = new CancellationTokenSource();
            var service = _service;
            var loadTask = service
                .LoadAsync<TestAsset>(
                    new ResourceKey("asset/blocked-cleanup"),
                    cancellation.Token)
                .AsTask();

            cancellation.Cancel();
            yield return WaitForTask(loadTask);
            AssertCanceled(loadTask);

            Task stopTask = null;
            ResourceDiagnostics diagnosticsDuringRelease = null;
            var stopCompletedDuringRelease = false;
            var coordinator = Task.Run(
                () =>
                {
                    if (!releaseEntered.Wait(TimeSpan.FromSeconds(5)))
                    {
                        releaseGate.Set();
                        throw new TimeoutException(
                            "Backend release did not enter the blocking gate.");
                    }

                    try
                    {
                        stopTask = service.StopAsync().AsTask();
                        diagnosticsDuringRelease = service.Diagnostics;
                        stopCompletedDuringRelease = stopTask.IsCompleted;
                    }
                    finally
                    {
                        releaseGate.Set();
                    }
                });

            operation.Succeed(asset);
            yield return WaitForTask(coordinator);
            coordinator.GetAwaiter().GetResult();

            Assert.That(stopTask, Is.Not.Null);
            Assert.That(stopCompletedDuringRelease, Is.False);
            Assert.That(
                diagnosticsDuringRelease.InflightOperationCount,
                Is.EqualTo(1));

            yield return WaitForTask(stopTask);
            stopTask.GetAwaiter().GetResult();
            Assert.That(operation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(operation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(
                service.Diagnostics.InflightOperationCount,
                Is.Zero);
            Assert.That(service.Diagnostics.OutstandingLeases, Is.Empty);

            cancellation.Dispose();
            releaseGate.Dispose();
            releaseEntered.Dispose();
        }

        [UnityTest]
        public IEnumerator CanceledCleanupReleaseFailure_IsObservedAndPropagatedToStop()
        {
            var asset = Track(ScriptableObject.CreateInstance<TestAsset>());
            var releaseFailure = new TestReleaseException();
            var operation = _backend.EnqueueAsset<TestAsset>();
            operation.ReleaseException = releaseFailure;
            var cancellation = new CancellationTokenSource();
            var service = _service;
            var loadTask = service
                .LoadAsync<TestAsset>(
                    new ResourceKey("asset/canceled-cleanup-failure"),
                    cancellation.Token)
                .AsTask();

            cancellation.Cancel();
            yield return WaitForTask(loadTask);
            AssertCanceled(loadTask);

            var stopTask = service.StopAsync().AsTask();
            Assert.That(stopTask.IsCompleted, Is.False);
            operation.Succeed(asset);
            yield return WaitForTask(stopTask);

            var stopFailure = Assert.Throws<TestReleaseException>(
                () => stopTask.GetAwaiter().GetResult());
            Assert.That(stopFailure, Is.SameAs(releaseFailure));

            var disposeTask = service.DisposeAsync().AsTask();
            yield return WaitForTask(disposeTask);
            var disposeFailure = Assert.Throws<TestReleaseException>(
                () => disposeTask.GetAwaiter().GetResult());
            Assert.That(disposeFailure, Is.SameAs(releaseFailure));
            _service = null;
            cancellation.Dispose();

            yield return WaitUntil(() => _logger.Errors.Count == 1);
            Assert.That(
                _logger.Errors[0].Exception,
                Is.SameAs(releaseFailure));
            Assert.That(operation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(operation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(
                service.Diagnostics.InflightOperationCount,
                Is.Zero);
            Assert.That(service.Diagnostics.OutstandingLeases, Is.Empty);
        }

        [UnityTest]
        public IEnumerator FailedOperation_ReleasesBackendHandleAndPropagatesOriginalError()
        {
            var failure = new TestBackendException();
            var releaseFailure = new TestReleaseException();
            var operation = _backend.EnqueueAsset<TestAsset>();
            operation.ReleaseException = releaseFailure;
            _logger.ThrowOnError = true;
            var task = _service
                .LoadAsync<TestAsset>(new ResourceKey("asset/failure"))
                .AsTask();

            operation.Fail(failure);
            yield return WaitForTask(task);

            var thrown = Assert.Throws<TestBackendException>(
                () => task.GetAwaiter().GetResult());
            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(operation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(operation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(_logger.Errors.Count, Is.EqualTo(1));
            Assert.That(_logger.Errors[0].Exception, Is.SameAs(releaseFailure));
            Assert.That(_service.Diagnostics.InflightOperationCount, Is.Zero);
            Assert.That(_service.Diagnostics.OutstandingLeases, Is.Empty);
        }

        [UnityTest]
        public IEnumerator Stop_ReleasesOutstandingAssetAndInstanceLeases()
        {
            var asset = Track(ScriptableObject.CreateInstance<TestAsset>());
            var instance = Track(new GameObject("ResourceServiceTests.Instance"));
            var assetOperation = _backend.EnqueueCompletedAsset(asset);
            var instanceOperation = _backend.EnqueueCompletedInstance(instance);
            var assetTask = _service
                .LoadAsync<TestAsset>(new ResourceKey("asset/live"))
                .AsTask();
            var instanceTask = _service
                .InstantiateAsync(new ResourceKey("prefab/live"))
                .AsTask();
            yield return WaitForTask(assetTask);
            yield return WaitForTask(instanceTask);

            var assetLease = assetTask.GetAwaiter().GetResult();
            var instanceLease = instanceTask.GetAwaiter().GetResult();
            var stopTask = _service.StopAsync().AsTask();
            yield return WaitForTask(stopTask);
            stopTask.GetAwaiter().GetResult();

            Assert.That(assetOperation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(instanceOperation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(_service.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.Throws<ObjectDisposedException>(() => _ = assetLease.Asset);
            Assert.Throws<ObjectDisposedException>(() => _ = instanceLease.Instance);
        }

        [UnityTest]
        public IEnumerator Stop_CancelsInflightWaitAndReleasesAfterCompletion()
        {
            var asset = Track(ScriptableObject.CreateInstance<TestAsset>());
            var operation = _backend.EnqueueAsset<TestAsset>();
            var loadTask = _service
                .LoadAsync<TestAsset>(new ResourceKey("asset/inflight"))
                .AsTask();

            var stopTask = _service.StopAsync().AsTask();
            yield return WaitForTask(loadTask);

            AssertCanceled(loadTask);
            Assert.That(stopTask.IsCompleted, Is.False);
            Assert.That(operation.ReleaseCallCount, Is.Zero);

            operation.Succeed(asset);
            yield return WaitForTask(stopTask);
            stopTask.GetAwaiter().GetResult();

            Assert.That(operation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(operation.UnderlyingReleaseCount, Is.EqualTo(1));
            Assert.That(_service.Diagnostics.InflightOperationCount, Is.Zero);
        }

        [Test]
        public void CallsAfterStop_AreRejected()
        {
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var canceledStop = _service.StopAsync(canceled.Token).AsTask();
                AssertCanceled(canceledStop);
            }

            Assert.Throws<ObjectDisposedException>(
                () => Await(
                    _service.LoadAsync<TestAsset>(
                        new ResourceKey("asset/rejected"))));
            Assert.Throws<ObjectDisposedException>(
                () => Await(
                    _service.InstantiateAsync(
                        new ResourceKey("prefab/rejected"))));
            Assert.Throws<ObjectDisposedException>(
                () => Await(
                    _service.LoadByLabelAsync<TestAsset>("rejected")));
            Assert.That(_backend.StartCount, Is.Zero);

            Exception disposeFailure = null;
            try
            {
                Await(_service.DisposeAsync());
            }
            catch (Exception exception)
            {
                disposeFailure = exception;
            }

            _service = null;
            Assert.That(disposeFailure, Is.Null);
        }

        [Test]
        public void Diagnostics_ReturnImmutableOutstandingLeaseSnapshots()
        {
            var asset = Track(ScriptableObject.CreateInstance<TestAsset>());
            var operation = _backend.EnqueueCompletedAsset(asset);
            var beforeLoad = _service.Diagnostics;
            var lease = Await(
                _service.LoadAsync<TestAsset>(new ResourceKey("asset/snapshot")));
            var withLease = _service.Diagnostics;

            Assert.That(beforeLoad.OutstandingLeases, Is.Empty);
            Assert.That(withLease.OutstandingLeases.Count, Is.EqualTo(1));
            var entry = withLease.OutstandingLeases[0];
            Assert.That(entry.LeaseId, Is.EqualTo(lease.LeaseId));
            Assert.That(entry.Kind, Is.EqualTo(ResourceLeaseKind.Asset));
            Assert.That(entry.KeyOrLabel, Is.EqualTo("asset/snapshot"));
            Assert.That(entry.AssetType, Is.EqualTo(typeof(TestAsset)));
            Assert.That(entry.CreatedUtc, Is.EqualTo(lease.CreatedUtc));
            var mutableView =
                (IList<ResourceLeaseDiagnostics>)withLease.OutstandingLeases;
            Assert.Throws<NotSupportedException>(() => mutableView.Add(entry));

            lease.Dispose();

            Assert.That(operation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(withLease.OutstandingLeases.Count, Is.EqualTo(1));
            Assert.That(_service.Diagnostics.OutstandingLeases, Is.Empty);
        }

        [Test]
        public void LoadByLabel_LeasesShareOneBackendRelease()
        {
            var first = Track(ScriptableObject.CreateInstance<TestAsset>());
            var second = Track(ScriptableObject.CreateInstance<TestAsset>());
            var operation =
                _backend.EnqueueCompletedLabel<TestAsset>(first, second);

            var leases = Await(
                _service.LoadByLabelAsync<TestAsset>("test-label"));

            Assert.That(leases.Count, Is.EqualTo(2));
            Assert.That(leases[0].Asset, Is.SameAs(first));
            Assert.That(leases[1].Asset, Is.SameAs(second));
            leases[0].Dispose();
            Assert.That(operation.ReleaseCallCount, Is.Zero);
            leases[1].Dispose();
            Assert.That(operation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(operation.UnderlyingReleaseCount, Is.EqualTo(1));
        }

        [Test]
        public void Stop_LabelLeasesReleaseBackendOperationOnce()
        {
            var first = Track(ScriptableObject.CreateInstance<TestAsset>());
            var second = Track(ScriptableObject.CreateInstance<TestAsset>());
            var operation =
                _backend.EnqueueCompletedLabel<TestAsset>(first, second);
            var leases = Await(
                _service.LoadByLabelAsync<TestAsset>("test-label"));

            Await(_service.StopAsync());
            leases[0].Dispose();
            leases[1].Dispose();

            Assert.That(operation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(operation.UnderlyingReleaseCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator SceneLoadAndUnload_ReleaseBothOperationsExactlyOnce()
        {
            var loadOperation = _backend.EnqueueSceneLoad();
            var unloadOperation = _backend.EnqueueSceneUnload();
            var loadTask = ((ISceneResourceLoader)_service)
                .LoadSceneAsync(
                    new ResourceKey("scene/test"),
                    LoadSceneMode.Additive,
                    true)
                .AsTask();
            loadOperation.Succeed(default);
            yield return WaitForTask(loadTask);

            var lease = loadTask.GetAwaiter().GetResult();
            var unloadTask = ((ISceneResourceLoader)_service)
                .UnloadSceneAsync(lease)
                .AsTask();
            unloadOperation.Succeed(default);
            yield return WaitForTask(unloadTask);
            unloadTask.GetAwaiter().GetResult();

            Assert.That(loadOperation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(unloadOperation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(_service.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.Throws<ObjectDisposedException>(() => _ = lease.Scene);
            Assert.DoesNotThrow(lease.Dispose);

            var firstReleaseCount = 0;
            var attachedReleaseCount = 0;
            var releaseState = new ReleaseCallbackState(
                () => firstReleaseCount++);
            releaseState.Release();
            releaseState.Attach(() => attachedReleaseCount++);
            releaseState.Release();

            Assert.That(firstReleaseCount, Is.EqualTo(1));
            Assert.That(attachedReleaseCount, Is.EqualTo(1));

            var expectedReleaseError =
                new InvalidOperationException("release failed");
            var releaseAfterFailureCount = 0;
            var throwingReleaseState = new ReleaseCallbackState(
                () => throw expectedReleaseError);
            throwingReleaseState.Attach(
                () => releaseAfterFailureCount++);

            var observedReleaseError =
                Assert.Throws<InvalidOperationException>(
                    throwingReleaseState.Release);
            Assert.That(observedReleaseError, Is.SameAs(expectedReleaseError));
            Assert.That(releaseAfterFailureCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator TransactionSceneCancellationWaitsForUnloadCleanup()
        {
            var loadOperation = _backend.EnqueueSceneLoad();
            var unloadOperation = _backend.EnqueueSceneUnload();
            var cancellation = new CancellationTokenSource();
            var task = ((ISceneTransactionResourceLoader)_service)
                .LoadSceneTransactionAsync(
                    new ResourceKey("scene/transaction-cancel"),
                    LoadSceneMode.Additive,
                    false,
                    cancellation.Token)
                .AsTask();

            cancellation.Cancel();
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(
                _service.Diagnostics.InflightOperationCount,
                Is.EqualTo(1));

            loadOperation.Succeed(default);
            yield return WaitUntil(() => _backend.StartCount == 2);
            Assert.That(task.IsCompleted, Is.False);
            Assert.That(
                _service.Diagnostics.OutstandingLeases.Count,
                Is.EqualTo(0));

            unloadOperation.Succeed(default);
            yield return WaitForTask(task);
            AssertCanceled(task);
            Assert.That(loadOperation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(unloadOperation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(
                _service.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(
                _service.Diagnostics.InflightOperationCount,
                Is.Zero);
            cancellation.Dispose();
        }

        [UnityTest]
        public IEnumerator FailedSceneUnloadRestoresLeaseForOwnedRetry()
        {
            var loadOperation = _backend.EnqueueSceneLoad();
            var firstUnload = _backend.EnqueueSceneUnload();
            var load = ((ISceneResourceLoader)_service)
                .LoadSceneAsync(
                    new ResourceKey("scene/retry"),
                    LoadSceneMode.Additive,
                    false)
                .AsTask();
            loadOperation.Succeed(default);
            yield return WaitForTask(load);
            var lease = load.GetAwaiter().GetResult();

            var expected = new TestBackendException();
            var failed = ((ISceneResourceLoader)_service)
                .UnloadSceneAsync(lease)
                .AsTask();
            firstUnload.Fail(expected);
            yield return WaitForTask(failed);
            var observed = Assert.Throws<TestBackendException>(
                () => failed.GetAwaiter().GetResult());
            Assert.That(observed, Is.SameAs(expected));
            Assert.DoesNotThrow(() => _ = lease.Scene);
            Assert.That(
                _service.Diagnostics.OutstandingLeases.Count,
                Is.EqualTo(1));

            var secondUnload = _backend.EnqueueSceneUnload();
            var retry = ((ISceneResourceLoader)_service)
                .UnloadSceneAsync(lease)
                .AsTask();
            secondUnload.Succeed(default);
            yield return WaitForTask(retry);
            retry.GetAwaiter().GetResult();

            Assert.That(
                _service.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(loadOperation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(firstUnload.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(secondUnload.ReleaseCallCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator DisposeDuringFailedSceneUnloadDoesNotReviveLease()
        {
            var loadOperation = _backend.EnqueueSceneLoad();
            var unloadOperation = _backend.EnqueueSceneUnload();
            var load = ((ISceneResourceLoader)_service)
                .LoadSceneAsync(
                    new ResourceKey("scene/dispose-inflight"),
                    LoadSceneMode.Additive,
                    false)
                .AsTask();
            loadOperation.Succeed(default);
            yield return WaitForTask(load);
            var lease = load.GetAwaiter().GetResult();

            var unload = ((ISceneResourceLoader)_service)
                .UnloadSceneAsync(lease)
                .AsTask();
            lease.Dispose();
            var expected = new TestBackendException();
            unloadOperation.Fail(expected);
            yield return WaitForTask(unload);
            var observed = Assert.Throws<TestBackendException>(
                () => unload.GetAwaiter().GetResult());

            Assert.That(observed, Is.SameAs(expected));
            Assert.Throws<ObjectDisposedException>(() => _ = lease.Scene);
            Assert.That(
                _service.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(loadOperation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(unloadOperation.ReleaseCallCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator StopDuringFailedSceneUnloadDoesNotRestoreLease()
        {
            var loadOperation = _backend.EnqueueSceneLoad();
            var unloadOperation = _backend.EnqueueSceneUnload();
            var load = ((ISceneResourceLoader)_service)
                .LoadSceneAsync(
                    new ResourceKey("scene/stop-inflight"),
                    LoadSceneMode.Additive,
                    false)
                .AsTask();
            loadOperation.Succeed(default);
            yield return WaitForTask(load);
            var lease = load.GetAwaiter().GetResult();
            var unload = ((ISceneResourceLoader)_service)
                .UnloadSceneAsync(lease)
                .AsTask();

            var stop = _service.StopAsync().AsTask();
            yield return WaitForTask(unload);
            AssertCanceled(unload);
            unloadOperation.Fail(new TestBackendException());
            yield return WaitForTask(stop);
            stop.GetAwaiter().GetResult();

            Assert.Throws<ObjectDisposedException>(() => _ = lease.Scene);
            Assert.That(
                _service.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(
                _service.Diagnostics.InflightOperationCount,
                Is.Zero);
            Assert.That(loadOperation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(unloadOperation.ReleaseCallCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator TransactionCancellationCleanupFailureIsTerminal()
        {
            var loadOperation = _backend.EnqueueSceneLoad();
            var unloadOperation = _backend.EnqueueSceneUnload();
            var cancellation = new CancellationTokenSource();
            var transaction = ((ISceneTransactionResourceLoader)_service)
                .LoadSceneTransactionAsync(
                    new ResourceKey("scene/cancel-cleanup-failure"),
                    LoadSceneMode.Additive,
                    false,
                    cancellation.Token)
                .AsTask();
            cancellation.Cancel();
            loadOperation.Succeed(default);
            yield return WaitUntil(() => _backend.StartCount == 2);
            var cleanupFailure = new TestBackendException();
            unloadOperation.Fail(cleanupFailure);
            yield return WaitForTask(transaction);

            var aggregate = Assert.Throws<AggregateException>(
                () => transaction.GetAwaiter().GetResult());
            Assert.That(
                aggregate.InnerExceptions[0],
                Is.TypeOf<OperationCanceledException>());
            Assert.That(
                aggregate.InnerExceptions[1],
                Is.SameAs(cleanupFailure));
            Assert.That(
                _service.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(
                _service.Diagnostics.InflightOperationCount,
                Is.Zero);
            Assert.That(loadOperation.ReleaseCallCount, Is.EqualTo(1));
            Assert.That(unloadOperation.ReleaseCallCount, Is.EqualTo(1));
            cancellation.Dispose();
        }

        [UnityTest]
        public IEnumerator ResourceModule_RegistersServiceAndSceneLoaderWithoutDoubleOwnership()
        {
            var runtime = new FrameworkRuntime();
            var module = new ResourceModule();
            var startTask = runtime
                .StartAsync(
                    new[]
                    {
                        new ModuleDescriptor(
                            "Resource",
                            Array.Empty<string>(),
                            0,
                            () => module)
                    },
                    CancellationToken.None)
                .AsTask();
            yield return WaitForTask(startTask);
            startTask.GetAwaiter().GetResult();

            var resourceService = runtime.Services.Resolve<IResourceService>();
            var sceneLoader = runtime.Services.Resolve<ISceneResourceLoader>();
            Assert.That(module.Id, Is.EqualTo("Resource"));
            Assert.That(module.Dependencies, Is.Empty);
            Assert.That(sceneLoader, Is.SameAs(resourceService));

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitForTask(stopTask);
            stopTask.GetAwaiter().GetResult();
            var disposeTask = runtime.DisposeAsync().AsTask();
            yield return WaitForTask(disposeTask);
            disposeTask.GetAwaiter().GetResult();
        }

        private T Track<T>(T value) where T : Object
        {
            _objects.Add(value);
            return value;
        }

        private static IEnumerator WaitForTask(Task task)
        {
            for (var frame = 0; frame < 240 && !task.IsCompleted; frame++)
            {
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True, "Async operation timed out.");
        }

        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            for (var frame = 0; frame < 240 && !condition(); frame++)
            {
                yield return null;
            }

            Assert.That(condition(), Is.True, "Condition timed out.");
        }

        private static void AssertCanceled(Task task)
        {
            Assert.That(task.IsCanceled, Is.True);
            Assert.Catch<OperationCanceledException>(
                () => task.GetAwaiter().GetResult());
        }

        private static void Await(ValueTask task)
        {
            task.AsTask().GetAwaiter().GetResult();
        }

        private static T Await<T>(ValueTask<T> task)
        {
            return task.AsTask().GetAwaiter().GetResult();
        }

        private sealed class TestAsset : ScriptableObject
        {
        }

        private sealed class AlternativeSceneLease : ISceneLease
        {
            public long LeaseId => 1;
            public ResourceKey Key => new ResourceKey("alternative");
            public DateTime CreatedUtc => DateTime.UtcNow;
            public SceneInstance Scene => default;

            public void Dispose()
            {
            }
        }

        private sealed class TestBackendException : Exception
        {
        }

        private sealed class TestReleaseException : Exception
        {
        }

        private sealed class TestLoggerException : Exception
        {
        }

        private sealed class RecordingLogger : IFrameworkLogger
        {
            public List<ErrorRecord> Errors { get; } =
                new List<ErrorRecord>();

            public bool ThrowOnError { get; set; }

            public void Debug(string moduleId, string category, string message)
            {
            }

            public void Info(string moduleId, string category, string message)
            {
            }

            public void Warning(
                string moduleId,
                string category,
                string message)
            {
            }

            public void Error(
                string moduleId,
                string category,
                string message,
                Exception exception)
            {
                Errors.Add(
                    new ErrorRecord(
                        moduleId,
                        category,
                        message,
                        exception));
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
                string message,
                Exception exception)
            {
                ModuleId = moduleId;
                Category = category;
                Message = message;
                Exception = exception;
            }

            public string ModuleId { get; }

            public string Category { get; }

            public string Message { get; }

            public Exception Exception { get; }
        }

        private sealed class FakeResourceBackend : IResourceBackend
        {
            private object _assetOperation;
            private PendingOperation<GameObject> _instanceOperation;
            private object _labelOperation;
            private PendingOperation<SceneInstance> _sceneLoadOperation;
            private PendingOperation<SceneInstance> _sceneUnloadOperation;

            public int StartCount { get; private set; }

            public PendingOperation<T> EnqueueAsset<T>() where T : Object
            {
                var operation = new PendingOperation<T>();
                _assetOperation = operation;
                return operation;
            }

            public PendingOperation<T> EnqueueCompletedAsset<T>(T asset)
                where T : Object
            {
                var operation = EnqueueAsset<T>();
                operation.Succeed(asset);
                return operation;
            }

            public PendingOperation<GameObject> EnqueueCompletedInstance(
                GameObject instance)
            {
                _instanceOperation = new PendingOperation<GameObject>();
                _instanceOperation.Succeed(instance);
                return _instanceOperation;
            }

            public PendingOperation<IReadOnlyList<T>> EnqueueCompletedLabel<T>(
                params T[] assets)
                where T : Object
            {
                var operation =
                    new PendingOperation<IReadOnlyList<T>>();
                _labelOperation = operation;
                operation.Succeed(Array.AsReadOnly(assets));
                return operation;
            }

            public PendingOperation<SceneInstance> EnqueueSceneLoad()
            {
                _sceneLoadOperation = new PendingOperation<SceneInstance>();
                return _sceneLoadOperation;
            }

            public PendingOperation<SceneInstance> EnqueueSceneUnload()
            {
                _sceneUnloadOperation = new PendingOperation<SceneInstance>();
                return _sceneUnloadOperation;
            }

            public IResourceOperation<T> LoadAssetAsync<T>(ResourceKey key)
                where T : Object
            {
                StartCount++;
                return Take<T>(ref _assetOperation, "asset");
            }

            public IResourceOperation<GameObject> InstantiateAsync(
                ResourceKey key,
                Transform parent)
            {
                StartCount++;
                return Take(ref _instanceOperation, "instance");
            }

            public IResourceOperation<IReadOnlyList<T>> LoadByLabelAsync<T>(
                string label)
                where T : Object
            {
                StartCount++;
                return Take<IReadOnlyList<T>>(ref _labelOperation, "label");
            }

            public IResourceOperation<SceneInstance> LoadSceneAsync(
                ResourceKey key,
                LoadSceneMode mode,
                bool activateOnLoad)
            {
                StartCount++;
                return Take(ref _sceneLoadOperation, "scene load");
            }

            public IResourceOperation<SceneInstance> UnloadSceneAsync(
                SceneInstance scene)
            {
                StartCount++;
                return Take(ref _sceneUnloadOperation, "scene unload");
            }

            private static PendingOperation<T> Take<T>(
                ref object operation,
                string kind)
            {
                var typed = operation as PendingOperation<T>;
                operation = null;
                if (typed == null)
                {
                    throw new InvalidOperationException(
                        $"No {kind} operation was enqueued.");
                }

                return typed;
            }

            private static PendingOperation<T> Take<T>(
                ref PendingOperation<T> operation,
                string kind)
            {
                var value = operation;
                operation = null;
                if (value == null)
                {
                    throw new InvalidOperationException(
                        $"No {kind} operation was enqueued.");
                }

                return value;
            }
        }

        private sealed class PendingOperation<T> : IResourceOperation<T>
        {
            private readonly TaskCompletionSource<T> _completion =
                new TaskCompletionSource<T>();
            private int _released;
            private int _releaseCallCount;
            private int _underlyingReleaseCount;
            private ManualResetEventSlim _releaseEntered;
            private ManualResetEventSlim _releaseGate;

            public Task<T> Task => _completion.Task;

            public int ReleaseCallCount =>
                Volatile.Read(ref _releaseCallCount);

            public int UnderlyingReleaseCount =>
                Volatile.Read(ref _underlyingReleaseCount);

            public Exception ReleaseException { get; set; }

            public void BlockRelease(
                ManualResetEventSlim releaseEntered,
                ManualResetEventSlim releaseGate)
            {
                _releaseEntered = releaseEntered;
                _releaseGate = releaseGate;
            }

            public void Succeed(T value)
            {
                _completion.SetResult(value);
            }

            public void Fail(Exception exception)
            {
                _completion.SetException(exception);
            }

            public void Release()
            {
                Interlocked.Increment(ref _releaseCallCount);
                if (Interlocked.Exchange(ref _released, 1) == 0)
                {
                    Interlocked.Increment(ref _underlyingReleaseCount);
                    if (_releaseEntered != null)
                    {
                        _releaseEntered.Set();
                        _releaseGate.Wait();
                    }

                    if (ReleaseException != null)
                    {
                        throw ReleaseException;
                    }
                }
            }
        }
    }
}
