using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    public sealed class DomainReloadResetTests
    {
        private const double TimeoutSeconds = 10d;

        private readonly List<Object> _createdObjects = new List<Object>();
        private readonly List<UIRoot> _createdRoots = new List<UIRoot>();
        private readonly List<CycleResourceBackend> _resourceBackends =
            new List<CycleResourceBackend>();
        private readonly List<PendingOperation<CycleAsset>>
            _pendingOperations =
                new List<PendingOperation<CycleAsset>>();
        private readonly List<Task<IAssetLease<CycleAsset>>>
            _resourceLeaseTasks =
                new List<Task<IAssetLease<CycleAsset>>>();
        private readonly List<IAssetLease<CycleAsset>> _resourceLeases =
            new List<IAssetLease<CycleAsset>>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            FrameworkStaticReset.Reset();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            var failures = new List<Exception>();
            for (var index = 0; index < _resourceBackends.Count; index++)
            {
                try
                {
                    _resourceBackends[index].CompleteInflight();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            for (var index = 0; index < _resourceLeaseTasks.Count; index++)
            {
                var leaseTask = _resourceLeaseTasks[index];
                yield return WaitForCleanupTask(
                    leaseTask,
                    "Resource lease cleanup wait timed out.",
                    failures,
                    swallowTaskFailure: true);
                if (leaseTask.Status == TaskStatus.RanToCompletion &&
                    leaseTask.Result != null &&
                    !_resourceLeases.Contains(leaseTask.Result))
                {
                    _resourceLeases.Add(leaseTask.Result);
                }
            }

            for (var index = 0; index < _resourceLeases.Count; index++)
            {
                try
                {
                    _resourceLeases[index]?.Dispose();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            for (var index = 0; index < _createdRoots.Count; index++)
            {
                var root = _createdRoots[index];
                if (root == null)
                {
                    continue;
                }

                Task dispose = null;
                try
                {
                    dispose = root.DisposeAsync().AsTask();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }

                if (dispose != null)
                {
                    yield return WaitForCleanupTask(
                        dispose,
                        "UIRoot cleanup timed out.",
                        failures);
                }
            }

            for (var index = 0; index < _createdObjects.Count; index++)
            {
                if (!(_createdObjects[index] is GameObject gameObject) ||
                    gameObject == null)
                {
                    continue;
                }

                var host = gameObject.GetComponent<FrameworkHost>();
                if (host != null && host.Runtime != null)
                {
                    Task stop = null;
                    try
                    {
                        stop = host.StopRuntimeAsync().AsTask();
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }

                    if (stop != null)
                    {
                        yield return WaitForCleanupTask(
                            stop,
                            "FrameworkHost cleanup timed out.",
                            failures);
                    }
                }
            }

            for (var index = _createdObjects.Count - 1; index >= 0; index--)
            {
                var created = _createdObjects[index];
                if (created != null)
                {
                    try
                    {
                        Object.Destroy(created);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(exception);
                    }
                }
            }

            yield return null;
            try
            {
                FrameworkStaticReset.Reset();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            _resourceBackends.Clear();
            _pendingOperations.Clear();
            _resourceLeaseTasks.Clear();
            _resourceLeases.Clear();
            _createdRoots.Clear();
            _createdObjects.Clear();
            if (failures.Count != 0)
            {
                throw new AggregateException(
                    "Domain Reload test cleanup failed.",
                    failures);
            }
        }

        [UnityTest]
        public IEnumerator Reset_ClearsCurrentHostWithoutDestroyingUnrelatedObject()
        {
            var unrelated = Track(new GameObject("Unrelated"));
            var host = CreateHost("First Host", CreateProfile());
            Assert.That(FrameworkHost.Current, Is.SameAs(host));

            FrameworkStaticReset.Reset();
            yield return null;

            Assert.That(FrameworkHost.Current, Is.Null);
            Assert.That(unrelated, Is.Not.Null);
            Assert.That(host, Is.Not.Null);
        }

        [Test]
        public void Reset_IsolatesObjectPoolOwnershipAcrossSessions()
        {
            var item = new PooledReference();
            var firstPool = new ObjectPool<PooledReference>(
                () => item,
                maxIdleCapacity: 0);
            Assert.That(firstPool.Rent(), Is.SameAs(item));
            firstPool.Return(item);

            FrameworkStaticReset.Reset();

            var secondPool = new ObjectPool<PooledReference>(
                () => item,
                maxIdleCapacity: 0);
            Assert.That(secondPool.Rent(), Is.SameAs(item));
            secondPool.Return(item);
        }

        [UnityTest]
        public IEnumerator Reset_AllowsUIRootRecreationAfterDisposal()
        {
            var first = Track(UIRoot.Create(dontDestroyOnLoad: false));
            var dispose = first.DisposeAsync().AsTask();
            yield return WaitForTask(dispose, "First UIRoot disposal timed out.");
            Observe(dispose);

            FrameworkStaticReset.Reset();
            var second = Track(UIRoot.Create(dontDestroyOnLoad: false));
            yield return null;

            Assert.That(first == null, Is.True);
            Assert.That(second, Is.Not.Null);
            Assert.That(second, Is.Not.SameAs(first));
        }

        [UnityTest]
        public IEnumerator SequentialHostCycles_IsolateRuntimeServicesAndDiagnostics()
        {
            var resourceAsset =
                Track(ScriptableObject.CreateInstance<CycleAsset>());
            var firstPending = Track(new PendingOperation<CycleAsset>());
            var secondPending = Track(new PendingOperation<CycleAsset>());
            var firstBackend = Track(
                new CycleResourceBackend(resourceAsset, firstPending));
            var secondBackend = Track(
                new CycleResourceBackend(resourceAsset, secondPending));
            Assert.That(_resourceBackends, Does.Contain(firstBackend));
            Assert.That(_pendingOperations, Does.Contain(firstPending));
            var backends = new Queue<CycleResourceBackend>(
                new[] { firstBackend, secondBackend });
            var profile = CreateRuntimeProfile(
                () => new CycleResourceModule(backends.Dequeue()));
            var firstHost = CreateHost("First Host", profile);
            var firstStart = firstHost.StartRuntimeAsync().AsTask();
            yield return WaitForTask(firstStart, "First runtime startup timed out.");
            Observe(firstStart);

            var firstRuntime = firstHost.Runtime;
            var firstModules =
                firstRuntime.Modules.Select(record => record.Module).ToArray();
            var firstEvents = firstRuntime.Services.Resolve<IEventBus>();
            var firstResources =
                firstRuntime.Services.Resolve<IResourceService>();
            var firstFsm = firstRuntime.Services.Resolve<IFsmService>();
            firstEvents.Enqueue(new CycleEvent());
            firstFsm.Create("first-cycle-machine", new object());

            var leaseTask = Track(
                firstResources.LoadAsync<CycleAsset>(
                    new ResourceKey("lease")).AsTask());
            yield return WaitForTask(
                leaseTask,
                "First-cycle resource lease did not complete.");
            Observe(leaseTask);
            var firstLease = Track(leaseTask.Result);
            var inflightTask = Track(
                firstResources.LoadAsync<CycleAsset>(
                    new ResourceKey("inflight")).AsTask());
            Assert.That(_resourceLeases, Does.Contain(firstLease));
            Assert.That(_resourceLeaseTasks, Does.Contain(inflightTask));
            Assert.That(
                firstResources.Diagnostics.OutstandingLeases,
                Has.Count.EqualTo(1));
            Assert.That(
                firstResources.Diagnostics.InflightOperationCount,
                Is.EqualTo(1));

            firstBackend.CompleteInflight();
            var firstStop = firstHost.StopRuntimeAsync().AsTask();
            yield return WaitForTask(firstStop, "First runtime shutdown timed out.");
            Observe(firstStop);
            yield return WaitForTask(
                IgnoreFailure(inflightTask),
                "First-cycle inflight resource did not settle.");
            firstLease.Dispose();
            AssertEmptyDiagnostics(firstEvents, firstResources, firstFsm);

            Object.Destroy(firstHost.gameObject);
            yield return null;
            Assert.That(FrameworkHost.Current, Is.Null);
            FrameworkStaticReset.Reset();

            var secondHost = CreateHost("Second Host", profile);
            var secondStart = secondHost.StartRuntimeAsync().AsTask();
            yield return WaitForTask(secondStart, "Second runtime startup timed out.");
            Observe(secondStart);

            var secondRuntime = secondHost.Runtime;
            var secondEvents = secondRuntime.Services.Resolve<IEventBus>();
            var secondResources =
                secondRuntime.Services.Resolve<IResourceService>();
            var secondFsm = secondRuntime.Services.Resolve<IFsmService>();

            Assert.That(secondRuntime, Is.Not.SameAs(firstRuntime));
            Assert.That(secondEvents, Is.Not.SameAs(firstEvents));
            Assert.That(secondResources, Is.Not.SameAs(firstResources));
            Assert.That(secondFsm, Is.Not.SameAs(firstFsm));
            Assert.That(secondRuntime.Modules, Has.Count.EqualTo(3));
            Assert.That(
                secondRuntime.Modules.Select(record => record.Module),
                Has.None.Matches<IFrameworkModule>(
                    module => firstModules.Contains(module)));

            var dispatchCount = 0;
            using (secondEvents.Subscribe<CycleEvent>(_ => dispatchCount++))
            {
                secondRuntime.Update(0f);
            }

            Assert.That(dispatchCount, Is.Zero);
            var cycleDiagnostics =
                secondEvents.Diagnostics.Get<CycleEvent>();
            Assert.That(cycleDiagnostics.ListenerCount, Is.Zero);
            Assert.That(cycleDiagnostics.DispatchCount, Is.Zero);
            Assert.That(cycleDiagnostics.ExceptionCount, Is.Zero);
            Assert.That(
                secondResources.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(
                secondResources.Diagnostics.InflightOperationCount,
                Is.Zero);
            Assert.That(secondFsm.Diagnostics, Is.Empty);
            Assert.That(FrameworkHost.Current, Is.SameAs(secondHost));

            var secondStop = secondHost.StopRuntimeAsync().AsTask();
            yield return WaitForTask(
                secondStop,
                "Second runtime shutdown timed out.");
            Observe(secondStop);
            AssertEmptyDiagnostics(secondEvents, secondResources, secondFsm);
            Assert.That(
                secondRuntime.Modules,
                Has.All.Matches<ModuleRecord>(
                    record => record.State == ModuleState.Unloaded));

            Object.Destroy(secondHost.gameObject);
            yield return null;
            Assert.That(FrameworkHost.Current, Is.Null);
        }

        [Test]
        public void Reset_RepeatedCallsAreSafeAndDeterministic()
        {
            Assert.DoesNotThrow(() => FrameworkStaticReset.Reset());
            Assert.DoesNotThrow(() => FrameworkStaticReset.Reset());
            Assert.That(FrameworkHost.Current, Is.Null);
        }

        [Test]
        public void Reset_DeduplicatesCallbacksAndAggregatesFailuresInOrder()
        {
            ResetCallbacks.ResetCounters();
            var registrations = new[]
            {
                FrameworkStaticReset.Register(ResetCallbacks.FirstFailure),
                FrameworkStaticReset.Register(ResetCallbacks.Success),
                FrameworkStaticReset.Register(ResetCallbacks.SecondFailure),
                FrameworkStaticReset.Register(ResetCallbacks.Success)
            };
            try
            {
                ResetCallbacks.Fail = true;
                var aggregate = Assert.Throws<AggregateException>(
                    FrameworkStaticReset.Reset);
                Assert.That(ResetCallbacks.SuccessCount, Is.EqualTo(1));
                Assert.That(aggregate.InnerExceptions, Has.Count.EqualTo(2));
                Assert.That(
                    aggregate.InnerExceptions.Select(
                        exception => exception.Message),
                    Is.EqualTo(
                        new[]
                        {
                            "first reset failure",
                            "second reset failure"
                        }));

                ResetCallbacks.Fail = false;
                Assert.DoesNotThrow(() => FrameworkStaticReset.Reset());
                Assert.That(ResetCallbacks.SuccessCount, Is.EqualTo(2));
            }
            finally
            {
                ResetCallbacks.Fail = false;
                for (var index = 0; index < registrations.Length; index++)
                {
                    registrations[index].Dispose();
                }
            }
        }

        [Test]
        public void SubsystemRegistration_IsolatesAndLogsResetFailures()
        {
            ResetCallbacks.ResetCounters();
            var registrations = new[]
            {
                FrameworkStaticReset.Register(ResetCallbacks.FirstFailure),
                FrameworkStaticReset.Register(ResetCallbacks.Success),
                FrameworkStaticReset.Register(ResetCallbacks.SecondFailure)
            };
            try
            {
                ResetCallbacks.Fail = true;
                var entry = typeof(FrameworkStaticReset).GetMethod(
                    "ResetAtSubsystemRegistration",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(entry, Is.Not.Null);
                LogAssert.Expect(
                    LogType.Exception,
                    new System.Text.RegularExpressions.Regex(
                        "first reset failure"));
                LogAssert.Expect(
                    LogType.Exception,
                    new System.Text.RegularExpressions.Regex(
                        "second reset failure"));

                Assert.DoesNotThrow(() => entry.Invoke(null, null));
                Assert.That(ResetCallbacks.SuccessCount, Is.EqualTo(1));
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                ResetCallbacks.Fail = false;
                for (var index = 0; index < registrations.Length; index++)
                {
                    registrations[index].Dispose();
                }
            }
        }

        private FrameworkProfile CreateRuntimeProfile(
            Func<IFrameworkModule> resourceFactory = null)
        {
            return CreateProfile(
                CreateInstaller(
                    "EventBus",
                    Array.Empty<string>(),
                    () => new EventBusModule()),
                CreateInstaller(
                    "Resource",
                    Array.Empty<string>(),
                    resourceFactory ?? (() => new ResourceModule())),
                CreateInstaller(
                    "FSM",
                    Array.Empty<string>(),
                    () => new FsmModule()));
        }

        private RuntimeInstaller CreateInstaller(
            string id,
            IReadOnlyCollection<string> dependencies,
            Func<IFrameworkModule> factory)
        {
            var installer = Track(
                ScriptableObject.CreateInstance<RuntimeInstaller>());
            installer.Configure(id, dependencies, factory);
            return installer;
        }

        private FrameworkHost CreateHost(
            string name,
            FrameworkProfile profile)
        {
            var gameObject = Track(new GameObject(name));
            var host = gameObject.AddComponent<FrameworkHost>();
            host.Configure(profile);
            return host;
        }

        private FrameworkProfile CreateProfile(
            params ModuleInstaller[] installers)
        {
            var profile =
                Track(ScriptableObject.CreateInstance<FrameworkProfile>());
            var field = typeof(FrameworkProfile).GetField(
                "_installers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(profile, new List<ModuleInstaller>(installers));
            return profile;
        }

        private T Track<T>(T created)
            where T : Object
        {
            _createdObjects.Add(created);
            if (created is UIRoot root)
            {
                _createdRoots.Add(root);
            }

            return created;
        }

        private CycleResourceBackend Track(CycleResourceBackend backend)
        {
            _resourceBackends.Add(backend);
            return backend;
        }

        private PendingOperation<CycleAsset> Track(
            PendingOperation<CycleAsset> operation)
        {
            _pendingOperations.Add(operation);
            return operation;
        }

        private Task<IAssetLease<CycleAsset>> Track(
            Task<IAssetLease<CycleAsset>> task)
        {
            _resourceLeaseTasks.Add(task);
            return task;
        }

        private IAssetLease<CycleAsset> Track(
            IAssetLease<CycleAsset> lease)
        {
            if (lease != null && !_resourceLeases.Contains(lease))
            {
                _resourceLeases.Add(lease);
            }

            return lease;
        }

        private static void AssertEmptyDiagnostics(
            IEventBus events,
            IResourceService resources,
            IFsmService fsm)
        {
            Assert.That(events.Diagnostics.Entries, Is.Empty);
            Assert.That(resources.Diagnostics.OutstandingLeases, Is.Empty);
            Assert.That(resources.Diagnostics.InflightOperationCount, Is.Zero);
            Assert.That(fsm.Diagnostics, Is.Empty);
        }

        private static IEnumerator WaitForTask(Task task, string timeoutMessage)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted &&
                   elapsed.Elapsed.TotalSeconds < TimeoutSeconds)
            {
                yield return null;
            }

            elapsed.Stop();
            Assert.That(
                task.IsCompleted,
                Is.True,
                timeoutMessage + " Elapsed real seconds: " +
                elapsed.Elapsed.TotalSeconds.ToString("F3") + ".");
        }

        private static IEnumerator WaitForCleanupTask(
            Task task,
            string timeoutMessage,
            ICollection<Exception> failures,
            bool swallowTaskFailure = false)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted &&
                   elapsed.Elapsed.TotalSeconds < TimeoutSeconds)
            {
                yield return null;
            }

            elapsed.Stop();
            if (!task.IsCompleted)
            {
                failures.Add(
                    new TimeoutException(
                        timeoutMessage + " Elapsed real seconds: " +
                        elapsed.Elapsed.TotalSeconds.ToString("F3") + "."));
                yield break;
            }

            if (swallowTaskFailure)
            {
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch
                {
                    // A resource request may already be canceled by the test.
                }

                yield break;
            }

            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        private static void Observe(Task task)
        {
            task.GetAwaiter().GetResult();
        }

        private static async Task IgnoreFailure(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
                // The caller only needs the operation to settle.
            }
        }

        private sealed class RuntimeInstaller : ModuleInstaller
        {
            private string _id;
            private IReadOnlyCollection<string> _dependencies;
            private Func<IFrameworkModule> _factory;

            public override string ModuleId => _id;

            public override IReadOnlyCollection<string> Dependencies =>
                _dependencies;

            public override IFrameworkModule CreateModule()
            {
                return _factory();
            }

            public void Configure(
                string id,
                IReadOnlyCollection<string> dependencies,
                Func<IFrameworkModule> factory)
            {
                _id = id;
                _dependencies = dependencies;
                _factory = factory;
            }
        }

        private sealed class PooledReference
        {
        }

        private sealed class CycleAsset : ScriptableObject
        {
        }

        private sealed class CycleResourceModule : IFrameworkModule
        {
            private static readonly IReadOnlyCollection<string> NoDependencies =
                Array.Empty<string>();
            private readonly IResourceBackend _backend;
            private ResourceService _service;

            public CycleResourceModule(IResourceBackend backend)
            {
                _backend = backend;
            }

            public string Id => "Resource";
            public IReadOnlyCollection<string> Dependencies => NoDependencies;

            public ValueTask InitializeAsync(
                ModuleContext context,
                CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                _service = new ResourceService(_backend, context.Logger);
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
                return _service?.StopAsync(token) ?? default;
            }

            public ValueTask DisposeAsync()
            {
                _service = null;
                return default;
            }
        }

        private sealed class CycleResourceBackend : IResourceBackend
        {
            private readonly CycleAsset _asset;
            private readonly PendingOperation<CycleAsset> _inflight;

            public CycleResourceBackend(
                CycleAsset asset,
                PendingOperation<CycleAsset> inflight)
            {
                _asset = asset;
                _inflight = inflight;
            }

            public void CompleteInflight()
            {
                _inflight.Succeed(_asset);
            }

            public IResourceOperation<T> LoadAssetAsync<T>(ResourceKey key)
                where T : Object
            {
                if (typeof(T) != typeof(CycleAsset))
                {
                    throw new InvalidOperationException(
                        "The cycle backend only serves CycleAsset.");
                }

                if (string.Equals(
                        key.Value,
                        "lease",
                        StringComparison.Ordinal))
                {
                    return (IResourceOperation<T>)(object)
                        new CompletedOperation<CycleAsset>(_asset);
                }

                if (string.Equals(
                        key.Value,
                        "inflight",
                        StringComparison.Ordinal))
                {
                    return (IResourceOperation<T>)(object)_inflight;
                }

                throw new InvalidOperationException(
                    "Unexpected cycle resource key '" + key.Value + "'.");
            }

            public IResourceOperation<GameObject> InstantiateAsync(
                ResourceKey key,
                Transform parent)
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
        }

        private sealed class CompletedOperation<T> : IResourceOperation<T>
        {
            public CompletedOperation(T value)
            {
                Task = System.Threading.Tasks.Task.FromResult(value);
            }

            public Task<T> Task { get; }

            public void Release()
            {
            }
        }

        private sealed class PendingOperation<T> : IResourceOperation<T>
        {
            private readonly TaskCompletionSource<T> _completion =
                new TaskCompletionSource<T>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<T> Task => _completion.Task;

            public void Succeed(T value)
            {
                _completion.TrySetResult(value);
            }

            public void Release()
            {
            }
        }

        private readonly struct CycleEvent
        {
        }

        private static class ResetCallbacks
        {
            public static bool Fail { get; set; }
            public static int SuccessCount { get; private set; }

            public static void ResetCounters()
            {
                Fail = false;
                SuccessCount = 0;
            }

            public static void FirstFailure()
            {
                if (Fail)
                {
                    throw new InvalidOperationException("first reset failure");
                }
            }

            public static void Success()
            {
                SuccessCount++;
            }

            public static void SecondFailure()
            {
                if (Fail)
                {
                    throw new InvalidOperationException("second reset failure");
                }
            }
        }
    }
}
