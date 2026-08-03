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
    public sealed class FrameworkRuntimeTests
    {
        [UnityTest]
        public IEnumerator StartAsync_InitializesAndStartsInTopologicalOrder()
        {
            var events = new List<string>();
            var logger = new RecordingLogger();
            var runtime = new FrameworkRuntime(logger);
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var resource = new RecordingModule(
                "Resource",
                new[] { "Core" },
                events);
            var descriptors = new[]
            {
                Descriptor(resource, 20, events),
                Descriptor(core, 10, events)
            };

            var startTask = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();

            CollectionAssert.AreEqual(
                new[]
                {
                    "Core.Factory",
                    "Core.Initialize",
                    "Core.Start",
                    "Resource.Factory",
                    "Resource.Initialize",
                    "Resource.Start"
                },
                events);
            Assert.That(
                runtime.Modules.Select(record => record.Descriptor.Id),
                Is.EqualTo(new[] { "Core", "Resource" }));
            Assert.That(
                runtime.Modules.All(record => record.State == ModuleState.Running),
                Is.True);
            Assert.That(core.Context.Services, Is.SameAs(runtime.Services));
            Assert.That(core.Context.ModuleId, Is.EqualTo("Core"));
            Assert.That(core.Context.Logger, Is.SameAs(logger));
            Assert.That(core.Context.ModuleScope, Is.Not.Null);

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            stopTask.GetAwaiter().GetResult();
        }

        [UnityTest]
        public IEnumerator StopAsync_StopsAndDisposesInReverseOrder()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true
            };
            var resource = new RecordingModule(
                "Resource",
                new[] { "Core" },
                events)
            {
                TrackScopeDisposal = true
            };
            var descriptors = new[]
            {
                Descriptor(resource, 20, events),
                Descriptor(core, 10, events)
            };
            var startTask = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            stopTask.GetAwaiter().GetResult();

            CollectionAssert.AreEqual(
                new[]
                {
                    "Resource.Stop",
                    "Resource.Dispose",
                    "Resource.ScopeDispose",
                    "Core.Stop",
                    "Core.Dispose",
                    "Core.ScopeDispose"
                },
                events);
            Assert.That(
                runtime.Modules.All(record => record.State == ModuleState.Unloaded),
                Is.True);
        }

        [UnityTest]
        public IEnumerator FailedStart_MarksModuleFaultedAndCleansCreatedModules()
        {
            var events = new List<string>();
            var logger = new RecordingLogger();
            var runtime = new FrameworkRuntime(logger);
            var core = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true
            };
            var resource = new RecordingModule(
                "Resource",
                new[] { "Core" },
                events)
            {
                TrackScopeDisposal = true,
                StartFailure = new TestStartException(),
                DisposeFailure = new TestCleanupException()
            };
            var ui = new RecordingModule("UI", new[] { "Resource" }, events);
            var descriptors = new[]
            {
                Descriptor(ui, 30, events),
                Descriptor(resource, 20, events),
                Descriptor(core, 10, events)
            };

            var startTask = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(startTask);
            var exception = Assert.Throws<TestStartException>(
                () => startTask.GetAwaiter().GetResult());

            Assert.That(runtime.Modules.Count, Is.EqualTo(2));
            var coreRecord = runtime.Modules.Single(
                record => record.Descriptor.Id == "Core");
            var resourceRecord = runtime.Modules.Single(
                record => record.Descriptor.Id == "Resource");
            Assert.That(coreRecord.State, Is.EqualTo(ModuleState.Unloaded));
            Assert.That(resourceRecord.State, Is.EqualTo(ModuleState.Faulted));
            Assert.That(resourceRecord.LastException, Is.SameAs(exception));
            Assert.That(logger.Errors.Any(error => error.Exception is TestCleanupException));
            CollectionAssert.AreEqual(
                new[]
                {
                    "Core.Factory",
                    "Core.Initialize",
                    "Core.Start",
                    "Resource.Factory",
                    "Resource.Initialize",
                    "Resource.Start",
                    "Resource.Dispose",
                    "Resource.ScopeDispose",
                    "Core.Stop",
                    "Core.Dispose",
                    "Core.ScopeDispose"
                },
                events);
        }

        [UnityTest]
        public IEnumerator Tick_OnlyInvokesRunningFrameModules()
        {
            var events = new List<string>();
            var logger = new RecordingLogger();
            var runtime = new FrameworkRuntime(logger);
            var failing = new FrameRecordingModule(
                "Failing",
                Array.Empty<string>(),
                events)
            {
                UpdateFailure = new TestFrameException()
            };
            var healthy = new FrameRecordingModule(
                "Healthy",
                Array.Empty<string>(),
                events);
            var descriptors = new[]
            {
                Descriptor(failing, 0, events),
                Descriptor(healthy, 1, events)
            };
            var startTask = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();

            runtime.Update(0.1f);
            runtime.Update(0.2f);
            runtime.LateUpdate(0.3f);
            runtime.FixedUpdate(0.4f);

            Assert.That(failing.UpdateCount, Is.EqualTo(1));
            Assert.That(failing.LateUpdateCount, Is.Zero);
            Assert.That(failing.FixedUpdateCount, Is.Zero);
            Assert.That(healthy.UpdateCount, Is.EqualTo(2));
            Assert.That(healthy.LateUpdateCount, Is.EqualTo(1));
            Assert.That(healthy.FixedUpdateCount, Is.EqualTo(1));
            Assert.That(
                runtime.Modules.Single(record => record.Descriptor.Id == "Failing").State,
                Is.EqualTo(ModuleState.Faulted));
            Assert.That(logger.Errors.Any(error => error.Exception is TestFrameException));

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            stopTask.GetAwaiter().GetResult();
        }

        [UnityTest]
        public IEnumerator StartAsync_RejectsRepeatedStart()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var module = new RecordingModule("Core", Array.Empty<string>(), events);
            var descriptors = new[] { Descriptor(module, 0, events) };
            var firstStart = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(firstStart);
            firstStart.GetAwaiter().GetResult();

            Assert.Throws<InvalidOperationException>(
                () => runtime
                    .StartAsync(descriptors, CancellationToken.None)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult());

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            stopTask.GetAwaiter().GetResult();
        }

        [UnityTest]
        public IEnumerator StartAsync_RejectsNullFactoryResultAndCleansEarlierModules()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var descriptors = new[]
            {
                new ModuleDescriptor(
                    "Missing",
                    new[] { "Core" },
                    1,
                    () =>
                    {
                        events.Add("Missing.Factory");
                        return null;
                    }),
                Descriptor(core, 0, events)
            };

            var startTask = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(startTask);
            var exception = Assert.Throws<InvalidOperationException>(
                () => startTask.GetAwaiter().GetResult());

            StringAssert.Contains("Missing", exception.Message);
            Assert.That(runtime.Modules.Count, Is.EqualTo(2));
            Assert.That(runtime.Modules[1].State, Is.EqualTo(ModuleState.Faulted));
            Assert.That(runtime.Modules[1].Module, Is.Null);
            CollectionAssert.AreEqual(
                new[]
                {
                    "Core.Factory",
                    "Core.Initialize",
                    "Core.Start",
                    "Missing.Factory",
                    "Core.Stop",
                    "Core.Dispose"
                },
                events);
        }

        [UnityTest]
        public IEnumerator StartAsync_RejectsModuleContractMismatch()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var mismatched = new RecordingModule(
                "Actual",
                Array.Empty<string>(),
                events);
            var descriptor = new ModuleDescriptor(
                "Expected",
                Array.Empty<string>(),
                0,
                () => mismatched);

            var startTask = runtime
                .StartAsync(new[] { descriptor }, CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            var exception = Assert.Throws<InvalidOperationException>(
                () => startTask.GetAwaiter().GetResult());

            StringAssert.Contains("Expected", exception.Message);
            StringAssert.Contains("Actual", exception.Message);
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Faulted));
            Assert.That(mismatched.DisposeCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator StartAsync_AcceptsModuleDependenciesInDifferentOrder()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var resource = new RecordingModule(
                "Resource",
                Array.Empty<string>(),
                events);
            var ui = new RecordingModule(
                "UI",
                new[] { "Resource", "Core" },
                events);
            var descriptors = new[]
            {
                Descriptor(
                    "UI",
                    new[] { "Core", "Resource" },
                    ui,
                    2,
                    events),
                Descriptor(resource, 1, events),
                Descriptor(core, 0, events)
            };

            var startTask = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();

            Assert.That(runtime.Modules.Last().State, Is.EqualTo(ModuleState.Running));

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            stopTask.GetAwaiter().GetResult();
        }

        [UnityTest]
        public IEnumerator StartAsync_RejectsDuplicateModuleDependenciesAndCleansCreatedModules()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true
            };
            var duplicate = new RecordingModule(
                "Duplicate",
                new[] { "Core", "Core" },
                events);
            var descriptors = new[]
            {
                Descriptor(
                    "Duplicate",
                    new[] { "Core" },
                    duplicate,
                    1,
                    events),
                Descriptor(core, 0, events)
            };

            var startTask = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(startTask);
            var exception = Assert.Throws<InvalidOperationException>(
                () => startTask.GetAwaiter().GetResult());

            AssertContractFailureCleanup(runtime, duplicate, exception);
            CollectionAssert.AreEqual(
                new[]
                {
                    "Core.Factory",
                    "Core.Initialize",
                    "Core.Start",
                    "Duplicate.Factory",
                    "Duplicate.Dispose",
                    "Core.Stop",
                    "Core.Dispose",
                    "Core.ScopeDispose"
                },
                events);
            Assert.That(runtime.Services.TryResolve<CoreScopeDisposable>(out _), Is.False);
        }

        [UnityTest]
        public IEnumerator StartAsync_RejectsMissingModuleDependencyAndCleansCreatedModules()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true
            };
            var resource = new RecordingModule(
                "Resource",
                Array.Empty<string>(),
                events)
            {
                TrackScopeDisposal = true
            };
            var missing = new RecordingModule(
                "Missing",
                new[] { "Core" },
                events);
            var descriptors = new[]
            {
                Descriptor(
                    "Missing",
                    new[] { "Core", "Resource" },
                    missing,
                    2,
                    events),
                Descriptor(resource, 1, events),
                Descriptor(core, 0, events)
            };

            var startTask = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(startTask);
            var exception = Assert.Throws<InvalidOperationException>(
                () => startTask.GetAwaiter().GetResult());

            AssertContractFailureCleanup(runtime, missing, exception);
            CollectionAssert.AreEqual(
                new[]
                {
                    "Core.Factory",
                    "Core.Initialize",
                    "Core.Start",
                    "Resource.Factory",
                    "Resource.Initialize",
                    "Resource.Start",
                    "Missing.Factory",
                    "Missing.Dispose",
                    "Resource.Stop",
                    "Resource.Dispose",
                    "Resource.ScopeDispose",
                    "Core.Stop",
                    "Core.Dispose",
                    "Core.ScopeDispose"
                },
                events);
            Assert.That(runtime.Services.TryResolve<CoreScopeDisposable>(out _), Is.False);
            Assert.That(runtime.Services.TryResolve<ResourceScopeDisposable>(out _), Is.False);
        }

        [UnityTest]
        public IEnumerator StartAsync_RejectsExtraModuleDependencyAndCleansCreatedModules()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true
            };
            var extra = new RecordingModule(
                "Extra",
                new[] { "Core", "Unexpected" },
                events);
            var descriptors = new[]
            {
                Descriptor(
                    "Extra",
                    new[] { "Core" },
                    extra,
                    1,
                    events),
                Descriptor(core, 0, events)
            };

            var startTask = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(startTask);
            var exception = Assert.Throws<InvalidOperationException>(
                () => startTask.GetAwaiter().GetResult());

            AssertContractFailureCleanup(runtime, extra, exception);
            CollectionAssert.AreEqual(
                new[]
                {
                    "Core.Factory",
                    "Core.Initialize",
                    "Core.Start",
                    "Extra.Factory",
                    "Extra.Dispose",
                    "Core.Stop",
                    "Core.Dispose",
                    "Core.ScopeDispose"
                },
                events);
            Assert.That(runtime.Services.TryResolve<CoreScopeDisposable>(out _), Is.False);
        }

        [UnityTest]
        public IEnumerator StopAsync_ContinuesCleanupAndPropagatesFirstException()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var coreFailure = new TestStopException("Core");
            var resourceFailure = new TestStopException("Resource");
            var core = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true,
                StopFailure = coreFailure
            };
            var resource = new RecordingModule(
                "Resource",
                new[] { "Core" },
                events)
            {
                TrackScopeDisposal = true,
                StopFailure = resourceFailure
            };
            var descriptors = new[]
            {
                Descriptor(resource, 1, events),
                Descriptor(core, 0, events)
            };
            var startTask = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            var exception = Assert.Throws<TestStopException>(
                () => stopTask.GetAwaiter().GetResult());

            Assert.That(exception, Is.SameAs(resourceFailure));
            CollectionAssert.AreEqual(
                new[]
                {
                    "Resource.Stop",
                    "Resource.Dispose",
                    "Resource.ScopeDispose",
                    "Core.Stop",
                    "Core.Dispose",
                    "Core.ScopeDispose"
                },
                events);
            Assert.That(
                runtime.Modules.All(record => record.State == ModuleState.Faulted),
                Is.True);
        }

        [UnityTest]
        public IEnumerator DisposeAsync_IsIdempotent()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var module = new RecordingModule("Core", Array.Empty<string>(), events);
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(module, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();

            var firstDispose = runtime.DisposeAsync().AsTask();
            yield return WaitFor(firstDispose);
            firstDispose.GetAwaiter().GetResult();
            var secondDispose = runtime.DisposeAsync().AsTask();
            yield return WaitFor(secondDispose);
            secondDispose.GetAwaiter().GetResult();

            Assert.That(module.StopCount, Is.EqualTo(1));
            Assert.That(module.DisposeCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator StopAsync_DuringInitialize_WaitsThenCleansExactlyOnce()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var initializeGate = NewGate();
            var module = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true,
                InitializeGate = initializeGate
            };
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(module, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return null;
            Assert.That(startTask.IsCompleted, Is.False);
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Initializing));

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return null;

            var stoppedBeforeRelease = stopTask.IsCompleted;
            var disposeCountBeforeRelease = module.DisposeCount;
            initializeGate.SetResult(true);
            yield return WaitFor(stopTask);
            yield return WaitFor(startTask);
            stopTask.GetAwaiter().GetResult();
            startTask.GetAwaiter().GetResult();

            Assert.That(stoppedBeforeRelease, Is.False);
            Assert.That(disposeCountBeforeRelease, Is.Zero);
            AssertSerializedCleanup(runtime, module);
            var repeatedStop = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(repeatedStop);
            repeatedStop.GetAwaiter().GetResult();
            Assert.That(module.StopCount, Is.EqualTo(1));
            Assert.That(module.DisposeCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator StopAsync_DuringStart_WaitsThenCleansExactlyOnce()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var startGate = NewGate();
            var module = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true,
                StartGate = startGate
            };
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(module, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return null;
            Assert.That(startTask.IsCompleted, Is.False);
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Starting));

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return null;

            var stoppedBeforeRelease = stopTask.IsCompleted;
            var disposeCountBeforeRelease = module.DisposeCount;
            startGate.SetResult(true);
            yield return WaitFor(stopTask);
            yield return WaitFor(startTask);
            stopTask.GetAwaiter().GetResult();
            startTask.GetAwaiter().GetResult();

            Assert.That(stoppedBeforeRelease, Is.False);
            Assert.That(disposeCountBeforeRelease, Is.Zero);
            AssertSerializedCleanup(runtime, module);
        }

        [UnityTest]
        public IEnumerator DisposeAsync_DuringStart_WaitsThenCleansExactlyOnce()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var startGate = NewGate();
            var module = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true,
                StartGate = startGate
            };
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(module, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return null;
            Assert.That(startTask.IsCompleted, Is.False);

            var disposeTask = runtime.DisposeAsync().AsTask();
            yield return null;

            var disposedBeforeRelease = disposeTask.IsCompleted;
            var disposeCountBeforeRelease = module.DisposeCount;
            startGate.SetResult(true);
            yield return WaitFor(disposeTask);
            yield return WaitFor(startTask);
            disposeTask.GetAwaiter().GetResult();
            startTask.GetAwaiter().GetResult();

            Assert.That(disposedBeforeRelease, Is.False);
            Assert.That(disposeCountBeforeRelease, Is.Zero);
            AssertSerializedCleanup(runtime, module);
            var repeatedStop = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(repeatedStop);
            repeatedStop.GetAwaiter().GetResult();
            Assert.That(module.DisposeCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator FailedInitialize_CleansAllCreatedModulesAndPreservesFailure()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true
            };
            var failure = new TestInitializeException();
            var resource = new RecordingModule(
                "Resource",
                new[] { "Core" },
                events)
            {
                TrackScopeDisposal = true,
                InitializeFailure = failure
            };
            var descriptors = new[]
            {
                Descriptor(resource, 1, events),
                Descriptor(core, 0, events)
            };

            var startTask = runtime.StartAsync(descriptors, CancellationToken.None).AsTask();
            yield return WaitFor(startTask);
            var exception = Assert.Throws<TestInitializeException>(
                () => startTask.GetAwaiter().GetResult());

            Assert.That(exception, Is.SameAs(failure));
            Assert.That(runtime.Modules[0].State, Is.EqualTo(ModuleState.Unloaded));
            Assert.That(runtime.Modules[1].State, Is.EqualTo(ModuleState.Faulted));
            Assert.That(runtime.Modules[1].LastException, Is.SameAs(failure));
            CollectionAssert.AreEqual(
                new[]
                {
                    "Core.Factory",
                    "Core.Initialize",
                    "Core.Start",
                    "Resource.Factory",
                    "Resource.Initialize",
                    "Resource.Dispose",
                    "Resource.ScopeDispose",
                    "Core.Stop",
                    "Core.Dispose",
                    "Core.ScopeDispose"
                },
                events);
            Assert.That(runtime.Services.TryResolve<CoreScopeDisposable>(out _), Is.False);
            Assert.That(runtime.Services.TryResolve<ResourceScopeDisposable>(out _), Is.False);
        }

        [UnityTest]
        public IEnumerator StopAsync_ModuleDisposeFailure_ContinuesAndPropagatesFailure()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var failure = new TestModuleDisposeException();
            var core = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true
            };
            var resource = new RecordingModule(
                "Resource",
                new[] { "Core" },
                events)
            {
                TrackScopeDisposal = true,
                DisposeFailure = failure
            };
            var startTask = runtime
                .StartAsync(
                    new[]
                    {
                        Descriptor(resource, 1, events),
                        Descriptor(core, 0, events)
                    },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            var exception = Assert.Throws<TestModuleDisposeException>(
                () => stopTask.GetAwaiter().GetResult());

            Assert.That(exception, Is.SameAs(failure));
            AssertStopFailureResult(runtime, failure);
            AssertReverseCleanupOrder(events);
        }

        [UnityTest]
        public IEnumerator StopAsync_ScopeDisposeFailure_ContinuesAndPropagatesFailure()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var failure = new TestScopeDisposeException();
            var core = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true
            };
            var resource = new RecordingModule(
                "Resource",
                new[] { "Core" },
                events)
            {
                TrackScopeDisposal = true,
                ScopeDisposeFailure = failure
            };
            var startTask = runtime
                .StartAsync(
                    new[]
                    {
                        Descriptor(resource, 1, events),
                        Descriptor(core, 0, events)
                    },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            var exception = Assert.Throws<TestScopeDisposeException>(
                () => stopTask.GetAwaiter().GetResult());

            Assert.That(exception, Is.SameAs(failure));
            AssertStopFailureResult(runtime, failure);
            AssertReverseCleanupOrder(events);
        }

        [UnityTest]
        public IEnumerator StopAsync_ModuleAndScopeDisposeFailures_PropagatesFirstFailure()
        {
            var events = new List<string>();
            var logger = new RecordingLogger();
            var runtime = new FrameworkRuntime(logger);
            var moduleFailure = new TestModuleDisposeException();
            var scopeFailure = new TestScopeDisposeException();
            var core = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true
            };
            var resource = new RecordingModule(
                "Resource",
                new[] { "Core" },
                events)
            {
                TrackScopeDisposal = true,
                DisposeFailure = moduleFailure,
                ScopeDisposeFailure = scopeFailure
            };
            var startTask = runtime
                .StartAsync(
                    new[]
                    {
                        Descriptor(resource, 1, events),
                        Descriptor(core, 0, events)
                    },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            var exception = Assert.Throws<TestModuleDisposeException>(
                () => stopTask.GetAwaiter().GetResult());

            Assert.That(exception, Is.SameAs(moduleFailure));
            AssertStopFailureResult(runtime, moduleFailure);
            Assert.That(logger.Errors.Any(error => error.Exception == scopeFailure), Is.True);
            AssertReverseCleanupOrder(events);
        }

        [UnityTest]
        public IEnumerator StopAsync_RepeatedCall_CleansOnlyOnce()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var module = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                TrackScopeDisposal = true
            };
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(module, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();

            var firstStop = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(firstStop);
            firstStop.GetAwaiter().GetResult();
            var secondStop = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(secondStop);
            secondStop.GetAwaiter().GetResult();

            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Unloaded));
            Assert.That(module.StopCount, Is.EqualTo(1));
            Assert.That(module.DisposeCount, Is.EqualTo(1));
            Assert.That(events.Count(item => item == "Core.ScopeDispose"), Is.EqualTo(1));
            Assert.That(runtime.Services.TryResolve<CoreScopeDisposable>(out _), Is.False);
        }

        [UnityTest]
        public IEnumerator InstallAsync_StartsNewModuleWithoutRestartingExistingModules()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var feature = new RecordingModule(
                "Feature",
                new[] { "Core" },
                events);
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(core, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();

            var installTask = runtime
                .InstallAsync(
                    Descriptor(feature, 1, events),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(installTask);
            installTask.GetAwaiter().GetResult();

            CollectionAssert.AreEqual(
                new[] { "Core", "Feature" },
                runtime.Modules.Select(record => record.Descriptor.Id));
            Assert.That(
                events.Count(value => value == "Core.Start"),
                Is.EqualTo(1));
            Assert.That(
                events.Count(value => value == "Feature.Start"),
                Is.EqualTo(1));
            Assert.That(
                runtime.Modules.All(record => record.State == ModuleState.Running),
                Is.True);
        }

        [UnityTest]
        public IEnumerator InstallAsync_InstalledFrameModuleReceivesUpdates()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var frame = new FrameRecordingModule(
                "Frame",
                new[] { "Core" },
                events);
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(core, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();

            var installTask = runtime
                .InstallAsync(
                    Descriptor(frame, 1, events),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(installTask);
            installTask.GetAwaiter().GetResult();

            runtime.Update(0.1f);
            runtime.LateUpdate(0.1f);
            runtime.FixedUpdate(0.02f);

            Assert.That(frame.UpdateCount, Is.EqualTo(1));
            Assert.That(frame.LateUpdateCount, Is.EqualTo(1));
            Assert.That(frame.FixedUpdateCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator InstallAsync_AllowsUnrelatedExistingModuleToBeFaulted()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var failure = new TestFrameException();
            var faulted = new FrameRecordingModule(
                "Faulted",
                Array.Empty<string>(),
                events)
            {
                UpdateFailure = failure
            };
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(faulted, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            runtime.Update(0.1f);
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Faulted));

            var feature = new RecordingModule(
                "Feature",
                Array.Empty<string>(),
                events);
            var installTask = runtime
                .InstallAsync(
                    Descriptor(feature, 1, events),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(installTask);
            installTask.GetAwaiter().GetResult();

            Assert.That(
                runtime.Modules.Single(record => record.Descriptor.Id == "Faulted")
                    .LastException,
                Is.SameAs(failure));
            Assert.That(
                runtime.Modules.Single(record => record.Descriptor.Id == "Feature")
                    .State,
                Is.EqualTo(ModuleState.Running));
        }

        [UnityTest]
        public IEnumerator InstallAsync_RejectsDuplicateModuleIdWithoutMutation()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var duplicate = new RecordingModule(
                "Core",
                Array.Empty<string>(),
                events);
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(core, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var installTask = runtime
                .InstallAsync(
                    Descriptor(duplicate, 1, events),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(installTask);
            var exception = Assert.Throws<InvalidOperationException>(
                () => installTask.GetAwaiter().GetResult());

            Assert.That(exception.Message, Does.Contain("Core"));
            Assert.That(runtime.Modules.Count, Is.EqualTo(1));
            Assert.That(runtime.Modules.Single().Module, Is.SameAs(core));
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));
            Assert.That(events, Is.Empty);
        }

        [UnityTest]
        public IEnumerator InstallAsync_RejectsMissingDependencyWithoutMutation()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var feature = new RecordingModule(
                "Feature",
                new[] { "Missing" },
                events);
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(core, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var installTask = runtime
                .InstallAsync(
                    Descriptor(feature, 1, events),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(installTask);
            var exception = Assert.Throws<InvalidOperationException>(
                () => installTask.GetAwaiter().GetResult());

            Assert.That(exception.Message, Does.Contain("Feature"));
            Assert.That(exception.Message, Does.Contain("Missing"));
            Assert.That(runtime.Modules.Count, Is.EqualTo(1));
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));
            Assert.That(events, Is.Empty);
        }

        [UnityTest]
        public IEnumerator InstallAsync_RejectsRuntimeThatHasNotStartedOrHasStopped()
        {
            var events = new List<string>();
            var notStartedRuntime = new FrameworkRuntime(new RecordingLogger());
            var feature = new RecordingModule(
                "Feature",
                Array.Empty<string>(),
                events);

            var notStartedTask = notStartedRuntime
                .InstallAsync(
                    Descriptor(feature, 0, events),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(notStartedTask);
            Assert.Throws<InvalidOperationException>(
                () => notStartedTask.GetAwaiter().GetResult());

            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(core, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            stopTask.GetAwaiter().GetResult();

            var stoppedTask = runtime
                .InstallAsync(
                    Descriptor(feature, 1, events),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(stoppedTask);
            Assert.Throws<InvalidOperationException>(
                () => stoppedTask.GetAwaiter().GetResult());
            Assert.That(feature.Context, Is.Null);
        }

        [UnityTest]
        public IEnumerator InstallAsync_PreCanceledTokenDoesNotCreateCandidate()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var feature = new RecordingModule(
                "Feature",
                new[] { "Core" },
                events);
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(core, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var installTask = runtime
                .InstallAsync(
                    Descriptor(feature, 1, events),
                    cancellation.Token)
                .AsTask();
            yield return WaitFor(installTask);
            Assert.Catch<OperationCanceledException>(
                () => installTask.GetAwaiter().GetResult());

            Assert.That(runtime.Modules.Count, Is.EqualTo(1));
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));
            Assert.That(events, Is.Empty);
        }

        [UnityTest]
        public IEnumerator InstallAsync_FactoryFailurePreservesExistingModules()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(core, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();
            var failure = new TestFactoryException();
            var descriptor = new ModuleDescriptor(
                "Feature",
                new[] { "Core" },
                1,
                () =>
                {
                    events.Add("Feature.Factory");
                    throw failure;
                });

            var installTask = runtime
                .InstallAsync(descriptor, CancellationToken.None)
                .AsTask();
            yield return WaitFor(installTask);
            var exception = Assert.Throws<TestFactoryException>(
                () => installTask.GetAwaiter().GetResult());

            Assert.That(exception, Is.SameAs(failure));
            Assert.That(runtime.Modules.Count, Is.EqualTo(1));
            Assert.That(runtime.Modules.Single().Module, Is.SameAs(core));
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));
            CollectionAssert.AreEqual(new[] { "Feature.Factory" }, events);
        }

        [UnityTest]
        public IEnumerator InstallAsync_InitializeFailureCleansCandidateOnly()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var failure = new TestInitializeException();
            var feature = new RecordingModule(
                "Feature",
                new[] { "Core" },
                events)
            {
                TrackScopeDisposal = true,
                InitializeFailure = failure
            };
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(core, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var installTask = runtime
                .InstallAsync(
                    Descriptor(feature, 1, events),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(installTask);
            var exception = Assert.Throws<TestInitializeException>(
                () => installTask.GetAwaiter().GetResult());

            Assert.That(exception, Is.SameAs(failure));
            Assert.That(runtime.Modules.Count, Is.EqualTo(1));
            Assert.That(runtime.Modules.Single().Module, Is.SameAs(core));
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));
            Assert.That(feature.StopCount, Is.Zero);
            Assert.That(feature.DisposeCount, Is.EqualTo(1));
            CollectionAssert.AreEqual(
                new[]
                {
                    "Feature.Factory",
                    "Feature.Initialize",
                    "Feature.Dispose",
                    "Feature.ScopeDispose"
                },
                events);
            Assert.That(
                runtime.Services.TryResolve<ResourceScopeDisposable>(out _),
                Is.False);
        }

        [UnityTest]
        public IEnumerator InstallAsync_StartAndCleanupFailuresAreAggregated()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var startFailure = new TestStartException();
            var disposeFailure = new TestModuleDisposeException();
            var scopeFailure = new TestScopeDisposeException();
            var feature = new RecordingModule(
                "Feature",
                new[] { "Core" },
                events)
            {
                TrackScopeDisposal = true,
                StartFailure = startFailure,
                DisposeFailure = disposeFailure,
                ScopeDisposeFailure = scopeFailure
            };
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(core, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var installTask = runtime
                .InstallAsync(
                    Descriptor(feature, 1, events),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(installTask);
            var exception = Assert.Throws<AggregateException>(
                () => installTask.GetAwaiter().GetResult());

            CollectionAssert.AreEquivalent(
                new Exception[] { startFailure, disposeFailure, scopeFailure },
                exception.Flatten().InnerExceptions);
            Assert.That(runtime.Modules.Count, Is.EqualTo(1));
            Assert.That(runtime.Modules.Single().Module, Is.SameAs(core));
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));
            Assert.That(feature.StopCount, Is.Zero);
            Assert.That(feature.DisposeCount, Is.EqualTo(1));
            Assert.That(
                runtime.Services.TryResolve<ResourceScopeDisposable>(out _),
                Is.False);
        }

        [Test]
        public void ModuleUnloadResult_CopiesAndValidatesModuleIds()
        {
            var ids = new[] { "Feature", "Core" };

            var result = new ModuleUnloadResult(ids);
            ids[0] = "Changed";

            CollectionAssert.AreEqual(
                new[] { "Feature", "Core" },
                result.UnloadedModuleIds);
            Assert.Throws<ArgumentNullException>(
                () => new ModuleUnloadResult(null));
            Assert.Throws<ArgumentException>(
                () => new ModuleUnloadResult(new[] { "Feature", " " }));
            Assert.Throws<ArgumentException>(
                () => new ModuleUnloadResult(new[] { "Feature", "Feature" }));
        }

        [UnityTest]
        public IEnumerator UnloadAsync_DefaultModeRejectsRunningDependents()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var feature = new RecordingModule(
                "Feature",
                new[] { "Core" },
                events);
            var startTask = runtime
                .StartAsync(
                    new[]
                    {
                        Descriptor(feature, 1, events),
                        Descriptor(core, 0, events)
                    },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var unloadTask = runtime
                .UnloadAsync(
                    "Core",
                    ModuleUnloadMode.RequireNoDependents,
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(unloadTask);
            var exception = Assert.Throws<InvalidOperationException>(
                () => unloadTask.GetAwaiter().GetResult());

            Assert.That(exception.Message, Does.Contain("Feature"));
            Assert.That(
                runtime.Modules.All(record => record.State == ModuleState.Running),
                Is.True);
            Assert.That(events, Is.Empty);
        }

        [UnityTest]
        public IEnumerator UnloadAsync_LeafModuleRemovesRecordAndReturnsOrder()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var feature = new RecordingModule(
                "Feature",
                new[] { "Core" },
                events)
            {
                OwnScopeDisposal = true
            };
            var startTask = runtime
                .StartAsync(
                    new[]
                    {
                        Descriptor(feature, 1, events),
                        Descriptor(core, 0, events)
                    },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var unloadTask = runtime
                .UnloadAsync("Feature", token: CancellationToken.None)
                .AsTask();
            yield return WaitFor(unloadTask);
            var result = unloadTask.GetAwaiter().GetResult();

            CollectionAssert.AreEqual(
                new[] { "Feature" },
                result.UnloadedModuleIds);
            Assert.That(runtime.Modules.Single().Descriptor.Id, Is.EqualTo("Core"));
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));
            CollectionAssert.AreEqual(
                new[]
                {
                    "Feature.Stop",
                    "Feature.Dispose",
                    "Feature.ScopeDispose"
                },
                events);
        }

        [UnityTest]
        public IEnumerator UnloadAsync_RejectsInvalidStateAndUnknownModule()
        {
            var events = new List<string>();
            var descriptorModule = new RecordingModule(
                "Feature",
                Array.Empty<string>(),
                events);
            var descriptor = Descriptor(descriptorModule, 0, events);
            var notStartedRuntime = new FrameworkRuntime(new RecordingLogger());

            Assert.Throws<ArgumentException>(
                () => notStartedRuntime.UnloadAsync(" "));
            var notStartedTask = notStartedRuntime.UnloadAsync("Feature").AsTask();
            yield return WaitFor(notStartedTask);
            Assert.Throws<InvalidOperationException>(
                () => notStartedTask.GetAwaiter().GetResult());

            var runtime = new FrameworkRuntime(new RecordingLogger());
            var startTask = runtime
                .StartAsync(new[] { descriptor }, CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();

            var unknownTask = runtime.UnloadAsync("Missing").AsTask();
            yield return WaitFor(unknownTask);
            var unknownException = Assert.Throws<InvalidOperationException>(
                () => unknownTask.GetAwaiter().GetResult());
            Assert.That(unknownException.Message, Does.Contain("Missing"));
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));

            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            stopTask.GetAwaiter().GetResult();
            var stoppedTask = runtime.UnloadAsync("Feature").AsTask();
            yield return WaitFor(stoppedTask);
            Assert.Throws<InvalidOperationException>(
                () => stoppedTask.GetAwaiter().GetResult());
        }

        [UnityTest]
        public IEnumerator UnloadAsync_PreCanceledTokenDoesNotChangeState()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var module = new RecordingModule(
                "Feature",
                Array.Empty<string>(),
                events);
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(module, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();
            var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var unloadTask = runtime
                .UnloadAsync(
                    "Feature",
                    ModuleUnloadMode.RequireNoDependents,
                    cancellation.Token)
                .AsTask();
            yield return WaitFor(unloadTask);
            Assert.Catch<OperationCanceledException>(
                () => unloadTask.GetAwaiter().GetResult());

            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));
            Assert.That(events, Is.Empty);
        }

        [UnityTest]
        public IEnumerator UnloadAsync_CascadeCleansReverseTopologyAndKeepsOtherRunning()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var other = new FrameRecordingModule(
                "Other",
                Array.Empty<string>(),
                events);
            var core = new RecordingModule("Core", Array.Empty<string>(), events)
            {
                OwnScopeDisposal = true
            };
            var featureA = new RecordingModule(
                "FeatureA",
                new[] { "Core" },
                events)
            {
                OwnScopeDisposal = true
            };
            var featureB = new RecordingModule(
                "FeatureB",
                new[] { "FeatureA" },
                events)
            {
                OwnScopeDisposal = true
            };
            var startTask = runtime
                .StartAsync(
                    new[]
                    {
                        Descriptor(featureB, 3, events),
                        Descriptor(other, -1, events),
                        Descriptor(featureA, 2, events),
                        Descriptor(core, 1, events)
                    },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var unloadTask = runtime
                .UnloadAsync(
                    "Core",
                    ModuleUnloadMode.Cascade,
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(unloadTask);
            var result = unloadTask.GetAwaiter().GetResult();
            runtime.Update(0.1f);

            CollectionAssert.AreEqual(
                new[] { "FeatureB", "FeatureA", "Core" },
                result.UnloadedModuleIds);
            CollectionAssert.AreEqual(
                new[]
                {
                    "FeatureB.Stop",
                    "FeatureB.Dispose",
                    "FeatureB.ScopeDispose",
                    "FeatureA.Stop",
                    "FeatureA.Dispose",
                    "FeatureA.ScopeDispose",
                    "Core.Stop",
                    "Core.Dispose",
                    "Core.ScopeDispose"
                },
                events);
            Assert.That(runtime.Modules.Single().Descriptor.Id, Is.EqualTo("Other"));
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));
            Assert.That(other.UpdateCount, Is.EqualTo(1));
            Assert.That(
                other.Events.Count(value => value == "Other.Start"),
                Is.Zero);
        }

        [UnityTest]
        public IEnumerator UnloadAsync_CleanupFailuresContinueAndAreAggregated()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var stopFailure = new TestStopException("expected");
            var disposeFailure = new TestModuleDisposeException();
            var scopeFailure = new TestScopeDisposeException();
            var other = new FrameRecordingModule(
                "Other",
                Array.Empty<string>(),
                events);
            var core = new FrameRecordingModule(
                "Core",
                Array.Empty<string>(),
                events)
            {
                OwnScopeDisposal = true,
                ScopeDisposeFailure = scopeFailure
            };
            var featureA = new FrameRecordingModule(
                "FeatureA",
                new[] { "Core" },
                events)
            {
                OwnScopeDisposal = true,
                DisposeFailure = disposeFailure
            };
            var featureB = new FrameRecordingModule(
                "FeatureB",
                new[] { "FeatureA" },
                events)
            {
                OwnScopeDisposal = true,
                StopFailure = stopFailure
            };
            var startTask = runtime
                .StartAsync(
                    new[]
                    {
                        Descriptor(featureB, 3, events),
                        Descriptor(other, -1, events),
                        Descriptor(featureA, 2, events),
                        Descriptor(core, 1, events)
                    },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            var unloadTask = runtime
                .UnloadAsync(
                    "Core",
                    ModuleUnloadMode.Cascade,
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(unloadTask);
            var exception = Assert.Throws<AggregateException>(
                () => unloadTask.GetAwaiter().GetResult());
            runtime.Update(0.1f);

            CollectionAssert.AreEquivalent(
                new Exception[] { stopFailure, disposeFailure, scopeFailure },
                exception.Flatten().InnerExceptions);
            Assert.That(runtime.Modules.Single().Descriptor.Id, Is.EqualTo("Other"));
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));
            Assert.That(other.UpdateCount, Is.EqualTo(1));
            Assert.That(core.UpdateCount, Is.Zero);
            Assert.That(featureA.UpdateCount, Is.Zero);
            Assert.That(featureB.UpdateCount, Is.Zero);
            CollectionAssert.AreEqual(
                new[]
                {
                    "FeatureB.Stop",
                    "FeatureB.Dispose",
                    "FeatureB.ScopeDispose",
                    "FeatureA.Stop",
                    "FeatureA.Dispose",
                    "FeatureA.ScopeDispose",
                    "Core.Stop",
                    "Core.Dispose",
                    "Core.ScopeDispose"
                },
                events);
        }

        [UnityTest]
        public IEnumerator UnloadAsync_LastModuleCanBeFollowedByInstall()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var core = new RecordingModule("Core", Array.Empty<string>(), events);
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(core, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();

            var unloadTask = runtime.UnloadAsync("Core").AsTask();
            yield return WaitFor(unloadTask);
            unloadTask.GetAwaiter().GetResult();
            Assert.That(runtime.Modules, Is.Empty);

            var feature = new RecordingModule(
                "Feature",
                Array.Empty<string>(),
                events);
            var installTask = runtime
                .InstallAsync(
                    Descriptor(feature, 0, events),
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(installTask);
            installTask.GetAwaiter().GetResult();

            Assert.That(runtime.Modules.Single().Descriptor.Id, Is.EqualTo("Feature"));
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Running));
        }

        [UnityTest]
        public IEnumerator FrameFault_StopAndDispose_CleansModuleAndScope()
        {
            var events = new List<string>();
            var runtime = new FrameworkRuntime(new RecordingLogger());
            var failure = new TestFrameException();
            var module = new FrameRecordingModule(
                "Core",
                Array.Empty<string>(),
                events)
            {
                TrackScopeDisposal = true,
                UpdateFailure = failure
            };
            var startTask = runtime
                .StartAsync(
                    new[] { Descriptor(module, 0, events) },
                    CancellationToken.None)
                .AsTask();
            yield return WaitFor(startTask);
            startTask.GetAwaiter().GetResult();
            events.Clear();

            runtime.Update(0.1f);
            var stopTask = runtime.StopAsync(CancellationToken.None).AsTask();
            yield return WaitFor(stopTask);
            stopTask.GetAwaiter().GetResult();
            var disposeTask = runtime.DisposeAsync().AsTask();
            yield return WaitFor(disposeTask);
            disposeTask.GetAwaiter().GetResult();

            var record = runtime.Modules.Single();
            Assert.That(record.State, Is.EqualTo(ModuleState.Unloaded));
            Assert.That(record.LastException, Is.SameAs(failure));
            Assert.That(module.StopCount, Is.Zero);
            Assert.That(module.DisposeCount, Is.EqualTo(1));
            CollectionAssert.AreEqual(
                new[] { "Core.Dispose", "Core.ScopeDispose" },
                events);
            Assert.That(runtime.Services.TryResolve<CoreScopeDisposable>(out _), Is.False);
        }

        private static ModuleDescriptor Descriptor(
            RecordingModule module,
            int stableOrder,
            ICollection<string> events)
        {
            return Descriptor(
                module.Id,
                module.Dependencies,
                module,
                stableOrder,
                events);
        }

        private static ModuleDescriptor Descriptor(
            string descriptorId,
            IReadOnlyCollection<string> descriptorDependencies,
            RecordingModule module,
            int stableOrder,
            ICollection<string> events)
        {
            return new ModuleDescriptor(
                descriptorId,
                descriptorDependencies,
                stableOrder,
                () =>
                {
                    events.Add($"{descriptorId}.Factory");
                    return module;
                });
        }

        private static void AssertContractFailureCleanup(
            FrameworkRuntime runtime,
            RecordingModule failedModule,
            Exception failure)
        {
            Assert.That(runtime.Modules.Last().State, Is.EqualTo(ModuleState.Faulted));
            Assert.That(runtime.Modules.Last().Module, Is.SameAs(failedModule));
            Assert.That(runtime.Modules.Last().LastException, Is.SameAs(failure));
            Assert.That(
                runtime.Modules.Take(runtime.Modules.Count - 1)
                    .All(record => record.State == ModuleState.Unloaded),
                Is.True);
            Assert.That(failedModule.DisposeCount, Is.EqualTo(1));
        }

        private static void AssertSerializedCleanup(
            FrameworkRuntime runtime,
            RecordingModule module)
        {
            Assert.That(runtime.Modules.Single().State, Is.EqualTo(ModuleState.Unloaded));
            Assert.That(module.StopCount, Is.EqualTo(1));
            Assert.That(module.DisposeCount, Is.EqualTo(1));
            CollectionAssert.AreEqual(
                new[]
                {
                    "Core.Factory",
                    "Core.Initialize",
                    "Core.Start",
                    "Core.Stop",
                    "Core.Dispose",
                    "Core.ScopeDispose"
                },
                module.Events);
            Assert.That(runtime.Services.TryResolve<CoreScopeDisposable>(out _), Is.False);
        }

        private static void AssertStopFailureResult(
            FrameworkRuntime runtime,
            Exception failure)
        {
            var coreRecord = runtime.Modules.Single(
                record => record.Descriptor.Id == "Core");
            var resourceRecord = runtime.Modules.Single(
                record => record.Descriptor.Id == "Resource");
            Assert.That(coreRecord.State, Is.EqualTo(ModuleState.Unloaded));
            Assert.That(resourceRecord.State, Is.EqualTo(ModuleState.Faulted));
            Assert.That(resourceRecord.LastException, Is.SameAs(failure));
            Assert.That(runtime.Services.TryResolve<CoreScopeDisposable>(out _), Is.False);
            Assert.That(runtime.Services.TryResolve<ResourceScopeDisposable>(out _), Is.False);
        }

        private static void AssertReverseCleanupOrder(ICollection<string> events)
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    "Resource.Stop",
                    "Resource.Dispose",
                    "Resource.ScopeDispose",
                    "Core.Stop",
                    "Core.Dispose",
                    "Core.ScopeDispose"
                },
                events);
        }

        private static TaskCompletionSource<bool> NewGate()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static IEnumerator WaitFor(Task task)
        {
            for (var frame = 0; frame < 120 && !task.IsCompleted; frame++)
            {
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True, "Async operation timed out.");
        }

        private class RecordingModule : IFrameworkModule
        {
            private readonly IList<string> _events;

            public RecordingModule(
                string id,
                IReadOnlyCollection<string> dependencies,
                IList<string> events)
            {
                Id = id;
                Dependencies = dependencies;
                _events = events;
            }

            public string Id { get; }

            public IReadOnlyCollection<string> Dependencies { get; }

            public IList<string> Events => _events;

            public ModuleContext Context { get; private set; }

            public bool TrackScopeDisposal { get; set; }

            public bool OwnScopeDisposal { get; set; }

            public TaskCompletionSource<bool> InitializeGate { get; set; }

            public TaskCompletionSource<bool> StartGate { get; set; }

            public Exception InitializeFailure { get; set; }

            public Exception StartFailure { get; set; }

            public Exception StopFailure { get; set; }

            public Exception DisposeFailure { get; set; }

            public Exception ScopeDisposeFailure { get; set; }

            public int StopCount { get; private set; }

            public int DisposeCount { get; private set; }

            public virtual ValueTask InitializeAsync(
                ModuleContext context,
                CancellationToken token)
            {
                Context = context;
                _events.Add($"{Id}.Initialize");
                if (TrackScopeDisposal)
                {
                    if (Id == "Core")
                    {
                        context.ModuleScope.RegisterInstance(
                            new CoreScopeDisposable(
                                $"{Id}.ScopeDispose",
                                _events,
                                ScopeDisposeFailure));
                    }
                    else
                    {
                        context.ModuleScope.RegisterInstance(
                            new ResourceScopeDisposable(
                                $"{Id}.ScopeDispose",
                                _events,
                                ScopeDisposeFailure));
                    }
                }
                else if (OwnScopeDisposal)
                {
                    context.ModuleScope.Own(
                        new RecordingDisposable(
                            $"{Id}.ScopeDispose",
                            _events,
                            ScopeDisposeFailure));
                }

                return CompletePhaseAsync(InitializeGate, InitializeFailure);
            }

            public virtual ValueTask StartAsync(CancellationToken token)
            {
                _events.Add($"{Id}.Start");
                return CompletePhaseAsync(StartGate, StartFailure);
            }

            public virtual ValueTask StopAsync(CancellationToken token)
            {
                StopCount++;
                _events.Add($"{Id}.Stop");
                return StopFailure == null
                    ? default
                    : new ValueTask(Task.FromException(StopFailure));
            }

            public virtual ValueTask DisposeAsync()
            {
                DisposeCount++;
                _events.Add($"{Id}.Dispose");
                return DisposeFailure == null
                    ? default
                    : new ValueTask(Task.FromException(DisposeFailure));
            }

            private static ValueTask CompletePhaseAsync(
                TaskCompletionSource<bool> gate,
                Exception failure)
            {
                if (gate == null)
                {
                    return failure == null
                        ? default
                        : new ValueTask(Task.FromException(failure));
                }

                return new ValueTask(CompleteGatedPhaseAsync(gate.Task, failure));
            }

            private static async Task CompleteGatedPhaseAsync(
                Task gate,
                Exception failure)
            {
                await gate;
                if (failure != null)
                {
                    throw failure;
                }
            }
        }

        private sealed class FrameRecordingModule :
            RecordingModule,
            IUpdateModule,
            ILateUpdateModule,
            IFixedUpdateModule
        {
            public FrameRecordingModule(
                string id,
                IReadOnlyCollection<string> dependencies,
                IList<string> events)
                : base(id, dependencies, events)
            {
            }

            public Exception UpdateFailure { get; set; }

            public int UpdateCount { get; private set; }

            public int LateUpdateCount { get; private set; }

            public int FixedUpdateCount { get; private set; }

            public void Update(float deltaTime)
            {
                UpdateCount++;
                if (UpdateFailure != null)
                {
                    throw UpdateFailure;
                }
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

        private class RecordingDisposable : IDisposable
        {
            private readonly string _event;
            private readonly IList<string> _events;
            private readonly Exception _failure;

            public RecordingDisposable(
                string @event,
                IList<string> events,
                Exception failure = null)
            {
                _event = @event;
                _events = events;
                _failure = failure;
            }

            public void Dispose()
            {
                _events.Add(_event);
                if (_failure != null)
                {
                    throw _failure;
                }
            }
        }

        private sealed class CoreScopeDisposable : RecordingDisposable
        {
            public CoreScopeDisposable(
                string @event,
                IList<string> events,
                Exception failure = null)
                : base(@event, events, failure)
            {
            }
        }

        private sealed class ResourceScopeDisposable : RecordingDisposable
        {
            public ResourceScopeDisposable(
                string @event,
                IList<string> events,
                Exception failure = null)
                : base(@event, events, failure)
            {
            }
        }

        private sealed class RecordingLogger : IFrameworkLogger
        {
            public List<ErrorRecord> Errors { get; } = new List<ErrorRecord>();

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
                Errors.Add(new ErrorRecord(moduleId, category, message, exception));
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

        private sealed class TestStartException : Exception
        {
        }

        private sealed class TestFactoryException : Exception
        {
        }

        private sealed class TestInitializeException : Exception
        {
        }

        private sealed class TestCleanupException : Exception
        {
        }

        private sealed class TestModuleDisposeException : Exception
        {
        }

        private sealed class TestScopeDisposeException : Exception
        {
        }

        private sealed class TestFrameException : Exception
        {
        }

        private sealed class TestStopException : Exception
        {
            public TestStopException(string message)
                : base(message)
            {
            }
        }
    }
}
