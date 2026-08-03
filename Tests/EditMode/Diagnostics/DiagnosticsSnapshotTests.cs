using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArkFramework.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class DiagnosticsSnapshotTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly List<IAsyncDisposable> _asyncDisposables =
            new List<IAsyncDisposable>();
        private readonly List<FrameworkRuntime> _runtimes =
            new List<FrameworkRuntime>();

        [TearDown]
        public void TearDown()
        {
            for (var index = _runtimes.Count - 1; index >= 0; index--)
            {
                Await(_runtimes[index].StopAsync(CancellationToken.None));
                Await(_runtimes[index].DisposeAsync());
            }

            for (var index = _disposables.Count - 1; index >= 0; index--)
            {
                _disposables[index]?.Dispose();
            }

            for (var index = _asyncDisposables.Count - 1;
                 index >= 0;
                 index--)
            {
                Await(_asyncDisposables[index].DisposeAsync());
            }

            for (var index = _objects.Count - 1; index >= 0; index--)
            {
                if (_objects[index] != null)
                {
                    Object.DestroyImmediate(_objects[index]);
                }
            }
        }

        [Test]
        public void CaptureNull_ContainsExactlyTenOrderedEmptyPages()
        {
            var snapshot = DiagnosticsSnapshot.Capture(null);

            Assert.That(
                PageKinds(snapshot),
                Is.EqualTo(
                    new[]
                    {
                        DiagnosticsPageKind.Modules,
                        DiagnosticsPageKind.Events,
                        DiagnosticsPageKind.Resources,
                        DiagnosticsPageKind.Pools,
                        DiagnosticsPageKind.UI,
                        DiagnosticsPageKind.Audio,
                        DiagnosticsPageKind.Scene,
                        DiagnosticsPageKind.Config,
                        DiagnosticsPageKind.FSM,
                        DiagnosticsPageKind.Procedure
                    }));
            Assert.That(snapshot.Pages, Has.Count.EqualTo(10));
            Assert.That(snapshot.Pages, Has.All.Matches<DiagnosticsPageSnapshot>(
                page => page.Entries.Count == 0 && page.Error == null));
        }

        [Test]
        public void CaptureEmptyRuntime_ReturnsTenSafeEmptyPages()
        {
            var runtime = Track(new FrameworkRuntime());

            var snapshot = DiagnosticsSnapshot.Capture(runtime);

            Assert.That(snapshot.Pages, Has.Count.EqualTo(10));
            Assert.That(
                Page(snapshot, DiagnosticsPageKind.Modules).IsAvailable,
                Is.True);
            Assert.That(
                snapshot.Pages,
                Has.All.Matches<DiagnosticsPageSnapshot>(
                    page => page.Entries.Count == 0 && page.Error == null));
        }

        [Test]
        public void Collections_AreImmutableDeepSnapshots()
        {
            var snapshot = DiagnosticsSnapshot.Capture(null);
            var page = snapshot.Pages[0];

            Assert.Throws<NotSupportedException>(
                () => ((IList<DiagnosticsPageSnapshot>)snapshot.Pages)
                    .Add(page));
            Assert.Throws<NotSupportedException>(
                () => ((IList<DiagnosticsEntrySnapshot>)page.Entries)
                    .Add(new DiagnosticsEntrySnapshot(
                        "bad",
                        Array.Empty<DiagnosticsFieldSnapshot>())));

            var entry = new DiagnosticsEntrySnapshot(
                "entry",
                new[] { new DiagnosticsFieldSnapshot("name", "value") });
            Assert.Throws<NotSupportedException>(
                () => ((IList<DiagnosticsFieldSnapshot>)entry.Fields)
                    .Add(new DiagnosticsFieldSnapshot("bad", "bad")));
        }

        [Test]
        public void ThrowingService_FaultsOnlyItsOwnPage()
        {
            var runtime = Track(new FrameworkRuntime());
            Await(runtime.StartAsync(
                new[]
                {
                    new ModuleDescriptor(
                        "ThrowingEventBus",
                        Array.Empty<string>(),
                        0,
                        () => new ThrowingEventBusModule())
                },
                CancellationToken.None));

            var snapshot = DiagnosticsSnapshot.Capture(runtime);
            var events = Page(snapshot, DiagnosticsPageKind.Events);

            Assert.That(events.IsAvailable, Is.True);
            Assert.That(events.Error, Does.Contain("event diagnostics failed"));
            Assert.That(
                snapshot.Pages,
                Has.Exactly(1).Matches<DiagnosticsPageSnapshot>(
                    page => page.Error != null));
            Assert.That(
                Page(snapshot, DiagnosticsPageKind.Modules).Entries,
                Has.Count.EqualTo(1));
        }

        [Test]
        public void ThrowingExceptionFormatter_RemainsIsolatedToItsPage()
        {
            var runtime = Track(new FrameworkRuntime());
            Await(runtime.StartAsync(
                new[]
                {
                    new ModuleDescriptor(
                        "ThrowingEventBus",
                        Array.Empty<string>(),
                        0,
                        () => new ThrowingEventBusModule(
                            new ThrowingToStringException(
                                "diagnostics getter failed")))
                },
                CancellationToken.None));

            DiagnosticsSnapshot snapshot = null;
            Assert.DoesNotThrow(
                () => snapshot = DiagnosticsSnapshot.Capture(runtime));

            var events = Page(snapshot, DiagnosticsPageKind.Events);
            Assert.That(events.Error, Does.Contain(nameof(ThrowingToStringException)));
            Assert.That(
                Page(snapshot, DiagnosticsPageKind.Modules).Entries,
                Has.Count.EqualTo(1));
        }

        [Test]
        public void ModuleValues_AreCopiedAcrossRuntimeStop()
        {
            var runtime = Track(new FrameworkRuntime());
            Await(runtime.StartAsync(
                new[]
                {
                    new ModuleDescriptor(
                        "SnapshotModule",
                        Array.Empty<string>(),
                        7,
                        () => new EmptyModule("SnapshotModule"))
                },
                CancellationToken.None));
            var before = DiagnosticsSnapshot.Capture(runtime);
            var beforeState = Field(
                Page(before, DiagnosticsPageKind.Modules).Entries[0],
                "State");

            Await(runtime.StopAsync(CancellationToken.None));
            var after = DiagnosticsSnapshot.Capture(runtime);

            Assert.That(beforeState, Is.EqualTo("Running"));
            Assert.That(
                Field(
                    Page(before, DiagnosticsPageKind.Modules).Entries[0],
                    "State"),
                Is.EqualTo("Running"));
            Assert.That(
                Field(
                    Page(after, DiagnosticsPageKind.Modules).Entries[0],
                    "State"),
                Is.Not.EqualTo("Running"));
        }

        [Test]
        public void GameObjectPoolDiagnostics_ArePerKeyCopiedAndEmptySafe()
        {
            var resource = new LeaseResourceService(TrackObject);
            var pool = TrackDisposable(new GameObjectPool(resource, 2));
            Assert.That(pool.Diagnostics, Is.Empty);

            var a = Await(pool.RentAsync(new ResourceKey("a")));
            var b = Await(pool.RentAsync(new ResourceKey("b")));
            a.Dispose();
            var reusedA = Await(pool.RentAsync(new ResourceKey("a")));
            reusedA.Dispose();
            var copied = pool.Diagnostics;
            var aMetrics = copied[new ResourceKey("a")];
            var bMetrics = copied[new ResourceKey("b")];

            Assert.That(aMetrics.TotalCreatedCount, Is.EqualTo(1));
            Assert.That(aMetrics.ActiveCount, Is.Zero);
            Assert.That(aMetrics.IdleCount, Is.EqualTo(1));
            Assert.That(aMetrics.PeakActiveCount, Is.EqualTo(1));
            Assert.That(aMetrics.HitRate, Is.EqualTo(0.5d));
            Assert.That(bMetrics.ActiveCount, Is.EqualTo(1));
            Assert.Throws<NotSupportedException>(
                () => ((IDictionary<ResourceKey, PoolDiagnostics>)copied)
                    .Add(new ResourceKey("bad"), aMetrics));

            b.Dispose();
            pool.ClearAll();
            Assert.That(copied, Has.Count.EqualTo(2));
            Assert.That(pool.Diagnostics, Is.Empty);
        }

        [Test]
        public void ConfigValidationFailure_PreservesActiveAndClearsAfterSuccess()
        {
            var failValidation = false;
            var provider = new SequenceProvider(
                Snapshot("main", 1, "1"),
                Snapshot("main", 2, "2"),
                Snapshot("main", 3, "3"));
            var service = new ConfigService(
                new[] { provider },
                new RecordingEventBus());
            TrackAsyncDisposable(service);
            service.RegisterValidator<ConfigValue>(
                new DelegateValidator<ConfigValue>(
                    (key, value) =>
                    {
                        if (failValidation)
                        {
                            throw new InvalidOperationException(
                                "validator rejected candidate");
                        }
                    }));

            Await(service.ReloadAsync());
            failValidation = true;
            Assert.Throws<InvalidOperationException>(
                () => Await(service.ReloadAsync()));

            Assert.That(service.Get<ConfigValue>("main").Value, Is.EqualTo(1));
            Assert.That(service.Diagnostics.LastValidationSucceeded, Is.False);
            Assert.That(
                service.Diagnostics.LastValidationError,
                Does.Contain("validator rejected candidate"));

            failValidation = false;
            Await(service.ReloadAsync());
            Assert.That(service.Get<ConfigValue>("main").Value, Is.EqualTo(3));
            Assert.That(service.Diagnostics.LastValidationSucceeded, Is.True);
            Assert.That(service.Diagnostics.LastValidationError, Is.Null);
        }

        [Test]
        public void ConfigProviderFailure_IsNotReportedAsValidationFailure()
        {
            var provider = new SequenceProvider(Snapshot("main", 1, "1"));
            var service = new ConfigService(
                new[] { provider },
                new RecordingEventBus());
            TrackAsyncDisposable(service);
            Await(service.ReloadAsync());
            provider.Enqueue(
                new InvalidOperationException("provider load failed"));

            Assert.Throws<InvalidOperationException>(
                () => Await(service.ReloadAsync()));

            Assert.That(service.Get<ConfigValue>("main").Value, Is.EqualTo(1));
            Assert.That(service.Diagnostics.LastValidationSucceeded, Is.True);
            Assert.That(service.Diagnostics.LastValidationError, Is.Null);
        }

        [Test]
        public void ConfigPostValidationFailure_RecordsSuccessfulValidation()
        {
            var active = new EqualsThrowingConfig { Value = 1 };
            var failValidation = false;
            var provider = new SequenceProvider(
                SnapshotEquals("main", active, "1"),
                SnapshotEquals(
                    "main",
                    new EqualsThrowingConfig { Value = 2 },
                    "2"),
                SnapshotEquals(
                    "main",
                    new EqualsThrowingConfig { Value = 3 },
                    "1"));
            var service = new ConfigService(
                new[] { provider },
                new RecordingEventBus());
            TrackAsyncDisposable(service);
            service.RegisterValidator<EqualsThrowingConfig>(
                new DelegateValidator<EqualsThrowingConfig>(
                    (key, value) =>
                    {
                        if (failValidation)
                        {
                            throw new InvalidOperationException(
                                "validator failed");
                        }
                    }));
            Await(service.ReloadAsync());
            failValidation = true;
            Assert.Throws<InvalidOperationException>(
                () => Await(service.ReloadAsync()));
            Assert.That(service.Diagnostics.LastValidationSucceeded, Is.False);

            failValidation = false;
            active.ThrowOnEquals = true;
            Assert.Throws<InvalidOperationException>(
                () => Await(service.ReloadAsync()));

            Assert.That(
                service.Get<EqualsThrowingConfig>("main"),
                Is.SameAs(active));
            Assert.That(service.Diagnostics.LastValidationSucceeded, Is.True);
            Assert.That(service.Diagnostics.LastValidationError, Is.Null);
        }

        [Test]
        public void ConfigMaliciousValidatorException_RemainsPrimaryAndReadable()
        {
            var failure = new ThrowingToStringException(
                "validator rejected candidate");
            var provider = new SequenceProvider(
                Snapshot("main", 1, "1"));
            var service = new ConfigService(
                new[] { provider },
                new RecordingEventBus());
            TrackAsyncDisposable(service);
            service.RegisterValidator<ConfigValue>(
                new DelegateValidator<ConfigValue>(
                    (key, value) => throw failure));

            var thrown = Assert.Throws<ThrowingToStringException>(
                () => Await(service.ReloadAsync()));

            Assert.That(thrown, Is.SameAs(failure));
            Assert.DoesNotThrow(() => _ = service.Diagnostics);
            Assert.That(
                service.Diagnostics.LastValidationError,
                Does.Contain(nameof(ThrowingToStringException)));
        }

        [Test]
        public void FsmDiagnostics_ListCurrentTransitionsWithoutInvokingGuards()
        {
            var guardCalls = 0;
            var machine = new StateMachine<object>("machine", new object());
            TrackAsyncDisposable(machine);
            machine.RegisterState("A", new EmptyState());
            machine.RegisterState("B", new EmptyState());
            machine.RegisterState("C", new EmptyState());
            machine.RegisterTransition(
                new StateTransition<object>(
                    "A",
                    "go-b",
                    "B",
                    ignored =>
                    {
                        guardCalls++;
                        return true;
                    }));
            machine.RegisterTransition(
                new StateTransition<object>("A", "go-c", "C"));
            Await(machine.StartAsync("A"));

            var diagnostics = machine.GetDiagnostics();

            Assert.That(guardCalls, Is.Zero);
            Assert.That(
                diagnostics.AvailableTransitions,
                Has.Count.EqualTo(2));
            Assert.That(diagnostics.AvailableTransitions[0].Trigger, Is.EqualTo("go-b"));
            Assert.That(diagnostics.AvailableTransitions[0].TargetStateId, Is.EqualTo("B"));
            Assert.That(diagnostics.AvailableTransitions[0].HasGuard, Is.True);
            Assert.That(diagnostics.AvailableTransitions[1].Trigger, Is.EqualTo("go-c"));
        }

        [Test]
        public void ProcedureDiagnostics_AvailableTargetsExcludeCurrent()
        {
            var fsm = new FsmService();
            TrackAsyncDisposable(fsm);
            var service = new ProcedureService(fsm, new ServiceContainer());
            TrackAsyncDisposable(service);
            service.Register(new EmptyProcedure("A"));
            service.Register(new EmptyProcedure("B"));
            service.Register(new EmptyProcedure("C"));
            Await(service.StartAsync("B"));

            Assert.That(
                service.Diagnostics.AvailableTargetProcedureIds,
                Is.EqualTo(new[] { "A", "C" }));
        }

        [Test]
        public void DiagnosticsDrawer_FormatsConfigValidationState()
        {
            Assert.That(
                FrameworkDiagnosticsDrawer.FormatConfigValidation(
                    null,
                    null),
                Is.EqualTo("尚未校验"));
            Assert.That(
                FrameworkDiagnosticsDrawer.FormatConfigValidation(
                    true,
                    null),
                Is.EqualTo("校验通过"));
            Assert.That(
                FrameworkDiagnosticsDrawer.FormatConfigValidation(
                    false,
                    "字段 level 缺失"),
                Does.Contain("字段 level 缺失"));
        }

        [Test]
        public void DiagnosticsDrawer_SortsAndFormatsFsmTransitions()
        {
            var transitions = new[]
            {
                new FsmTransitionDiagnostics("z", "A", false),
                new FsmTransitionDiagnostics("a", "Z", true),
                new FsmTransitionDiagnostics("a", "A", false)
            };

            CollectionAssert.AreEqual(
                new[]
                {
                    "a -> A（Guard：无）",
                    "a -> Z（Guard：有）",
                    "z -> A（Guard：无）"
                },
                FrameworkDiagnosticsDrawer.FormatFsmTransitions(
                    transitions));
        }

        [Test]
        public void DiagnosticsDrawer_SortsProcedureTargets()
        {
            CollectionAssert.AreEqual(
                new[] { "Battle", "MainMenu", "Settings" },
                FrameworkDiagnosticsDrawer.SortProcedureTargets(
                    new[] { "Settings", "Battle", "MainMenu" }));
        }

        [Test]
        public void Overlay_SetupAndTeardownAreIdempotentAndReadonly()
        {
            var owner = TrackObject(new GameObject("overlay-owner"));
            var overlay = owner.AddComponent<RuntimeDebugOverlay>();

            Assert.That(owner.transform.childCount, Is.EqualTo(1));
            overlay.enabled = false;
            Assert.That(owner.transform.childCount, Is.Zero);
            overlay.enabled = true;
            Assert.That(owner.transform.childCount, Is.EqualTo(1));
            overlay.enabled = true;
            Assert.That(owner.transform.childCount, Is.EqualTo(1));

            var buttons = owner.GetComponentsInChildren<Button>(true);
            Assert.That(buttons, Has.Length.EqualTo(10));
            var labels = owner.GetComponentsInChildren<Text>(true);
            foreach (var label in labels)
            {
                Assert.That(label.text, Does.Not.Contain("Stop"));
                Assert.That(label.text, Does.Not.Contain("Restart"));
                Assert.That(label.text, Does.Not.Contain("Replace"));
            }

            Object.DestroyImmediate(overlay);
            Assert.That(owner.transform.childCount, Is.Zero);
        }

        [Test]
        public void Overlay_ScrollContentExpandsToTextPreferredHeight()
        {
            var owner = TrackObject(new GameObject("overlay-layout-owner"));
            owner.AddComponent<RuntimeDebugOverlay>();
            var scroll = owner.GetComponentInChildren<ScrollRect>(true);
            var content = scroll.content;
            var fitter = content.GetComponent<ContentSizeFitter>();
            var text = content.GetComponent<Text>();
            var builder = new StringBuilder();
            for (var index = 0; index < 300; index++)
            {
                builder.AppendLine($"diagnostics row {index}");
            }

            text.text = builder.ToString();
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(content);

            Assert.That(fitter, Is.Not.Null);
            Assert.That(
                fitter.verticalFit,
                Is.EqualTo(ContentSizeFitter.FitMode.PreferredSize));
            Assert.That(text.preferredHeight, Is.GreaterThan(1000f));
            Assert.That(
                content.rect.height,
                Is.GreaterThanOrEqualTo(text.preferredHeight - 0.1f));
        }

        [Test]
        public void Overlay_ReplacesLostBorrowedEventSystemWithoutTouchingUserObjects()
        {
            Assert.That(EventSystem.current, Is.Null);
            var eventSystemsField = typeof(EventSystem).GetField(
                "m_EventSystems",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(eventSystemsField, Is.Not.Null);
            var eventSystems =
                (List<EventSystem>)eventSystemsField.GetValue(null);
            var originalEventSystems = eventSystems.ToArray();
            try
            {
                var firstOwner =
                    TrackObject(new GameObject("first-overlay-owner"));
                var secondOwner =
                    TrackObject(new GameObject("second-overlay-owner"));
                var userChild = new GameObject("user-child");
                userChild.transform.SetParent(secondOwner.transform, false);
                firstOwner.AddComponent<RuntimeDebugOverlay>();
                Assert.That(
                    firstOwner.GetComponentsInChildren<EventSystem>(true),
                    Is.Empty);
                firstOwner.SendMessage(
                    "Update",
                    SendMessageOptions.DontRequireReceiver);
                var firstEventSystem =
                    firstOwner.GetComponentInChildren<EventSystem>(true);
                eventSystems.Clear();
                eventSystems.Add(firstEventSystem);
                var secondOverlay =
                    secondOwner.AddComponent<RuntimeDebugOverlay>();

                Assert.That(
                    firstOwner.GetComponentsInChildren<EventSystem>(true),
                    Has.Length.EqualTo(1));
                Assert.That(
                    secondOwner.GetComponentsInChildren<EventSystem>(true),
                    Is.Empty);

                Object.DestroyImmediate(firstOwner);
                eventSystems.Clear();
                Assert.That(EventSystem.current, Is.Null);
                secondOwner.SendMessage(
                    "Update",
                    SendMessageOptions.DontRequireReceiver);

                Assert.That(
                    secondOwner.GetComponentsInChildren<EventSystem>(true),
                    Has.Length.EqualTo(1));
                Object.DestroyImmediate(secondOverlay);
                Assert.That(secondOwner.transform.childCount, Is.EqualTo(1));
                Assert.That(
                    secondOwner.transform.GetChild(0).gameObject,
                    Is.SameAs(userChild));
                Assert.That(EventSystem.current, Is.Null);
            }
            finally
            {
                eventSystems.Clear();
                for (var index = 0;
                     index < originalEventSystems.Length;
                     index++)
                {
                    if (originalEventSystems[index] != null)
                    {
                        eventSystems.Add(originalEventSystems[index]);
                    }
                }
            }
        }

        private static DiagnosticsPageSnapshot Page(
            DiagnosticsSnapshot snapshot,
            DiagnosticsPageKind kind)
        {
            foreach (var page in snapshot.Pages)
            {
                if (page.Kind == kind)
                {
                    return page;
                }
            }

            Assert.Fail($"Missing diagnostics page '{kind}'.");
            return null;
        }

        private static DiagnosticsPageKind[] PageKinds(
            DiagnosticsSnapshot snapshot)
        {
            var values = new DiagnosticsPageKind[snapshot.Pages.Count];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = snapshot.Pages[index].Kind;
            }

            return values;
        }

        private static string Field(
            DiagnosticsEntrySnapshot entry,
            string name)
        {
            foreach (var field in entry.Fields)
            {
                if (field.Name == name)
                {
                    return field.Value;
                }
            }

            Assert.Fail($"Missing field '{name}'.");
            return null;
        }

        private FrameworkRuntime Track(FrameworkRuntime runtime)
        {
            _runtimes.Add(runtime);
            return runtime;
        }

        private T TrackDisposable<T>(T disposable)
            where T : IDisposable
        {
            _disposables.Add(disposable);
            return disposable;
        }

        private T TrackAsyncDisposable<T>(T disposable)
            where T : IAsyncDisposable
        {
            _asyncDisposables.Add(disposable);
            return disposable;
        }

        private T TrackObject<T>(T value)
            where T : Object
        {
            _objects.Add(value);
            return value;
        }

        private static void Await(ValueTask task)
        {
            task.AsTask().GetAwaiter().GetResult();
        }

        private static T Await<T>(ValueTask<T> task)
        {
            return task.AsTask().GetAwaiter().GetResult();
        }

        private sealed class EmptyModule : IFrameworkModule
        {
            private readonly string _id;

            public EmptyModule(string id)
            {
                _id = id;
            }

            public string Id => _id;
            public IReadOnlyCollection<string> Dependencies =>
                Array.Empty<string>();
            public ValueTask InitializeAsync(ModuleContext context, CancellationToken token) =>
                default;
            public ValueTask StartAsync(CancellationToken token) => default;
            public ValueTask StopAsync(CancellationToken token) => default;
            public ValueTask DisposeAsync() => default;
        }

        private sealed class ThrowingEventBusModule : IFrameworkModule
        {
            private readonly Exception _failure;

            public ThrowingEventBusModule(
                Exception failure = null)
            {
                _failure = failure ??
                    new InvalidOperationException(
                        "event diagnostics failed");
            }

            public string Id => "ThrowingEventBus";
            public IReadOnlyCollection<string> Dependencies =>
                Array.Empty<string>();

            public ValueTask InitializeAsync(ModuleContext context, CancellationToken token)
            {
                context.ModuleScope.RegisterInstance<IEventBus>(
                    new RecordingEventBus(_failure));
                return default;
            }

            public ValueTask StartAsync(CancellationToken token) => default;
            public ValueTask StopAsync(CancellationToken token) => default;
            public ValueTask DisposeAsync() => default;
        }

        private sealed class RecordingEventBus : IEventBus
        {
            private readonly Exception _diagnosticsFailure;

            public RecordingEventBus(Exception diagnosticsFailure = null)
            {
                _diagnosticsFailure = diagnosticsFailure;
            }

            public EventBusDiagnostics Diagnostics =>
                _diagnosticsFailure == null
                    ? null
                    : throw _diagnosticsFailure;

            public IDisposable Subscribe<TEvent>(Action<TEvent> handler) =>
                new EmptyDisposable();
            public IDisposable Subscribe<TEvent>(
                ModuleScope ownerScope,
                Action<TEvent> handler) =>
                new EmptyDisposable();
            public void Publish<TEvent>(TEvent value) { }
            public void Enqueue<TEvent>(TEvent value) { }
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public void Dispose() { }
        }

        private sealed class LeaseResourceService : IResourceService
        {
            private readonly Func<GameObject, GameObject> _track;
            private long _nextId;

            public LeaseResourceService(Func<GameObject, GameObject> track)
            {
                _track = track;
            }

            public ResourceDiagnostics Diagnostics => null;

            public ValueTask<IAssetLease<T>> LoadAsync<T>(
                ResourceKey key,
                CancellationToken token = default)
                where T : Object =>
                throw new NotSupportedException();

            public ValueTask<IInstanceLease> InstantiateAsync(
                ResourceKey key,
                Transform parent = null,
                CancellationToken token = default)
            {
                var instance = _track(new GameObject($"instance-{key}"));
                instance.transform.SetParent(parent, false);
                var constructor = typeof(InstanceLease).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(long),
                        typeof(ResourceKey),
                        typeof(GameObject),
                        typeof(DateTime),
                        typeof(Action)
                    },
                    null);
                var lease = (InstanceLease)constructor.Invoke(
                    new object[]
                    {
                        ++_nextId,
                        key,
                        instance,
                        DateTime.UtcNow,
                        new Action(() => { })
                    });
                return new ValueTask<IInstanceLease>(lease);
            }

            public ValueTask<IReadOnlyList<IAssetLease<T>>> LoadByLabelAsync<T>(
                string label,
                CancellationToken token = default)
                where T : Object =>
                throw new NotSupportedException();
        }

        private sealed class ConfigValue
        {
            public int Value { get; set; }
        }

        private sealed class EqualsThrowingConfig
        {
            public int Value { get; set; }

            public bool ThrowOnEquals { get; set; }

            public override bool Equals(object value)
            {
                if (ThrowOnEquals)
                {
                    throw new InvalidOperationException(
                        "config equality failed after validation");
                }

                return value is EqualsThrowingConfig other &&
                       other.Value == Value;
            }

            public override int GetHashCode()
            {
                return Value;
            }
        }

        private sealed class SequenceProvider : IConfigProvider
        {
            private readonly Queue<object> _results;

            public SequenceProvider(params ConfigProviderSnapshot[] snapshots)
            {
                _results = new Queue<object>();
                for (var index = 0; index < snapshots.Length; index++)
                {
                    _results.Enqueue(snapshots[index]);
                }
            }

            public string Name => "Sequence";

            public void Enqueue(Exception exception)
            {
                _results.Enqueue(exception);
            }

            public ValueTask<ConfigProviderSnapshot> LoadAsync(
                CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                var result = _results.Dequeue();
                if (result is Exception exception)
                {
                    throw exception;
                }

                return new ValueTask<ConfigProviderSnapshot>(
                    (ConfigProviderSnapshot)result);
            }
        }

        private sealed class DelegateValidator<T> : IConfigValidator<T>
        {
            private readonly Action<string, T> _validate;

            public DelegateValidator(Action<string, T> validate)
            {
                _validate = validate;
            }

            public void Validate(string key, T value)
            {
                _validate(key, value);
            }
        }

        private static ConfigProviderSnapshot Snapshot(
            string key,
            int value,
            string version)
        {
            return new ConfigProviderSnapshot(
                new[]
                {
                    new ConfigEntry(
                        new ConfigKey(typeof(ConfigValue), key),
                        new ConfigValue { Value = value },
                        "Sequence",
                        version)
                },
                Array.Empty<IDisposable>());
        }

        private static ConfigProviderSnapshot SnapshotEquals(
            string key,
            EqualsThrowingConfig value,
            string version)
        {
            return new ConfigProviderSnapshot(
                new[]
                {
                    new ConfigEntry(
                        new ConfigKey(typeof(EqualsThrowingConfig), key),
                        value,
                        "Sequence",
                        version)
                },
                Array.Empty<IDisposable>());
        }

        private sealed class ThrowingToStringException : Exception
        {
            public ThrowingToStringException(string message)
                : base(message)
            {
            }

            public override string ToString()
            {
                throw new InvalidOperationException(
                    "exception formatting failed");
            }
        }

        private sealed class EmptyState : IState<object>
        {
            public ValueTask EnterAsync(object context, CancellationToken token) =>
                default;
            public void Update(object context, float deltaTime) { }
            public ValueTask ExitAsync(object context, CancellationToken token) =>
                default;
        }

        private sealed class EmptyProcedure : ProcedureBase
        {
            private readonly string _id;

            public EmptyProcedure(string id)
            {
                _id = id;
            }

            public override string Id => _id;

            public override ValueTask EnterAsync(
                ProcedureContext context,
                CancellationToken token) =>
                default;

            public override ValueTask ExitAsync(
                ProcedureContext context,
                CancellationToken token) =>
                default;
        }
    }
}
