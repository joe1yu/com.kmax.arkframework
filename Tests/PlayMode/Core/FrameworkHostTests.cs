using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ArkFramework.Tests
{
    public sealed class FrameworkHostTests
    {
        private readonly List<UnityEngine.Object> _createdObjects =
            new List<UnityEngine.Object>();

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            if (FrameworkHost.Current != null)
            {
                UnityEngine.Object.Destroy(FrameworkHost.Current.gameObject);
                yield return null;
            }

            RecordingInstaller.Reset();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (FrameworkHost.Current != null)
            {
                UnityEngine.Object.Destroy(FrameworkHost.Current.gameObject);
            }

            for (var index = _createdObjects.Count - 1; index >= 0; index--)
            {
                var createdObject = _createdObjects[index];
                if (createdObject != null)
                {
                    UnityEngine.Object.Destroy(createdObject);
                }
            }

            _createdObjects.Clear();
            yield return null;
            Assert.That(FrameworkHost.Current, Is.Null);
            RecordingInstaller.Reset();
        }

        [UnityTest]
        public IEnumerator DuplicateHosts_KeepFirstAndDestroySecond()
        {
            var profile = CreateProfile();
            var first = CreateHost("First Framework Host", profile);

            LogAssert.Expect(
                LogType.Warning,
                new System.Text.RegularExpressions.Regex("FrameworkHost.*already exists"));
            var secondObject = new GameObject("Second Framework Host");
            _createdObjects.Add(secondObject);
            var second = secondObject.AddComponent<FrameworkHost>();
            Assert.That(second.Runtime, Is.Null);

            yield return null;

            Assert.That(FrameworkHost.Current, Is.SameAs(first));
            Assert.That(second == null, Is.True);
            Assert.That(secondObject == null, Is.True);
            Assert.That(first.Runtime, Is.Not.Null);
        }

        [UnityTest]
        public IEnumerator DestroyingCurrentHost_ClearsStaticCurrent()
        {
            var host = CreateHost("Framework Host", CreateProfile());
            Assert.That(FrameworkHost.Current, Is.SameAs(host));

            UnityEngine.Object.Destroy(host.gameObject);
            yield return null;

            Assert.That(FrameworkHost.Current, Is.Null);
        }

        [UnityTest]
        public IEnumerator Host_StartsProfileModulesAndForwardsFrames()
        {
            var installer = CreateInstaller("recording");
            var host = CreateHost("Framework Host", CreateProfile(installer));

            var startTask = host.StartRuntimeAsync().AsTask();
            yield return WaitForTask(startTask);

            var module = installer.LastCreated;
            Assert.That(module, Is.Not.Null);
            Assert.That(module.InitializeCount, Is.EqualTo(1));
            Assert.That(module.StartCount, Is.EqualTo(1));
            Assert.That(RecordingInstaller.TotalCreateCount, Is.EqualTo(1));

            yield return null;
            yield return new WaitForFixedUpdate();
            yield return null;

            Assert.That(module.UpdateCount, Is.GreaterThan(0));
            Assert.That(module.LateUpdateCount, Is.GreaterThan(0));
            Assert.That(module.FixedUpdateCount, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator StartRuntimeAsync_ReusesPublishedTaskAcrossStartupGates()
        {
            var installer = CreateInstaller("recording");
            installer.ConfigureCreatedModule = module => module.DelayStartup();
            var host = CreateHost("Framework Host", CreateProfile(installer));

            var firstTask = host.StartRuntimeAsync().AsTask();
            var module = installer.LastCreated;
            var secondTask = host.StartRuntimeAsync().AsTask();

            Assert.That(secondTask, Is.SameAs(firstTask));
            Assert.That(RecordingInstaller.TotalCreateCount, Is.EqualTo(1));
            Assert.That(module.InitializeCount, Is.EqualTo(1));
            Assert.That(module.StartCount, Is.Zero);

            module.CompleteInitialize();
            while (module.StartCount == 0)
            {
                yield return null;
            }

            Assert.That(firstTask.IsCompleted, Is.False);
            module.CompleteStart();
            yield return WaitForTask(firstTask);

            Assert.That(secondTask.IsCompleted, Is.True);
            Assert.That(secondTask.IsFaulted, Is.False);
            Assert.That(secondTask.IsCanceled, Is.False);
            Assert.That(module.StartCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator FactoryReentry_StartRuntimeAsync_ReturnsPublishedTask()
        {
            var installer = CreateInstaller("recording");
            var host = CreateHost("Framework Host", CreateProfile(installer));
            Task reentrantTask = null;
            var reentered = false;
            installer.CreateCallback = () =>
            {
                if (reentered)
                {
                    return;
                }

                reentered = true;
                reentrantTask = host.StartRuntimeAsync().AsTask();
            };

            var outerTask = host.StartRuntimeAsync().AsTask();

            Assert.That(reentrantTask, Is.SameAs(outerTask));
            Assert.That(RecordingInstaller.TotalCreateCount, Is.EqualTo(1));
            yield return WaitForTask(outerTask);
        }

        [UnityTest]
        public IEnumerator InitializeReentry_StartRuntimeAsync_ReturnsPublishedTask()
        {
            var installer = CreateInstaller("recording");
            var host = CreateHost("Framework Host", CreateProfile(installer));
            Task reentrantTask = null;
            var reentered = false;
            installer.ConfigureCreatedModule = module =>
            {
                module.InitializeCallback = () =>
                {
                    if (reentered)
                    {
                        return;
                    }

                    reentered = true;
                    reentrantTask = host.StartRuntimeAsync().AsTask();
                };
            };

            var outerTask = host.StartRuntimeAsync().AsTask();

            Assert.That(reentrantTask, Is.SameAs(outerTask));
            Assert.That(RecordingInstaller.TotalCreateCount, Is.EqualTo(1));
            yield return WaitForTask(outerTask);
        }

        [UnityTest]
        public IEnumerator HostDestruction_StopsAndDisposesRuntime()
        {
            var installer = CreateInstaller("recording");
            var host = CreateHost("Framework Host", CreateProfile(installer));
            var startTask = host.StartRuntimeAsync().AsTask();
            yield return WaitForTask(startTask);

            var module = installer.LastCreated;
            module.DelayShutdown();
            UnityEngine.Object.Destroy(host.gameObject);
            yield return null;

            Assert.That(module.StopCount, Is.EqualTo(1));
            Assert.That(module.DisposeCount, Is.Zero);
            var stopTask = host.StopRuntimeAsync().AsTask();
            Assert.That(stopTask.IsCompleted, Is.False);

            module.CompleteStop();
            while (module.DisposeCount == 0)
            {
                yield return null;
            }

            Assert.That(stopTask.IsCompleted, Is.False);
            module.CompleteDispose();
            yield return WaitForTask(stopTask);

            Assert.That(module.StopCount, Is.EqualTo(1));
            Assert.That(module.DisposeCount, Is.EqualTo(1));
            Assert.That(FrameworkHost.Current, Is.Null);
        }

        [UnityTest]
        public IEnumerator StopReentry_StopRuntimeAsync_ReturnsPublishedTailTask()
        {
            var installer = CreateInstaller("recording");
            var host = CreateHost("Framework Host", CreateProfile(installer));
            var startTask = host.StartRuntimeAsync().AsTask();
            yield return WaitForTask(startTask);

            Task reentrantTask = null;
            var reentered = false;
            var module = installer.LastCreated;
            module.StopCallback = () =>
            {
                if (reentered)
                {
                    return;
                }

                reentered = true;
                reentrantTask = host.StopRuntimeAsync().AsTask();
            };

            var outerTask = host.StopRuntimeAsync().AsTask();

            Assert.That(reentrantTask, Is.SameAs(outerTask));
            yield return WaitForTask(outerTask);
            Assert.That(module.StopCount, Is.EqualTo(1));
            Assert.That(module.DisposeCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator UnityStartFailure_IsLoggedAndSharedWithExplicitCaller()
        {
            var startFailure = new InvalidOperationException("startup boom");
            var installer = CreateInstaller("recording");
            installer.ConfigureCreatedModule =
                module => module.StartException = startFailure;

            LogAssert.Expect(
                LogType.Error,
                new System.Text.RegularExpressions.Regex(
                    @"\[Lifecycle\] \[recording\] Framework startup failed\."));
            LogAssert.Expect(
                LogType.Error,
                new System.Text.RegularExpressions.Regex(
                    @"\[Lifecycle\] \[FrameworkHost\] Framework startup failed\."));
            var host = CreateHost("Framework Host", CreateProfile(installer));

            yield return null;

            var explicitTask = host.StartRuntimeAsync().AsTask();
            yield return WaitForTaskCompletion(explicitTask);

            Assert.That(explicitTask.IsFaulted, Is.True);
            Assert.That(explicitTask.Exception.InnerException, Is.SameAs(startFailure));
            Assert.That(RecordingInstaller.TotalCreateCount, Is.EqualTo(1));
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator StopAndDisposeFailures_AreLoggedAndStopTaskKeepsPrimaryFailure()
        {
            var stopFailure = new InvalidOperationException("stop boom");
            var disposeFailure = new InvalidOperationException("dispose boom");
            var installer = CreateInstaller("recording");
            installer.ConfigureCreatedModule = module =>
            {
                module.StopException = stopFailure;
                module.DisposeException = disposeFailure;
            };
            var host = CreateHost("Framework Host", CreateProfile(installer));
            yield return WaitForTask(host.StartRuntimeAsync().AsTask());

            LogAssert.Expect(
                LogType.Error,
                new System.Text.RegularExpressions.Regex(
                    @"\[Lifecycle\] \[recording\] Module stop failed\."));
            LogAssert.Expect(
                LogType.Error,
                new System.Text.RegularExpressions.Regex(
                    @"\[Lifecycle\] \[recording\] Module disposal failed\."));
            LogAssert.Expect(
                LogType.Error,
                new System.Text.RegularExpressions.Regex(
                    @"\[Lifecycle\] \[FrameworkHost\] Framework shutdown failed\."));

            var stopTask = host.StopRuntimeAsync().AsTask();
            yield return WaitForTaskCompletion(stopTask);

            Assert.That(stopTask.IsFaulted, Is.True);
            Assert.That(stopTask.Exception.InnerException, Is.SameAs(stopFailure));
            Assert.That(installer.LastCreated.StopCount, Is.EqualTo(1));
            Assert.That(installer.LastCreated.DisposeCount, Is.EqualTo(1));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void Profile_CreatesStableDescriptorsFromInstallers()
        {
            var first = CreateInstaller("first");
            var second = CreateInstaller("second", "first");
            var profile = CreateProfile(first, second);
            var installerSnapshot = profile.Installers;

            var descriptors = profile.CreateDescriptors();
            GetInstallerList(profile).Add(CreateInstaller("third"));

            Assert.That(installerSnapshot, Has.Count.EqualTo(2));
            Assert.That(descriptors, Has.Count.EqualTo(2));
            Assert.That(descriptors[0].Id, Is.EqualTo("first"));
            Assert.That(descriptors[0].StableOrder, Is.EqualTo(0));
            Assert.That(descriptors[1].Id, Is.EqualTo("second"));
            Assert.That(descriptors[1].StableOrder, Is.EqualTo(1));
            Assert.That(descriptors[1].Dependencies, Is.EqualTo(new[] { "first" }));
            Assert.That(RecordingInstaller.TotalCreateCount, Is.Zero);
            Assert.That(first.ServiceTypes, Is.Empty);

            var firstInstance = (RecordingModule)descriptors[0].Factory();
            var secondInstance = (RecordingModule)descriptors[1].Factory();

            Assert.That(firstInstance, Is.Not.SameAs(secondInstance));
            Assert.That(firstInstance.Id, Is.EqualTo("first"));
            Assert.That(secondInstance.Id, Is.EqualTo("second"));
            Assert.That(firstInstance, Is.SameAs(first.LastCreated));
            Assert.That(secondInstance, Is.SameAs(second.LastCreated));
            Assert.That(RecordingInstaller.TotalCreateCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator DefaultInstallerProfile_StartsAndStopsAllModules()
        {
            var profile = CreateProfile(
                CreateDefaultInstaller<EventBusModuleInstaller>(),
                CreateDefaultInstaller<ResourceModuleInstaller>(),
                CreateDefaultInstaller<PoolModuleInstaller>(),
                CreateDefaultInstaller<ConfigModuleInstaller>(),
                CreateDefaultInstaller<TableModuleInstaller>(),
                CreateDefaultInstaller<FsmModuleInstaller>(),
                CreateDefaultInstaller<SceneModuleInstaller>(),
                CreateDefaultInstaller<UIModuleInstaller>(),
                CreateDefaultInstaller<AudioModuleInstaller>(),
                CreateDefaultInstaller<ProcedureModuleInstaller>());
            var host = CreateHost("Framework Host", profile);

            yield return WaitForTask(host.StartRuntimeAsync().AsTask());

            Assert.That(host.Runtime.Modules, Has.Count.EqualTo(10));
            Assert.That(
                host.Runtime.Modules,
                Has.All.Matches<ModuleRecord>(
                    record => record.State == ModuleState.Running));

            yield return WaitForTask(host.StopRuntimeAsync().AsTask());
        }

        [Test]
        public void Profile_WithNullInstaller_RejectsDescriptorCreation()
        {
            var profile = CreateProfile((ModuleInstaller)null);

            Assert.Throws<InvalidOperationException>(
                () => profile.CreateDescriptors());
        }

        [UnityTest]
        public IEnumerator Configure_AfterRuntimeStart_IsRejected()
        {
            var host = CreateHost(
                "Framework Host",
                CreateProfile(CreateInstaller("recording")));
            var startTask = host.StartRuntimeAsync().AsTask();
            yield return WaitForTask(startTask);
            Assert.Throws<InvalidOperationException>(
                () => host.Configure(CreateProfile()));
        }

        private FrameworkHost CreateHost(string name, FrameworkProfile profile)
        {
            var gameObject = new GameObject(name);
            _createdObjects.Add(gameObject);
            var host = gameObject.AddComponent<FrameworkHost>();
            host.Configure(profile);
            return host;
        }

        private FrameworkProfile CreateProfile(params ModuleInstaller[] installers)
        {
            var profile = ScriptableObject.CreateInstance<FrameworkProfile>();
            _createdObjects.Add(profile);
            var field = typeof(FrameworkProfile).GetField(
                "_installers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(profile, new List<ModuleInstaller>(installers));
            return profile;
        }

        private static List<ModuleInstaller> GetInstallerList(
            FrameworkProfile profile)
        {
            var field = typeof(FrameworkProfile).GetField(
                "_installers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (List<ModuleInstaller>)field.GetValue(profile);
        }

        private RecordingInstaller CreateInstaller(
            string moduleId,
            params string[] dependencies)
        {
            var installer = ScriptableObject.CreateInstance<RecordingInstaller>();
            _createdObjects.Add(installer);
            installer.Configure(moduleId, dependencies);
            return installer;
        }

        private T CreateDefaultInstaller<T>() where T : ModuleInstaller
        {
            var installer = ScriptableObject.CreateInstance<T>();
            _createdObjects.Add(installer);
            return installer;
        }

        private static IEnumerator WaitForTask(Task task)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }

            task.GetAwaiter().GetResult();
        }

        private static IEnumerator WaitForTaskCompletion(Task task)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }
        }

        private sealed class RecordingInstaller : ModuleInstaller
        {
            private string _moduleId;
            private IReadOnlyCollection<string> _dependencies;

            public static int TotalCreateCount { get; private set; }

            public RecordingModule LastCreated { get; private set; }

            public Action CreateCallback { get; set; }

            public Action<RecordingModule> ConfigureCreatedModule { get; set; }

            public override string ModuleId => _moduleId;

            public override IReadOnlyCollection<string> Dependencies => _dependencies;

            public void Configure(
                string moduleId,
                IReadOnlyCollection<string> dependencies)
            {
                _moduleId = moduleId;
                _dependencies = dependencies;
            }

            public override IFrameworkModule CreateModule()
            {
                TotalCreateCount++;
                LastCreated = new RecordingModule(_moduleId, _dependencies);
                ConfigureCreatedModule?.Invoke(LastCreated);
                CreateCallback?.Invoke();
                return LastCreated;
            }

            public static void Reset()
            {
                TotalCreateCount = 0;
            }
        }

        private sealed class RecordingModule :
            IFrameworkModule,
            IUpdateModule,
            ILateUpdateModule,
            IFixedUpdateModule
        {
            public RecordingModule(
                string id,
                IReadOnlyCollection<string> dependencies)
            {
                Id = id;
                Dependencies = dependencies;
            }

            public string Id { get; }

            public IReadOnlyCollection<string> Dependencies { get; }

            public int InitializeCount { get; private set; }

            public int StartCount { get; private set; }

            public int StopCount { get; private set; }

            public int DisposeCount { get; private set; }

            public int UpdateCount { get; private set; }

            public int LateUpdateCount { get; private set; }

            public int FixedUpdateCount { get; private set; }

            private TaskCompletionSource<bool> _stopCompletion;

            private TaskCompletionSource<bool> _disposeCompletion;

            private TaskCompletionSource<bool> _initializeCompletion;

            private TaskCompletionSource<bool> _startCompletion;

            public Action InitializeCallback { get; set; }

            public Action StopCallback { get; set; }

            public Exception StartException { get; set; }

            public Exception StopException { get; set; }

            public Exception DisposeException { get; set; }

            public void DelayStartup()
            {
                _initializeCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _startCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public void CompleteInitialize()
            {
                _initializeCompletion.SetResult(true);
            }

            public void CompleteStart()
            {
                _startCompletion.SetResult(true);
            }

            public void DelayShutdown()
            {
                _stopCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeCompletion = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            public void CompleteStop()
            {
                _stopCompletion.SetResult(true);
            }

            public void CompleteDispose()
            {
                _disposeCompletion.SetResult(true);
            }

            public ValueTask InitializeAsync(
                ModuleContext context,
                CancellationToken token)
            {
                InitializeCount++;
                InitializeCallback?.Invoke();
                return _initializeCompletion == null
                    ? default
                    : new ValueTask(_initializeCompletion.Task);
            }

            public ValueTask StartAsync(CancellationToken token)
            {
                StartCount++;
                if (StartException != null)
                {
                    throw StartException;
                }

                return _startCompletion == null
                    ? default
                    : new ValueTask(_startCompletion.Task);
            }

            public ValueTask StopAsync(CancellationToken token)
            {
                StopCount++;
                StopCallback?.Invoke();
                if (StopException != null)
                {
                    throw StopException;
                }

                return _stopCompletion == null
                    ? default
                    : new ValueTask(_stopCompletion.Task);
            }

            public ValueTask DisposeAsync()
            {
                DisposeCount++;
                if (DisposeException != null)
                {
                    throw DisposeException;
                }

                return _disposeCompletion == null
                    ? default
                    : new ValueTask(_disposeCompletion.Task);
            }

            public void Update(float deltaTime)
            {
                UpdateCount++;
            }

            public void LateUpdate(float deltaTime)
            {
                LateUpdateCount++;
            }

            public void FixedUpdate(float fixedDeltaTime)
            {
                FixedUpdateCount++;
            }
        }
    }
}
