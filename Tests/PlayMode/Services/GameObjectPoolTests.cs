using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
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
    public sealed class GameObjectPoolTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private FakeResourceBackend _backend;
        private ResourceService _resourceService;
        private GameObjectPool _pool;

        [SetUp]
        public void SetUp()
        {
            _backend = new FakeResourceBackend();
            _resourceService = new ResourceService(_backend);
            _pool = new GameObjectPool(
                _resourceService,
                defaultMaxIdleCapacity: 1);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_pool != null)
            {
                _pool.Dispose();
                _pool = null;
            }

            if (_resourceService != null)
            {
                var stopTask = _resourceService.StopAsync().AsTask();
                yield return WaitForTask(stopTask);
                stopTask.GetAwaiter().GetResult();
                var disposeTask = _resourceService.DisposeAsync().AsTask();
                yield return WaitForTask(disposeTask);
                disposeTask.GetAwaiter().GetResult();
                _resourceService = null;
            }

            for (var index = _objects.Count - 1; index >= 0; index--)
            {
                if (_objects[index] != null)
                {
                    Object.Destroy(_objects[index]);
                }
            }

            _objects.Clear();
            yield return null;
        }

        [UnityTest]
        public IEnumerator RentAsync_InstantiatesAndAppliesTransformBeforeOnRent()
        {
            var events = new List<string>();
            var key = new ResourceKey("prefab/ordered");
            RecordingPoolable recorder = null;
            _backend.Register(
                key,
                () =>
                {
                    var instance = new GameObject("Ordered");
                    instance.SetActive(false);
                    recorder = instance.AddComponent<RecordingPoolable>();
                    recorder.Events = events;
                    return instance;
                },
                events);
            var parent = Track(new GameObject("TargetParent")).transform;
            var position = new Vector3(3f, 4f, 5f);
            var rotation = Quaternion.Euler(10f, 20f, 30f);

            var task = _pool
                .RentAsync(key, parent, position, rotation)
                .AsTask();
            yield return WaitForTask(task);
            var handle = task.GetAwaiter().GetResult();
            var instance = handle.Instance;

            Assert.That(_backend.GetInstantiateCount(key), Is.EqualTo(1));
            Assert.That(instance.transform.parent, Is.SameAs(parent));
            Assert.That(instance.transform.position, Is.EqualTo(position));
            Assert.That(
                Quaternion.Angle(instance.transform.rotation, rotation),
                Is.LessThan(0.01f));
            Assert.That(instance.activeSelf, Is.True);
            Assert.That(recorder.RentParent, Is.SameAs(parent));
            Assert.That(recorder.RentPosition, Is.EqualTo(position));
            Assert.That(
                Quaternion.Angle(recorder.RentRotation, rotation),
                Is.LessThan(0.01f));
            Assert.That(recorder.WasActiveOnRent, Is.True);
            Assert.That(events, Is.EqualTo(new[] { "instantiate", "rent" }));

            handle.Dispose();

            Assert.That(recorder.WasActiveOnReturn, Is.True);
            Assert.That(recorder.ReturnParent, Is.SameAs(parent));
            Assert.That(instance.activeSelf, Is.False);
            Assert.That(instance.transform.parent, Is.Not.Null);
            Assert.That(instance.transform.parent, Is.Not.SameAs(parent));
            Assert.That(instance.transform.parent.gameObject.activeSelf, Is.False);
            Assert.That(
                instance.transform.parent.gameObject.hideFlags,
                Is.EqualTo(HideFlags.HideAndDontSave));
            Assert.That(
                events,
                Is.EqualTo(new[] { "instantiate", "rent", "return" }));
            Assert.Throws<ObjectDisposedException>(() => _ = handle.Instance);
            handle.Dispose();
        }

        [UnityTest]
        public IEnumerator ReturnedInstance_IsReusedWithoutAnotherInstantiation()
        {
            var key = new ResourceKey("prefab/reused");
            _backend.Register(key, () => new GameObject("Reused"));

            var firstTask = _pool.RentAsync(key).AsTask();
            yield return WaitForTask(firstTask);
            var firstHandle = firstTask.GetAwaiter().GetResult();
            var firstInstance = firstHandle.Instance;
            firstHandle.Dispose();

            var secondTask = _pool.RentAsync(key).AsTask();
            yield return WaitForTask(secondTask);
            var secondHandle = secondTask.GetAwaiter().GetResult();

            Assert.That(secondHandle.Instance, Is.SameAs(firstInstance));
            Assert.That(_backend.GetInstantiateCount(key), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases.Count,
                Is.EqualTo(1));

            secondHandle.Dispose();
        }

        [UnityTest]
        public IEnumerator IdleCapacityAndClear_ReleaseInstanceLeases()
        {
            var key = new ResourceKey("prefab/capacity");
            _backend.Register(key, () => new GameObject("Capacity"));
            var firstTask = _pool.RentAsync(key).AsTask();
            var secondTask = _pool.RentAsync(key).AsTask();
            yield return WaitForTask(firstTask);
            yield return WaitForTask(secondTask);
            var first = firstTask.GetAwaiter().GetResult();
            var second = secondTask.GetAwaiter().GetResult();

            first.Dispose();
            second.Dispose();

            Assert.That(_backend.GetInstantiateCount(key), Is.EqualTo(2));
            Assert.That(_backend.GetReleaseCount(key), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases.Count,
                Is.EqualTo(1));

            _pool.Clear(key);

            Assert.That(_backend.GetReleaseCount(key), Is.EqualTo(2));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases,
                Is.Empty);
        }

        [UnityTest]
        public IEnumerator ClearAll_ReleasesActiveAndIdleAndInvalidatesOldHandles()
        {
            var firstKey = new ResourceKey("prefab/first");
            var secondKey = new ResourceKey("prefab/second");
            _backend.Register(firstKey, () => new GameObject("First"));
            _backend.Register(secondKey, () => new GameObject("Second"));
            var activeTask = _pool.RentAsync(firstKey).AsTask();
            var idleTask = _pool.RentAsync(secondKey).AsTask();
            yield return WaitForTask(activeTask);
            yield return WaitForTask(idleTask);
            var active = activeTask.GetAwaiter().GetResult();
            var idle = idleTask.GetAwaiter().GetResult();
            idle.Dispose();

            _pool.Clear(firstKey);
            Assert.That(_backend.GetReleaseCount(firstKey), Is.Zero);
            Assert.That(active.Instance, Is.Not.Null);

            _pool.ClearAll();

            Assert.That(_backend.GetReleaseCount(firstKey), Is.EqualTo(1));
            Assert.That(_backend.GetReleaseCount(secondKey), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.Throws<ObjectDisposedException>(() => _ = active.Instance);
            active.Dispose();
            active.Dispose();

            var replacementTask = _pool.RentAsync(firstKey).AsTask();
            yield return WaitForTask(replacementTask);
            var replacement = replacementTask.GetAwaiter().GetResult();
            Assert.That(_backend.GetInstantiateCount(firstKey), Is.EqualTo(2));
            replacement.Dispose();
        }

        [UnityTest]
        public IEnumerator ClearKey_DuringInflightRent_DoesNotDetachCompletedLease()
        {
            var key = new ResourceKey("prefab/inflight-clear");
            _backend.RegisterPending(
                key,
                () => new GameObject("InflightClear"));

            var rentTask = _pool.RentAsync(key).AsTask();
            Assert.That(rentTask.IsCompleted, Is.False);

            _pool.Clear(key);
            _backend.CompletePending(key);
            yield return WaitForTask(rentTask);
            var handle = rentTask.GetAwaiter().GetResult();
            handle.Dispose();

            _pool.ClearAll();

            Assert.That(_backend.GetReleaseCount(key), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases,
                Is.Empty);
        }

        [UnityTest]
        public IEnumerator PoolableCallbackFailures_CloseLeaseOwnership()
        {
            var rentKey = new ResourceKey("prefab/rent-failure");
            var returnKey = new ResourceKey("prefab/return-failure");
            var rentFailure = new TestPoolableException("rent");
            var returnFailure = new TestPoolableException("return");
            _backend.Register(
                rentKey,
                () =>
                {
                    var instance = new GameObject("RentFailure");
                    var poolable = instance.AddComponent<RecordingPoolable>();
                    poolable.RentFailure = rentFailure;
                    return instance;
                });
            _backend.Register(
                returnKey,
                () =>
                {
                    var instance = new GameObject("ReturnFailure");
                    var poolable = instance.AddComponent<RecordingPoolable>();
                    poolable.ReturnFailure = returnFailure;
                    return instance;
                });

            var failedRentTask = _pool.RentAsync(rentKey).AsTask();
            yield return WaitForTask(failedRentTask);
            var thrownRent = Assert.Throws<TestPoolableException>(
                () => failedRentTask.GetAwaiter().GetResult());
            Assert.That(thrownRent, Is.SameAs(rentFailure));
            Assert.That(_backend.GetReleaseCount(rentKey), Is.EqualTo(1));

            var returnTask = _pool.RentAsync(returnKey).AsTask();
            yield return WaitForTask(returnTask);
            var handle = returnTask.GetAwaiter().GetResult();
            var thrownReturn = Assert.Throws<TestPoolableException>(
                () => handle.Dispose());
            Assert.That(thrownReturn, Is.SameAs(returnFailure));
            Assert.That(_backend.GetReleaseCount(returnKey), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.Throws<ObjectDisposedException>(() => _ = handle.Instance);
            handle.Dispose();
        }

        [UnityTest]
        public IEnumerator OnRent_ClearAll_ReleasesRentingLease()
        {
            var key = new ResourceKey("prefab/rent-clear-all");
            _backend.Register(
                key,
                () =>
                {
                    var instance = new GameObject("RentClearAll");
                    var poolable = instance.AddComponent<RecordingPoolable>();
                    poolable.RentAction = () => _pool.ClearAll();
                    return instance;
                });

            var rentTask = _pool.RentAsync(key).AsTask();
            yield return WaitForTask(rentTask);

            var failure = Assert.Throws<InvalidOperationException>(
                () => rentTask.GetAwaiter().GetResult());
            Assert.That(failure.Message, Does.Contain("cleared during OnRent"));
            Assert.That(_backend.GetReleaseCount(key), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases,
                Is.Empty);
        }

        [UnityTest]
        public IEnumerator NewRent_OnEnableClearAll_ReleasesLease()
        {
            var key = new ResourceKey("prefab/lifecycle-new-enable");
            _backend.Register(
                key,
                () =>
                {
                    var instance = new GameObject("LifecycleNewEnable");
                    instance.SetActive(false);
                    var lifecycle =
                        instance.AddComponent<LifecyclePoolReentrant>();
                    lifecycle.EnableAction = () => _pool.ClearAll();
                    return instance;
                });

            var rentTask = _pool.RentAsync(key).AsTask();
            yield return WaitForTask(rentTask);

            Assert.Throws<InvalidOperationException>(
                () => rentTask.GetAwaiter().GetResult());
            Assert.That(_backend.GetReleaseCount(key), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases,
                Is.Empty);
        }

        [UnityTest]
        public IEnumerator IdleReuse_OnEnableClearAll_ReleasesLease()
        {
            var key = new ResourceKey("prefab/lifecycle-idle-enable");
            LifecyclePoolReentrant lifecycle = null;
            _backend.Register(
                key,
                () =>
                {
                    var instance = new GameObject("LifecycleIdleEnable");
                    instance.SetActive(false);
                    lifecycle =
                        instance.AddComponent<LifecyclePoolReentrant>();
                    return instance;
                });

            var firstTask = _pool.RentAsync(key).AsTask();
            yield return WaitForTask(firstTask);
            var first = firstTask.GetAwaiter().GetResult();
            first.Dispose();
            lifecycle.EnableAction = () => _pool.ClearAll();

            var secondTask = _pool.RentAsync(key).AsTask();
            yield return WaitForTask(secondTask);

            Assert.Throws<InvalidOperationException>(
                () => secondTask.GetAwaiter().GetResult());
            Assert.That(_backend.GetInstantiateCount(key), Is.EqualTo(1));
            Assert.That(_backend.GetReleaseCount(key), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases,
                Is.Empty);
        }

        [UnityTest]
        public IEnumerator NewRent_OnTransformParentChangedClearAll_ReleasesLease()
        {
            var key = new ResourceKey("prefab/lifecycle-new-parent");
            _backend.Register(
                key,
                () =>
                {
                    var instance = new GameObject("LifecycleNewParent");
                    instance.SetActive(false);
                    var lifecycle =
                        instance.AddComponent<LifecyclePoolReentrant>();
                    lifecycle.ParentChangesToIgnore = 1;
                    lifecycle.ParentChangedAction = () => _pool.ClearAll();
                    return instance;
                });
            var parent = Track(
                new GameObject("LifecycleNewParentTarget")).transform;

            var rentTask = _pool.RentAsync(key, parent).AsTask();
            yield return WaitForTask(rentTask);

            Assert.Throws<InvalidOperationException>(
                () => rentTask.GetAwaiter().GetResult());
            Assert.That(_backend.GetReleaseCount(key), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases,
                Is.Empty);
        }

        [UnityTest]
        public IEnumerator Return_OnDisableClearAll_LeavesEntryDestroyedAndReleasesOnce()
        {
            var key = new ResourceKey("prefab/lifecycle-return-disable");
            LifecyclePoolReentrant lifecycle = null;
            _backend.Register(
                key,
                () =>
                {
                    var instance = new GameObject("LifecycleReturnDisable");
                    instance.SetActive(false);
                    lifecycle =
                        instance.AddComponent<LifecyclePoolReentrant>();
                    return instance;
                });
            var rentTask = _pool.RentAsync(key).AsTask();
            yield return WaitForTask(rentTask);
            var handle = rentTask.GetAwaiter().GetResult();
            var entry = GetHandleEntry(handle);
            lifecycle.DisableAction = () => _pool.ClearAll();

            handle.Dispose();

            Assert.That(GetEntryState(entry), Is.EqualTo("Destroyed"));
            Assert.That(GetEntryIdleCount(entry), Is.Zero);
            Assert.That(_backend.GetReleaseCount(key), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases,
                Is.Empty);
            handle.Dispose();
            Assert.That(_backend.GetReleaseCount(key), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator OnReturn_ClearAll_ReleasesReturningAndIdleLeases()
        {
            var returningKey = new ResourceKey("prefab/return-clear-all");
            var idleKey = new ResourceKey("prefab/idle-during-clear-all");
            _backend.Register(
                returningKey,
                () =>
                {
                    var instance = new GameObject("ReturnClearAll");
                    var poolable = instance.AddComponent<RecordingPoolable>();
                    poolable.ReturnAction = () => _pool.ClearAll();
                    return instance;
                });
            _backend.Register(idleKey, () => new GameObject("Idle"));
            var returningTask = _pool.RentAsync(returningKey).AsTask();
            var idleTask = _pool.RentAsync(idleKey).AsTask();
            yield return WaitForTask(returningTask);
            yield return WaitForTask(idleTask);
            var returning = returningTask.GetAwaiter().GetResult();
            var idle = idleTask.GetAwaiter().GetResult();
            idle.Dispose();

            returning.Dispose();

            Assert.That(
                _backend.GetReleaseCount(returningKey),
                Is.EqualTo(1));
            Assert.That(_backend.GetReleaseCount(idleKey), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.Throws<ObjectDisposedException>(
                () => _ = returning.Instance);
            returning.Dispose();
        }

        [UnityTest]
        public IEnumerator ClearAll_OnReturnRentNewKey_RejectsReentryAndReleasesEveryLease()
        {
            var callbackKey = new ResourceKey("prefab/clear-callback");
            var idleKey = new ResourceKey("prefab/clear-idle");
            var reentrantKey = new ResourceKey("prefab/clear-reentrant");
            RecordingPoolable callback = null;
            _backend.Register(
                callbackKey,
                () =>
                {
                    var instance = new GameObject("ClearCallback");
                    callback = instance.AddComponent<RecordingPoolable>();
                    callback.ReturnAction = () => _pool
                        .RentAsync(reentrantKey)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                    return instance;
                });
            _backend.Register(idleKey, () => new GameObject("ClearIdle"));
            _backend.Register(
                reentrantKey,
                () => new GameObject("ClearReentrant"));
            var callbackTask = _pool.RentAsync(callbackKey).AsTask();
            var idleTask = _pool.RentAsync(idleKey).AsTask();
            yield return WaitForTask(callbackTask);
            yield return WaitForTask(idleTask);
            var callbackHandle = callbackTask.GetAwaiter().GetResult();
            var idleHandle = idleTask.GetAwaiter().GetResult();
            idleHandle.Dispose();

            var failure = Assert.Throws<InvalidOperationException>(
                () => _pool.ClearAll());
            callback.ReturnAction = null;

            Assert.That(
                failure.Message,
                Does.Contain("ClearAll is in progress"));
            Assert.That(_backend.GetInstantiateCount(reentrantKey), Is.Zero);
            Assert.That(_backend.GetReleaseCount(callbackKey), Is.EqualTo(1));
            Assert.That(_backend.GetReleaseCount(idleKey), Is.EqualTo(1));
            Assert.That(
                _resourceService.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.Throws<ObjectDisposedException>(
                () => _ = callbackHandle.Instance);
            callbackHandle.Dispose();
        }

        [UnityTest]
        public IEnumerator Return_RejectsHandleOwnedByDifferentPool()
        {
            var key = new ResourceKey("prefab/ownership");
            _backend.Register(key, () => new GameObject("Ownership"));
            var otherPool = new GameObjectPool(_resourceService);
            try
            {
                var task = _pool.RentAsync(key).AsTask();
                yield return WaitForTask(task);
                var handle = task.GetAwaiter().GetResult();

                Assert.Throws<ArgumentException>(
                    () => otherPool.Return(handle));
                Assert.That(handle.Instance, Is.Not.Null);

                handle.Dispose();
            }
            finally
            {
                otherPool.Dispose();
            }
        }

        [Test]
        public void Return_RejectsForeignHandleImplementation()
        {
            Assert.Throws<ArgumentException>(
                () => _pool.Return(new ForeignPooledHandle()));
        }

        [UnityTest]
        public IEnumerator PoolModule_StopClearsBeforeResourceAndScopeOwnsPool()
        {
            _pool.Dispose();
            _pool = null;
            var events = new List<string>();
            var backend = new FakeResourceBackend();
            var key = new ResourceKey("prefab/module");
            backend.Register(key, () => new GameObject("Module"), events);
            var resourceModule = new TestResourceModule(backend, events);
            var poolModule = new PoolModule();
            Assert.That(poolModule.Id, Is.EqualTo("Pool"));
            Assert.That(poolModule.Dependencies, Is.EqualTo(new[] { "Resource" }));
            var runtime = new FrameworkRuntime();
            var descriptors = new[]
            {
                new ModuleDescriptor(
                    "Pool",
                    new[] { "Resource" },
                    1,
                    () => poolModule),
                new ModuleDescriptor(
                    "Resource",
                    Array.Empty<string>(),
                    0,
                    () => resourceModule)
            };

            var startTask = runtime
                .StartAsync(descriptors, CancellationToken.None)
                .AsTask();
            yield return WaitForTask(startTask);
            startTask.GetAwaiter().GetResult();
            var modulePool = runtime.Services.Resolve<IGameObjectPool>();
            var rentTask = modulePool.RentAsync(key).AsTask();
            yield return WaitForTask(rentTask);
            var handle = rentTask.GetAwaiter().GetResult();

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitForTask(stopTask);
            stopTask.GetAwaiter().GetResult();

            Assert.That(
                events,
                Is.EqualTo(
                    new[]
                    {
                        "instantiate",
                        "release",
                        "resource-stop"
                    }));
            Assert.That(backend.GetReleaseCount(key), Is.EqualTo(1));
            Assert.Throws<ObjectDisposedException>(() => _ = handle.Instance);
            handle.Dispose();
            Assert.Throws<InvalidOperationException>(
                () => runtime.Services.Resolve<IGameObjectPool>());

            var disposeTask = runtime.DisposeAsync().AsTask();
            yield return WaitForTask(disposeTask);
            disposeTask.GetAwaiter().GetResult();
            Assert.That(backend.GetReleaseCount(key), Is.EqualTo(1));
        }

        private GameObject Track(GameObject instance)
        {
            _objects.Add(instance);
            return instance;
        }

        private static object GetHandleEntry(IPooledGameObjectHandle handle)
        {
            Assert.That(handle, Is.TypeOf<PooledGameObjectHandle>());
            return typeof(PooledGameObjectHandle)
                .GetField(
                    "_entry",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue((PooledGameObjectHandle)handle);
        }

        private sealed class ForeignPooledHandle : IPooledGameObjectHandle
        {
            public GameObject Instance => null;

            public void Dispose()
            {
            }
        }

        private static string GetEntryState(object entry)
        {
            return entry
                .GetType()
                .GetProperty(
                    "State",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(entry)
                ?.ToString();
        }

        private static int GetEntryIdleCount(object entry)
        {
            var pool = entry
                .GetType()
                .GetProperty(
                    "Pool",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(entry);
            var idle = pool
                ?.GetType()
                .GetProperty(
                    "Idle",
                    BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(pool) as ICollection;
            return idle?.Count ?? -1;
        }

        private static IEnumerator WaitForTask(Task task)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted &&
                   elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                yield return null;
            }

            elapsed.Stop();
            Assert.That(
                task.IsCompleted,
                Is.True,
                "Async operation timed out after " +
                elapsed.Elapsed.TotalSeconds.ToString("F3") +
                " real seconds.");
        }

        private sealed class FakeResourceBackend : IResourceBackend
        {
            private readonly Dictionary<ResourceKey, Registration>
                _registrations =
                    new Dictionary<ResourceKey, Registration>();

            public void Register(
                ResourceKey key,
                Func<GameObject> factory,
                List<string> events = null)
            {
                _registrations.Add(
                    key,
                    new Registration(factory, events));
            }

            public void RegisterPending(
                ResourceKey key,
                Func<GameObject> factory)
            {
                var registration = new Registration(factory, null);
                registration.IsPending = true;
                _registrations.Add(key, registration);
            }

            public void CompletePending(ResourceKey key)
            {
                _registrations[key].PendingOperation.Succeed();
            }

            public int GetInstantiateCount(ResourceKey key)
            {
                return _registrations[key].InstantiateCount;
            }

            public int GetReleaseCount(ResourceKey key)
            {
                return _registrations[key].ReleaseCount;
            }

            public IResourceOperation<GameObject> InstantiateAsync(
                ResourceKey key,
                Transform parent)
            {
                var registration = _registrations[key];
                registration.InstantiateCount++;
                var instance = registration.Factory();
                instance.transform.SetParent(parent, false);
                registration.Events?.Add("instantiate");
                Action release = () =>
                {
                    registration.ReleaseCount++;
                    registration.Events?.Add("release");
                    if (instance != null)
                    {
                        Object.Destroy(instance);
                    }
                };
                if (registration.IsPending)
                {
                    var pending =
                        new PendingOperation<GameObject>(instance, release);
                    registration.PendingOperation = pending;
                    return pending;
                }

                return new CompletedOperation<GameObject>(instance, release);
            }

            public IResourceOperation<T> LoadAssetAsync<T>(ResourceKey key)
                where T : Object
            {
                throw new NotSupportedException();
            }

            public IResourceOperation<IReadOnlyList<T>> LoadByLabelAsync<T>(
                string label)
                where T : Object
            {
                throw new NotSupportedException();
            }

            public IResourceOperation<SceneInstance> LoadSceneAsync(
                ResourceKey key,
                LoadSceneMode mode,
                bool activateOnLoad)
            {
                throw new NotSupportedException();
            }

            public IResourceOperation<SceneInstance> UnloadSceneAsync(
                SceneInstance scene)
            {
                throw new NotSupportedException();
            }

            private sealed class Registration
            {
                public Registration(
                    Func<GameObject> factory,
                    List<string> events)
                {
                    Factory = factory ??
                        throw new ArgumentNullException(nameof(factory));
                    Events = events;
                }

                public Func<GameObject> Factory { get; }

                public List<string> Events { get; }

                public int InstantiateCount { get; set; }

                public int ReleaseCount { get; set; }

                public bool IsPending { get; set; }

                public PendingOperation<GameObject> PendingOperation { get; set; }
            }
        }

        private sealed class CompletedOperation<T> : IResourceOperation<T>
        {
            private readonly Action _release;
            private bool _released;

            public CompletedOperation(T result, Action release)
            {
                Task = System.Threading.Tasks.Task.FromResult(result);
                _release = release ??
                    throw new ArgumentNullException(nameof(release));
            }

            public Task<T> Task { get; }

            public void Release()
            {
                if (_released)
                {
                    return;
                }

                _released = true;
                _release();
            }
        }

        private sealed class PendingOperation<T> : IResourceOperation<T>
        {
            private readonly T _result;
            private readonly Action _release;
            private readonly TaskCompletionSource<T> _completion =
                new TaskCompletionSource<T>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private bool _released;

            public PendingOperation(T result, Action release)
            {
                _result = result;
                _release = release ??
                    throw new ArgumentNullException(nameof(release));
            }

            public Task<T> Task => _completion.Task;

            public void Succeed()
            {
                _completion.TrySetResult(_result);
            }

            public void Release()
            {
                if (_released)
                {
                    return;
                }

                _released = true;
                _release();
            }
        }

        private sealed class TestResourceModule : IFrameworkModule
        {
            private static readonly IReadOnlyCollection<string> NoDependencies =
                Array.Empty<string>();

            private readonly FakeResourceBackend _backend;
            private readonly List<string> _events;
            private ResourceService _service;

            public TestResourceModule(
                FakeResourceBackend backend,
                List<string> events)
            {
                _backend = backend;
                _events = events;
            }

            public string Id => "Resource";

            public IReadOnlyCollection<string> Dependencies => NoDependencies;

            public ValueTask InitializeAsync(
                ModuleContext context,
                CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                _service = new ResourceService(_backend);
                context.ModuleScope.Own(_service);
                context.ModuleScope.RegisterInstance<IResourceService>(_service);
                return default;
            }

            public ValueTask StartAsync(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return default;
            }

            public ValueTask StopAsync(CancellationToken token)
            {
                _events.Add("resource-stop");
                return _service.StopAsync(token);
            }

            public ValueTask DisposeAsync()
            {
                _service = null;
                return default;
            }
        }
    }

    internal sealed class RecordingPoolable : MonoBehaviour, IPoolable
    {
        public List<string> Events { get; set; }

        public Exception RentFailure { get; set; }

        public Exception ReturnFailure { get; set; }

        public Action RentAction { get; set; }

        public Action ReturnAction { get; set; }

        public Transform RentParent { get; private set; }

        public Vector3 RentPosition { get; private set; }

        public Quaternion RentRotation { get; private set; }

        public bool WasActiveOnRent { get; private set; }

        public Transform ReturnParent { get; private set; }

        public bool WasActiveOnReturn { get; private set; }

        public void OnRent()
        {
            Events?.Add("rent");
            RentParent = transform.parent;
            RentPosition = transform.position;
            RentRotation = transform.rotation;
            WasActiveOnRent = gameObject.activeSelf;
            RentAction?.Invoke();
            if (RentFailure != null)
            {
                throw RentFailure;
            }
        }

        public void OnReturn()
        {
            Events?.Add("return");
            ReturnParent = transform.parent;
            WasActiveOnReturn = gameObject.activeSelf;
            ReturnAction?.Invoke();
            if (ReturnFailure != null)
            {
                throw ReturnFailure;
            }
        }
    }

    internal sealed class LifecyclePoolReentrant : MonoBehaviour
    {
        public Action EnableAction;

        public Action DisableAction;

        public Action ParentChangedAction;

        public int ParentChangesToIgnore;

        private void OnEnable()
        {
            InvokeOnce(ref EnableAction);
        }

        private void OnDisable()
        {
            InvokeOnce(ref DisableAction);
        }

        private void OnTransformParentChanged()
        {
            if (ParentChangesToIgnore > 0)
            {
                ParentChangesToIgnore--;
                return;
            }

            InvokeOnce(ref ParentChangedAction);
        }

        private static void InvokeOnce(ref Action action)
        {
            var callback = action;
            action = null;
            callback?.Invoke();
        }
    }

    internal sealed class TestPoolableException : Exception
    {
        public TestPoolableException(string message)
            : base(message)
        {
        }
    }
}
