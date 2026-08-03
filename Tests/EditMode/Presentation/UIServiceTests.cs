using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class UIServiceTests
    {
        private FakeResourceBackend _backend;
        private ResourceService _resources;
        private GameObjectPool _pool;
        private RecordingEventBus _events;
        private UIRoot _root;
        private UIService _service;
        private HashSet<int> _initialRootIds;
        private HashSet<int> _initialEventSystemIds;

        [SetUp]
        public void SetUp()
        {
            _initialRootIds = SnapshotRuntimeIds<UIRoot>();
            _initialEventSystemIds = SnapshotRuntimeIds<EventSystem>();
            if (_initialRootIds.Count != 0)
            {
                Assert.Ignore(
                    "UI EditMode tests do not take ownership of an existing UIRoot.");
            }

            TestWindow.ResetCounters();
            _backend = new FakeResourceBackend();
            _resources = new ResourceService(_backend);
            _pool = new GameObjectPool(_resources);
            _events = new RecordingEventBus();
            _root = UIRoot.Create(dontDestroyOnLoad: false);
            _service = new UIService(_resources, _pool, _events, _root);
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_service != null)
            {
                var stop = _service.StopAsync().AsTask();
                yield return WaitFor(stop);
                Observe(stop);
            }

            _pool?.Dispose();
            if (_resources != null)
            {
                var dispose = _resources.DisposeAsync().AsTask();
                yield return WaitFor(dispose);
                Observe(dispose);
            }

            DestroyCreated<EventSystem>(_initialEventSystemIds);
            DestroyCreated<UIRoot>(_initialRootIds);
            TestWindow.ResetCounters();
        }

        [Test]
        public void DescriptorRejectsInvalidIdentityEnumsAndMaskCombinations()
        {
            Assert.Throws<ArgumentException>(
                () => Descriptor(id: " "));
            Assert.Throws<ArgumentException>(
                () => Descriptor(key: " "));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => Descriptor(layer: (UILayer)99));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => Descriptor(mode: (UIWindowMode)99));
            Assert.Throws<ArgumentException>(
                () => Descriptor(
                    layer: UILayer.Normal,
                    requiresMask: true));
            Assert.Throws<ArgumentException>(
                () => Descriptor(
                    layer: UILayer.Popup,
                    requiresMask: false,
                    closeOnMaskClick: true));
            Assert.Throws<ArgumentException>(
                () => Descriptor(
                    layer: UILayer.Popup,
                    requiresMask: false,
                    blocksInput: true));
            Assert.Throws<ArgumentException>(
                () => Descriptor(
                    layer: UILayer.System,
                    allowBack: true));

            var normal = Descriptor(layer: UILayer.Normal);
            var system = Descriptor(layer: UILayer.System);
            Assert.That(normal.AllowBack, Is.True);
            Assert.That(system.AllowBack, Is.False);
        }

        [UnityTest]
        public IEnumerator CachedWindowUsesSubstitutableGameObjectPoolContract()
        {
            var initialStop = _service.StopAsync().AsTask();
            yield return WaitFor(initialStop);
            Observe(initialStop);
            _service = null;
            yield return null;

            _root = UIRoot.Create(dontDestroyOnLoad: false);
            var pool = new RecordingGameObjectPool(
                CreatePrefab<TestWindow>("contract-window"));
            _service = new UIService(_resources, pool, _events, _root);
            _service.Register<TestWindow>(Descriptor(cacheOnClose: true));

            var openTask = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(openTask);
            openTask.GetAwaiter().GetResult();
            var stopTask = _service.StopAsync().AsTask();
            yield return WaitFor(stopTask);
            Observe(stopTask);
            _service = null;

            Assert.That(pool.RentCount, Is.EqualTo(1));
            Assert.That(pool.Handle.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void HandleOperationsRejectAlternativeImplementation()
        {
            var handle = new AlternativeWindowHandle();

            Assert.Throws<ArgumentException>(
                () => _service.CloseAsync(handle));
            Assert.Throws<ArgumentException>(
                () => _service.TryGetWindow(handle, out _));
        }

        [Test]
        public void RootCreatesFiveOrderedCanvasesAndReusesExistingEventSystem()
        {
            Assert.That(
                UIRoot.Create(dontDestroyOnLoad: false).GetInstanceID(),
                Is.EqualTo(_root.GetInstanceID()),
                "Create(false) must preserve one native root instance.");
            _root.Dispose();
            _root = null;
            _service = null;
            var external = new GameObject(
                "External.EventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
            var second = UIRoot.Create(dontDestroyOnLoad: false);
            try
            {
                var layers = second.Layers;
                Assert.That(layers.Count, Is.EqualTo(5));
                CollectionAssert.AreEqual(
                    new[]
                    {
                        UILayer.Background,
                        UILayer.Normal,
                        UILayer.Popup,
                        UILayer.Overlay,
                        UILayer.System
                    },
                    layers.Select(item => item.Layer));
                Assert.That(
                    layers.Select(item => item.SortingOrder).Distinct().Count(),
                    Is.EqualTo(5));
                Assert.That(
                    layers.Last().SortingOrder,
                    Is.GreaterThan(layers.Take(4).Max(item => item.SortingOrder)));
                Assert.That(
                    layers.All(
                        item =>
                            item.Root.GetComponent<Canvas>() != null &&
                            item.Root.GetComponent<CanvasScaler>() != null &&
                            item.Root.GetComponent<GraphicRaycaster>() != null),
                    Is.True);
                Assert.That(
                    Object.FindObjectsOfType<EventSystem>().Length,
                    Is.EqualTo(1));
                Assert.That(second.EventSystem, Is.SameAs(external.GetComponent<EventSystem>()));

                second.Dispose();
                Assert.That(external, Is.Not.Null);
            }
            finally
            {
                if (second != null)
                {
                    second.Dispose();
                }

                Object.DestroyImmediate(external);
            }
        }

        [Test]
        public void FixtureCleanupPreservesObjectsPresentAtSnapshot()
        {
            var protectedRoot = _root;
            var protectedEventSystem = _root.EventSystem;
            var createdEventObject = new GameObject(
                "Fixture.Created.EventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
            var createdRootObject = new GameObject(
                "Fixture.Created.UIRoot",
                typeof(RectTransform),
                typeof(UIRoot));
            createdRootObject.hideFlags = HideFlags.HideAndDontSave;

            DestroyCreated<EventSystem>(
                new HashSet<int>
                {
                    protectedEventSystem.GetInstanceID()
                });
            DestroyCreated<UIRoot>(
                new HashSet<int>
                {
                    protectedRoot.GetInstanceID()
                });

            Assert.That(protectedRoot, Is.Not.Null);
            Assert.That(protectedEventSystem, Is.Not.Null);
            Assert.That(createdEventObject == null, Is.True);
            Assert.That(createdRootObject == null, Is.True);
        }

        [UnityTest]
        public IEnumerator ExternallyDestroyedRootCleansOwnedEventSystemAndStopCompletes()
        {
            _service.Register<TestWindow>(Descriptor());
            TestWindow.SubscribeDuringOpen = true;
            _backend.EnqueuePrefab(
                CreatePrefab<TestWindow>("external-root"));
            var open = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(open);
            Assert.That(open.IsCompletedSuccessfully, Is.True);
            var handle = open.GetAwaiter().GetResult();

            var ownedEventSystem = _root.EventSystem;
            var ownsEventSystemField = typeof(UIRoot).GetField(
                "_ownsEventSystem",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(ownsEventSystemField, Is.Not.Null);
            Assert.That(
                ownsEventSystemField.GetValue(_root),
                Is.EqualTo(true));
            Object.DestroyImmediate(_root.gameObject);
            Assert.That(_root == null, Is.True);
            Assert.That(ownedEventSystem == null, Is.True);

            Task close = null;
            Assert.DoesNotThrow(
                () => close = _service.CloseAsync(handle).AsTask());
            var stop = _service.StopAsync().AsTask();
            yield return WaitFor(close, stop);
            Observe(close);
            Observe(stop);
            Assert.That(close.IsCompletedSuccessfully, Is.True);
            Assert.That(stop.IsCompletedSuccessfully, Is.True);
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            Assert.That(
                _resources.Diagnostics.OutstandingLeases,
                Is.Empty);
            _events.Publish(1);
            Assert.That(TestWindow.EventCount, Is.Zero);
            _service = null;
            _root = null;
        }

        [Test]
        public void RegistrationRejectsDuplicateTypeIdAndFreezesAtFirstOpen()
        {
            _service.Register<TestWindow>(Descriptor());
            Assert.Throws<InvalidOperationException>(
                () => _service.Register<TestWindow>(Descriptor(id: "other")));
            Assert.Throws<InvalidOperationException>(
                () => _service.Register<OtherWindow>(Descriptor()));

            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("single"));
            var open = _service.OpenAsync<TestWindow>().AsTask();
            Assert.That(open.IsCompleted, Is.True);
            Assert.Throws<InvalidOperationException>(
                () => _service.Register<OtherWindow>(
                    Descriptor(id: "late", key: "late")));
        }

        [UnityTest]
        public IEnumerator SingleFlightSharesOneLoadHandleAndOpenCallback()
        {
            _service.Register<TestWindow>(Descriptor());
            var operation = _backend.EnqueueGatedPrefab(
                CreatePrefab<TestWindow>("single"));

            var first = _service.OpenAsync<TestWindow>("first").AsTask();
            var second = _service.OpenAsync<TestWindow>("second").AsTask();
            Assert.That(_backend.InstantiateCount, Is.EqualTo(1));
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(second.IsCompleted, Is.False);

            operation.Complete();
            yield return WaitFor(first, second);
            var firstHandle = first.GetAwaiter().GetResult();
            var secondHandle = second.GetAwaiter().GetResult();

            Assert.That(secondHandle, Is.SameAs(firstHandle));
            Assert.That(firstHandle.InstanceId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(firstHandle.IsValid, Is.True);
            Assert.That(TestWindow.OpenCount, Is.EqualTo(1));

            var noOp = _service.OpenAsync<TestWindow>("ignored").AsTask();
            yield return WaitFor(noOp);
            Assert.That(noOp.GetAwaiter().GetResult(), Is.SameAs(firstHandle));
            Assert.That(TestWindow.OpenCount, Is.EqualTo(1));
            Assert.That(_backend.InstantiateCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator AcquisitionStaysInactiveUntilWindowLifetimeIsBound()
        {
            _service.Register<TestWindow>(
                Descriptor(
                    id: "cached",
                    key: "cached",
                    cacheOnClose: true));
            _service.Register<OtherWindow>(
                Descriptor(id: "direct", key: "direct"));
            _backend.EnqueuePrefab(
                CreatePrefab<TestWindow>("cached-active"));
            _backend.EnqueuePrefab(
                CreatePrefab<OtherWindow>("direct-active"));
            TestWindow.EnabledBeforeLifetime = false;

            var cached = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(cached);
            var direct = _service.OpenAsync<OtherWindow>().AsTask();
            yield return WaitFor(direct);

            Assert.That(
                TestWindow.EnabledBeforeLifetime,
                Is.False,
                "A pooled prefab activated before BeginLifetime.");
            Assert.That(
                _backend.RequestedParents.Count,
                Is.EqualTo(2));
            Assert.That(
                _backend.RequestedParents[1].gameObject.activeInHierarchy,
                Is.False,
                "A direct Addressables prefab was acquired under an active canvas.");
            Assert.That(
                _service.GetWindow(cached.GetAwaiter().GetResult())
                    .gameObject.activeInHierarchy,
                Is.True);
            Assert.That(
                _service.GetWindow(direct.GetAwaiter().GetResult())
                    .gameObject.activeInHierarchy,
                Is.True);
        }

        [UnityTest]
        public IEnumerator SingleFlightSharesFailureCleansOwnershipAndCanRetry()
        {
            _service.Register<TestWindow>(Descriptor());
            var operation = _backend.EnqueueGatedPrefab(
                CreatePrefab<TestWindow>("failure"));
            var primary = new InvalidOperationException("open failed");
            TestWindow.OpenAction = () => throw primary;

            var first = _service.OpenAsync<TestWindow>().AsTask();
            var second = _service.OpenAsync<TestWindow>().AsTask();
            operation.Complete();
            yield return WaitFor(first, second);
            var firstFailure = Assert.Throws<InvalidOperationException>(
                () => first.GetAwaiter().GetResult());
            var secondFailure = Assert.Throws<InvalidOperationException>(
                () => second.GetAwaiter().GetResult());
            Assert.That(firstFailure, Is.SameAs(primary));
            Assert.That(secondFailure, Is.SameAs(primary));
            Assert.That(TestWindow.TotalCloseCount, Is.EqualTo(1));
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            Assert.That(
                _resources.Diagnostics.OutstandingLeases,
                Is.Empty);

            TestWindow.OpenAction = null;
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("retry"));
            var retry = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(retry);
            Assert.That(retry.IsCompletedSuccessfully, Is.True);
            Assert.That(retry.GetAwaiter().GetResult().IsValid, Is.True);
            Assert.That(_backend.InstantiateCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator MissingOrDuplicateWindowComponentReleasesLease()
        {
            _service.Register<TestWindow>(
                Descriptor(mode: UIWindowMode.MultipleInstances));
            var missing = new GameObject(
                "missing",
                typeof(RectTransform));
            _backend.EnqueuePrefab(missing);
            var missingOpen = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(missingOpen);
            var missingFailure = Assert.Throws<InvalidOperationException>(
                () => missingOpen.GetAwaiter().GetResult());
            Assert.That(missingFailure.Message, Does.Contain("window"));
            Assert.That(missingFailure.Message, Does.Contain("TestWindow"));

            var duplicate = CreatePrefab<TestWindow>("duplicate");
            var child = new GameObject(
                "duplicate-child",
                typeof(RectTransform),
                typeof(TestWindow));
            child.transform.SetParent(duplicate.transform, false);
            _backend.EnqueuePrefab(duplicate);
            var duplicateOpen = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(duplicateOpen);
            var duplicateFailure = Assert.Throws<InvalidOperationException>(
                () => duplicateOpen.GetAwaiter().GetResult());
            Assert.That(duplicateFailure.Message, Does.Contain("duplicated"));
            Assert.That(_backend.ReleaseCount, Is.EqualTo(2));
            Assert.That(
                _resources.Diagnostics.OutstandingLeases,
                Is.Empty);
        }

        [UnityTest]
        public IEnumerator DisposeFromWindowCallbackIsRejectedWithoutStoppingService()
        {
            _service.Register<TestWindow>(Descriptor());
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("reentry"));
            var rejected = false;
            TestWindow.OpenAction = () =>
            {
                try
                {
                    _service.DisposeAsync();
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
            };

            var open = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(open);

            Assert.That(rejected, Is.True);
            Assert.That(open.IsCompletedSuccessfully, Is.True);
            Assert.That(open.GetAwaiter().GetResult().IsValid, Is.True);
        }

        [UnityTest]
        public IEnumerator MultipleInstancesHaveDistinctIdsAndDestroyOwnership()
        {
            _service.Register<TestWindow>(
                Descriptor(mode: UIWindowMode.MultipleInstances));
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("first"));
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("second"));

            var first = _service.OpenAsync<TestWindow>().AsTask();
            var second = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(first, second);
            var firstHandle = first.GetAwaiter().GetResult();
            var secondHandle = second.GetAwaiter().GetResult();

            Assert.That(firstHandle.InstanceId, Is.Not.EqualTo(secondHandle.InstanceId));
            Assert.That(
                _resources.Diagnostics.OutstandingLeases.Count,
                Is.EqualTo(2));

            var closeFirst = _service.CloseAsync(firstHandle).AsTask();
            var closeSecond = _service.CloseAsync(secondHandle).AsTask();
            yield return WaitFor(closeFirst, closeSecond);
            Observe(closeFirst);
            Observe(closeSecond);
            Assert.That(_backend.ReleaseCount, Is.EqualTo(2));
            Assert.That(
                _resources.Diagnostics.OutstandingLeases.Count,
                Is.Zero);
            Assert.That(firstHandle.IsValid, Is.False);
            Assert.That(secondHandle.IsValid, Is.False);
        }

        [UnityTest]
        public IEnumerator CacheReturnsToPoolAndReopenReusesInstanceWithNewLifetime()
        {
            _service.Register<TestWindow>(
                Descriptor(cacheOnClose: true));
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("cached"));

            var first = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(first);
            var firstHandle = first.GetAwaiter().GetResult();
            Assert.That(_service.TryGetWindow(firstHandle, out var firstWindow), Is.True);
            var firstObject = firstWindow.gameObject;
            var firstLifetime = firstWindow.LifetimeToken;

            var close = _service.CloseAsync(firstHandle).AsTask();
            yield return WaitFor(close);
            Observe(close);
            Assert.That(firstLifetime.IsCancellationRequested, Is.True);
            Assert.That(firstObject.activeSelf, Is.False);
            Assert.That(firstHandle.IsValid, Is.False);
            Assert.That(_backend.ReleaseCount, Is.Zero);

            var second = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(second);
            var secondHandle = second.GetAwaiter().GetResult();
            Assert.That(_service.TryGetWindow(secondHandle, out var secondWindow), Is.True);
            Assert.That(secondWindow.gameObject, Is.SameAs(firstObject));
            Assert.That(secondWindow.LifetimeToken, Is.Not.EqualTo(firstLifetime));
            Assert.That(secondWindow.LifetimeToken.IsCancellationRequested, Is.False);
            Assert.That(secondWindow.gameObject.activeSelf, Is.True);
            Assert.That(secondHandle.InstanceId, Is.Not.EqualTo(firstHandle.InstanceId));
            Assert.That(_backend.InstantiateCount, Is.EqualTo(1));
            Assert.That(_service.Diagnostics.CachedCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator CachedDiagnosticsPrunePoolEvictionAndExternalConsumption()
        {
            var key = new ResourceKey("shared-cache");
            _service.Register<TestWindow>(
                Descriptor(
                    key: key.Value,
                    cacheOnClose: true));
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("cached-first"));
            var first = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(first);
            var firstClose = _service.CloseAsync(
                first.GetAwaiter().GetResult()).AsTask();
            yield return WaitFor(firstClose);
            Observe(firstClose);
            Assert.That(_service.Diagnostics.CachedCount, Is.EqualTo(1));

            _pool.Clear(key);
            Assert.That(_service.Diagnostics.CachedCount, Is.Zero);

            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("cached-second"));
            var second = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(second);
            var secondClose = _service.CloseAsync(
                second.GetAwaiter().GetResult()).AsTask();
            yield return WaitFor(secondClose);
            Observe(secondClose);
            Assert.That(_service.Diagnostics.CachedCount, Is.EqualTo(1));

            var externalRent = _pool.RentAsync(key).AsTask();
            yield return WaitFor(externalRent);
            var externalHandle = externalRent.GetAwaiter().GetResult();
            Assert.That(externalHandle.Instance.activeSelf, Is.True);
            Assert.That(_service.Diagnostics.CachedCount, Is.Zero);

            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("cached-third"));
            var third = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(third);
            Assert.That(_service.Diagnostics.OpenCount, Is.EqualTo(1));
            Assert.That(_service.Diagnostics.CachedCount, Is.Zero);
            externalHandle.Dispose();
        }

        [UnityTest]
        public IEnumerator BackPrioritizesPopupAndMaskClickUsesCanonicalClose()
        {
            _service.Register<TestWindow>(
                Descriptor(id: "normal", key: "normal"));
            _service.Register<OtherWindow>(
                Descriptor(
                    id: "popup",
                    key: "popup",
                    layer: UILayer.Popup,
                    requiresMask: true,
                    closeOnMaskClick: true,
                    blocksInput: true));
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("normal"));
            _backend.EnqueuePrefab(CreatePrefab<OtherWindow>("popup"));

            var normal = _service.OpenAsync<TestWindow>().AsTask();
            var popup = _service.OpenAsync<OtherWindow>().AsTask();
            yield return WaitFor(normal, popup);
            var normalHandle = normal.GetAwaiter().GetResult();
            var popupHandle = popup.GetAwaiter().GetResult();

            Assert.That(_root.Mask.gameObject.activeSelf, Is.True);
            Assert.That(_service.Diagnostics.MaskPopupInstanceId, Is.EqualTo(popupHandle.InstanceId));
            Assert.That(
                _root.Mask.transform.GetSiblingIndex(),
                Is.EqualTo(_service.GetWindow(popupHandle).transform.GetSiblingIndex() - 1));

            _root.Mask.onClick.Invoke();
            yield return WaitUntil(() => !popupHandle.IsValid);
            Assert.That(TestWindow.TotalCloseCount, Is.EqualTo(1));
            Assert.That(normalHandle.IsValid, Is.True);
            Assert.That(_root.Mask.gameObject.activeSelf, Is.False);

            var back = _service.BackAsync().AsTask();
            yield return WaitFor(back);
            Assert.That(back.GetAwaiter().GetResult(), Is.True);
            Assert.That(normalHandle.IsValid, Is.False);
            Assert.That(TestWindow.TotalCloseCount, Is.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator MaskIsSharedAndTracksTopmostMaskedPopup()
        {
            _service.Register<TestWindow>(
                Descriptor(
                    id: "lower",
                    key: "lower",
                    layer: UILayer.Popup,
                    mode: UIWindowMode.MultipleInstances,
                    requiresMask: true,
                    closeOnMaskClick: false,
                    blocksInput: false));
            _service.Register<OtherWindow>(
                Descriptor(
                    id: "upper",
                    key: "upper",
                    layer: UILayer.Popup,
                    requiresMask: true,
                    closeOnMaskClick: true,
                    blocksInput: true));
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("lower"));
            _backend.EnqueuePrefab(CreatePrefab<OtherWindow>("upper"));
            var lowerOpen = _service.OpenAsync<TestWindow>().AsTask();
            var upperOpen = _service.OpenAsync<OtherWindow>().AsTask();
            yield return WaitFor(lowerOpen, upperOpen);
            var lower = lowerOpen.GetAwaiter().GetResult();
            var upper = upperOpen.GetAwaiter().GetResult();

            Assert.That(
                _root.GetLayerRoot(UILayer.Popup)
                    .GetComponentsInChildren<Button>(true)
                    .Count(button => button == _root.Mask),
                Is.EqualTo(1));
            Assert.That(
                _service.Diagnostics.MaskPopupInstanceId,
                Is.EqualTo(upper.InstanceId));
            Assert.That(
                _root.Mask.GetComponent<Image>().raycastTarget,
                Is.True);

            var upperClose = _service.CloseAsync(upper).AsTask();
            yield return WaitFor(upperClose);
            Observe(upperClose);
            Assert.That(
                _service.Diagnostics.MaskPopupInstanceId,
                Is.EqualTo(lower.InstanceId));
            Assert.That(
                _root.Mask.GetComponent<Image>().raycastTarget,
                Is.False);
            Assert.That(
                _root.Mask.transform.GetSiblingIndex(),
                Is.EqualTo(
                    _service.GetWindow(lower)
                        .transform.GetSiblingIndex() - 1));
        }

        [UnityTest]
        public IEnumerator MaskDoesNotReorderUnmaskedPopupOpenedAboveIt()
        {
            _service.Register<TestWindow>(
                Descriptor(
                    id: "masked",
                    key: "masked",
                    layer: UILayer.Popup,
                    requiresMask: true,
                    blocksInput: true));
            _service.Register<OtherWindow>(
                Descriptor(
                    id: "unmasked",
                    key: "unmasked",
                    layer: UILayer.Popup));
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("masked"));
            _backend.EnqueuePrefab(CreatePrefab<OtherWindow>("unmasked"));
            var maskedOpen = _service.OpenAsync<TestWindow>().AsTask();
            var unmaskedOpen = _service.OpenAsync<OtherWindow>().AsTask();
            yield return WaitFor(maskedOpen, unmaskedOpen);
            var masked = maskedOpen.GetAwaiter().GetResult();
            var unmasked = unmaskedOpen.GetAwaiter().GetResult();

            var maskedIndex =
                _service.GetWindow(masked).transform.GetSiblingIndex();
            var unmaskedIndex =
                _service.GetWindow(unmasked).transform.GetSiblingIndex();
            Assert.That(
                _root.Mask.transform.GetSiblingIndex(),
                Is.EqualTo(maskedIndex - 1));
            Assert.That(maskedIndex, Is.LessThan(unmaskedIndex));
            CollectionAssert.AreEqual(
                new[] { masked.InstanceId, unmasked.InstanceId },
                _service.Diagnostics.PopupNavigation);
        }

        [Test]
        public void PreCanceledOpenDoesNotStartResourceOrPoolWork()
        {
            _service.Register<TestWindow>(Descriptor());
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(
                    () => _service.OpenAsync<TestWindow>(
                        token: cancellation.Token));
            }

            Assert.That(_backend.InstantiateCount, Is.Zero);
            Assert.That(_service.Diagnostics.OpeningCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator PublicMutationRejectsBackgroundThreadBeforeWorkStarts()
        {
            _service.Register<TestWindow>(Descriptor());
            var unityThreadField = typeof(UIRoot).GetField(
                "_unityThreadId",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(unityThreadField, Is.Not.Null);
            unityThreadField.SetValue(null, 0);
            Exception operationFailure = null;
            Exception rootFailure = null;
            Exception constructorFailure = null;
            var background = Task.Run(
                () =>
                {
                    try
                    {
                        _service.OpenAsync<TestWindow>();
                    }
                    catch (Exception exception)
                    {
                        operationFailure = exception;
                    }

                    try
                    {
                        UIRoot.Create(dontDestroyOnLoad: false);
                    }
                    catch (Exception exception)
                    {
                        rootFailure = exception;
                    }

                    try
                    {
                        _ = new UIService(
                            _resources,
                            _pool,
                            _events,
                            _root);
                    }
                    catch (Exception exception)
                    {
                        constructorFailure = exception;
                    }
                });
            yield return WaitFor(background);
            Observe(background);

            Assert.That(
                operationFailure,
                Is.TypeOf<InvalidOperationException>());
            Assert.That(
                operationFailure.Message,
                Does.Contain("main thread"));
            Assert.That(rootFailure, Is.TypeOf<InvalidOperationException>());
            Assert.That(rootFailure.Message, Does.Contain("main thread"));
            Assert.That(
                constructorFailure,
                Is.TypeOf<InvalidOperationException>());
            Assert.That(
                constructorFailure.Message,
                Does.Contain("main thread"));
            Assert.That(_backend.InstantiateCount, Is.Zero);
            Assert.That(_service.Diagnostics.OpeningCount, Is.Zero);
            Assert.That(
                (int)unityThreadField.GetValue(null),
                Is.EqualTo(0),
                "A first background caller must not claim Unity ownership.");
            var restored = UIRoot.Create(dontDestroyOnLoad: false);
            var liveRoots = Resources.FindObjectsOfTypeAll<UIRoot>()
                .Where(
                    candidate =>
                        candidate != null &&
                        (candidate.gameObject.scene.IsValid() ||
                         (candidate.gameObject.hideFlags &
                          HideFlags.HideAndDontSave) != 0))
                .ToArray();
            TestContext.WriteLine(
                string.Join(
                    Environment.NewLine,
                    liveRoots.Select(
                        candidate =>
                            "UIRoot id=" + candidate.GetInstanceID() +
                            ", sceneValid=" +
                            candidate.gameObject.scene.IsValid() +
                            ", hideFlags=" +
                            candidate.gameObject.hideFlags)));
            Assert.That(
                restored.GetInstanceID(),
                Is.EqualTo(_root.GetInstanceID()));
            Assert.That(
                liveRoots.Select(candidate => candidate.GetInstanceID())
                    .Distinct()
                    .Count(),
                Is.EqualTo(1));
        }

        [Test]
        public void RootRecoveryRebuildsRuntimeStateAndRejectsAmbiguousCandidates()
        {
            var instanceField = typeof(UIRoot).GetField(
                "_instance",
                BindingFlags.NonPublic | BindingFlags.Static);
            var layerRootsField = typeof(UIRoot).GetField(
                "_layerRoots",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var layersField = typeof(UIRoot).GetField(
                "_layers",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var stagingProperty = typeof(UIRoot).GetProperty(
                "StagingRoot",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(instanceField, Is.Not.Null);
            Assert.That(layerRootsField, Is.Not.Null);
            Assert.That(layersField, Is.Not.Null);
            Assert.That(stagingProperty, Is.Not.Null);

            var originalLayerIds = _root.Layers
                .Select(layer => layer.Root.GetInstanceID())
                .ToArray();
            ((System.Collections.IDictionary)layerRootsField.GetValue(_root))
                .Clear();
            layersField.SetValue(_root, null);
            instanceField.SetValue(null, null);

            var recovered = UIRoot.Create(dontDestroyOnLoad: false);
            Assert.That(
                recovered.GetInstanceID(),
                Is.EqualTo(_root.GetInstanceID()));
            CollectionAssert.AreEqual(
                originalLayerIds,
                recovered.Layers.Select(
                    layer => layer.Root.GetInstanceID()));
            var staging =
                (RectTransform)stagingProperty.GetValue(recovered);
            Assert.That(staging, Is.Not.Null);
            Assert.That(
                staging.gameObject.activeSelf,
                Is.False);

            var rogueObject = new GameObject(
                "Uninitialized.UIRoot",
                typeof(RectTransform),
                typeof(UIRoot));
            rogueObject.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                instanceField.SetValue(null, null);
                var failure = Assert.Throws<InvalidOperationException>(
                    () => UIRoot.Create(dontDestroyOnLoad: false));
                Assert.That(
                    failure.Message,
                    Does.Contain("candidate"));
            }
            finally
            {
                Object.DestroyImmediate(rogueObject);
            }

            instanceField.SetValue(null, null);
            Assert.That(
                UIRoot.Create(dontDestroyOnLoad: false).GetInstanceID(),
                Is.EqualTo(_root.GetInstanceID()));
        }

        [UnityTest]
        public IEnumerator CallerCancellationOnlyCancelsWaitAndStopCleansOrphan()
        {
            _service.Register<TestWindow>(
                Descriptor(mode: UIWindowMode.MultipleInstances));
            var operation = _backend.EnqueueGatedPrefab(
                CreatePrefab<TestWindow>("orphan"));
            using (var cancellation = new CancellationTokenSource())
            {
                var caller = _service.OpenAsync<TestWindow>(
                    token: cancellation.Token).AsTask();
                cancellation.Cancel();
                yield return WaitFor(caller);
                Assert.That(caller.IsCanceled, Is.True);
            }

            operation.Complete();
            yield return WaitUntil(() => _service.Diagnostics.OpenCount == 1);
            Assert.That(TestWindow.OpenCount, Is.EqualTo(1));

            var stop = _service.StopAsync().AsTask();
            yield return WaitFor(stop);
            Observe(stop);
            Assert.That(_service.Diagnostics.OpenCount, Is.Zero);
            Assert.That(
                _resources.Diagnostics.OutstandingLeases.Count,
                Is.Zero);
        }

        [UnityTest]
        public IEnumerator LifetimeSubscriptionAndConcurrentCloseAreExactOnce()
        {
            _service.Register<TestWindow>(Descriptor());
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("events"));
            TestWindow.SubscribeDuringOpen = true;

            var open = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(open);
            var handle = open.GetAwaiter().GetResult();
            _events.Publish(1);
            Assert.That(TestWindow.EventCount, Is.EqualTo(1));

            var first = _service.CloseAsync(handle).AsTask();
            var second = _service.CloseAsync(handle).AsTask();
            yield return WaitFor(first, second);
            Observe(first);
            Observe(second);
            _events.Publish(2);

            Assert.That(TestWindow.EventCount, Is.EqualTo(1));
            Assert.That(TestWindow.TotalCloseCount, Is.EqualTo(1));
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator SubscriptionDisposeFailureDoesNotSkipCloseOrRelease()
        {
            _service.Register<TestWindow>(Descriptor());
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("scope-failure"));
            TestWindow.SubscribeDuringOpen = true;
            _events.ThrowOnDispose = true;
            var open = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(open);
            var handle = open.GetAwaiter().GetResult();

            var close = _service.CloseAsync(handle).AsTask();
            yield return WaitFor(close);
            var failure = Assert.Throws<InvalidOperationException>(
                () => close.GetAwaiter().GetResult());

            Assert.That(failure.Message, Does.Contain("subscription"));
            Assert.That(TestWindow.TotalCloseCount, Is.EqualTo(1));
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            Assert.That(
                _resources.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(_service.Diagnostics.RecentException, Is.SameAs(failure));
        }

        [UnityTest]
        public IEnumerator CloseCallerCancellationDoesNotSkipMandatoryCleanup()
        {
            _service.Register<TestWindow>(Descriptor());
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("close-gate"));
            TestWindow.CloseGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var open = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(open);
            var handle = open.GetAwaiter().GetResult();

            using (var cancellation = new CancellationTokenSource())
            {
                var close = _service.CloseAsync(
                    handle,
                    cancellation.Token).AsTask();
                cancellation.Cancel();
                yield return WaitFor(close);
                Assert.That(close.IsCanceled, Is.True);
            }

            Assert.That(handle.IsValid, Is.False);
            Assert.That(
                _resources.Diagnostics.OutstandingLeases.Count,
                Is.EqualTo(1));
            TestWindow.CloseGate.TrySetResult(true);
            yield return WaitUntil(
                () =>
                    _resources.Diagnostics.OutstandingLeases.Count == 0);
            Assert.That(TestWindow.TotalCloseCount, Is.EqualTo(1));
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator CallerCancellationObservesLaterCanonicalFailure()
        {
            _service.Register<TestWindow>(Descriptor());
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("abandoned-fault"));
            var open = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(open);
            var handle = open.GetAwaiter().GetResult();
            TestWindow.CloseGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            TestWindow.CloseFailure = new InvalidOperationException(
                "close failed after caller abandoned");

            var unobserved = false;
            EventHandler<UnobservedTaskExceptionEventArgs> handler =
                (_, arguments) =>
                {
                    if (arguments.Exception.ToString().Contains(
                            "close failed after caller abandoned"))
                    {
                        unobserved = true;
                        arguments.SetObserved();
                    }
                };
            TaskScheduler.UnobservedTaskException += handler;
            try
            {
                using (var cancellation = new CancellationTokenSource())
                {
                    Task caller = _service.CloseAsync(
                        handle,
                        cancellation.Token).AsTask();
                    cancellation.Cancel();
                    yield return WaitFor(caller);
                    Assert.That(caller.IsCanceled, Is.True);
                    caller = null;
                }

                TestWindow.CloseGate.TrySetResult(true);
                yield return WaitUntil(
                    () =>
                        ReferenceEquals(
                            _service.Diagnostics.RecentException,
                            TestWindow.CloseFailure));
                for (var cycle = 0; cycle < 5; cycle++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    yield return null;
                }

                Assert.That(
                    unobserved,
                    Is.False,
                    "The abandoned canonical close task fault was unobserved.");
            }
            finally
            {
                TaskScheduler.UnobservedTaskException -= handler;
            }
        }

        [UnityTest]
        public IEnumerator PreCanceledCloseAndStopStayCanceledWhenCleanupIsSynchronous()
        {
            _service.Register<TestWindow>(Descriptor());
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("pre-canceled"));
            var open = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(open);
            var handle = open.GetAwaiter().GetResult();

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var close = _service.CloseAsync(
                    handle,
                    cancellation.Token).AsTask();
                yield return WaitFor(close);
                Assert.That(close.IsCanceled, Is.True);
                Assert.That(_backend.ReleaseCount, Is.EqualTo(1));

                var staleClose = _service.CloseAsync(
                    handle,
                    cancellation.Token).AsTask();
                yield return WaitFor(staleClose);
                Assert.That(staleClose.IsCanceled, Is.True);

                var stop = _service.StopAsync(
                    cancellation.Token).AsTask();
                yield return WaitFor(stop);
                Assert.That(stop.IsCanceled, Is.True);
            }

            var canonicalStop = _service.StopAsync().AsTask();
            yield return WaitFor(canonicalStop);
            Observe(canonicalStop);
            _service = null;
        }

        [UnityTest]
        public IEnumerator StopCancelsOpeningAndWaitsForClosingWindow()
        {
            _service.Register<TestWindow>(
                Descriptor(mode: UIWindowMode.MultipleInstances));
            var gatedOperation = _backend.EnqueueGatedPrefab(
                CreatePrefab<TestWindow>("opening"));
            var opening = _service.OpenAsync<TestWindow>().AsTask();

            var stop = _service.StopAsync().AsTask();
            Assert.That(stop.IsCompleted, Is.False);
            Assert.That(opening.IsCompleted, Is.False);
            for (var frame = 0; frame < 3; frame++)
            {
                yield return null;
            }

            Assert.That(stop.IsCompleted, Is.False);
            Assert.That(opening.IsCompleted, Is.False);
            Assert.That(
                _resources.Diagnostics.InflightOperationCount,
                Is.EqualTo(1));

            gatedOperation.Complete();
            yield return WaitFor(opening, stop);
            var openingReportedCancellation = opening.IsCanceled;
            var canceledException = Assert.Throws<TaskCanceledException>(
                () => opening.GetAwaiter().GetResult());
            Assert.That(
                canceledException.CancellationToken.IsCancellationRequested,
                Is.True);
            Observe(stop);
            Assert.That(
                _resources.Diagnostics.InflightOperationCount,
                Is.Zero);
            Assert.That(
                _resources.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            Assert.That(openingReportedCancellation, Is.True);
        }

        [UnityTest]
        public IEnumerator StopCancellationCallbackFailureStillCompletesCleanup()
        {
            _service.Register<TestWindow>(Descriptor());
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("cancel-failure"));
            TestWindow.ThrowOnLifetimeCancellation = true;
            var open = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(open);

            var stop = _service.StopAsync().AsTask();
            yield return WaitFor(stop);
            var failure = Assert.Catch<Exception>(
                () => stop.GetAwaiter().GetResult());

            Assert.That(
                failure.ToString(),
                Does.Contain("lifetime cancellation failed"));
            Assert.That(TestWindow.TotalCloseCount, Is.EqualTo(1));
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            Assert.That(
                _resources.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(_service.Diagnostics.OpenCount, Is.Zero);
            _service = null;
        }

        [UnityTest]
        public IEnumerator CloseDuringOpeningCapturesCancellationCallbackFailure()
        {
            _service.Register<TestWindow>(Descriptor());
            TestWindow.ThrowOnLifetimeCancellation = true;
            TestWindow.OpenGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = _backend.EnqueueGatedPrefab(
                CreatePrefab<TestWindow>("opening-close"));
            var opening = _service.OpenAsync<TestWindow>().AsTask();
            operation.Complete();
            yield return WaitUntil(
                () => _service.Diagnostics.OpeningCount == 1);

            var instanceId =
                _service.Diagnostics.Windows.Single().InstanceId;
            var entriesField = typeof(UIService).GetField(
                "_entries",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(entriesField, Is.Not.Null);
            var entries =
                (System.Collections.IDictionary)entriesField.GetValue(
                    _service);
            var entry = entries[instanceId];
            Assert.That(entry, Is.Not.Null);
            var handleProperty = entry.GetType().GetProperty("Handle");
            Assert.That(handleProperty, Is.Not.Null);
            yield return WaitUntil(
                () => handleProperty.GetValue(entry) != null);
            var handle =
                (WindowHandle)handleProperty.GetValue(entry);

            Task close = null;
            Assert.DoesNotThrow(
                () => close = _service.CloseAsync(handle).AsTask());
            var stop = _service.StopAsync().AsTask();
            Assert.That(close.IsCompleted, Is.False);
            Assert.That(stop.IsCompleted, Is.False);
            TestWindow.OpenGate.TrySetResult(true);
            yield return WaitFor(opening, close, stop);

            Assert.That(opening.IsCanceled, Is.True);
            var failure = Assert.Catch<Exception>(
                () => close.GetAwaiter().GetResult());
            var stopFailure = Assert.Catch<Exception>(
                () => stop.GetAwaiter().GetResult());
            Assert.That(
                failure.ToString(),
                Does.Contain("lifetime cancellation failed"));
            Assert.That(
                stopFailure.ToString(),
                Does.Contain("lifetime cancellation failed"));
            Assert.That(
                _service.Diagnostics.RecentException.ToString(),
                Does.Contain("lifetime cancellation failed"));
            _service = null;
        }

        [UnityTest]
        public IEnumerator PreCanceledStopAndConcurrentDisposeShareCleanup()
        {
            _service.Register<TestWindow>(Descriptor());
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("stop-close"));
            var open = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(open);
            var handle = open.GetAwaiter().GetResult();
            TestWindow.CloseGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var close = _service.CloseAsync(handle).AsTask();

            Task canceledStop;
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                canceledStop =
                    _service.StopAsync(cancellation.Token).AsTask();
                yield return WaitFor(canceledStop);
                Assert.That(canceledStop.IsCanceled, Is.True);
            }

            var stop = _service.StopAsync().AsTask();
            var dispose = _service.DisposeAsync().AsTask();
            Assert.That(stop.IsCompleted, Is.False);
            Assert.That(dispose.IsCompleted, Is.False);
            TestWindow.CloseGate.TrySetResult(true);
            yield return WaitFor(close, stop, dispose);
            Observe(close);
            Observe(stop);
            Observe(dispose);

            Assert.That(TestWindow.TotalCloseCount, Is.EqualTo(1));
            Assert.That(_backend.ReleaseCount, Is.EqualTo(1));
            Assert.That(
                _resources.Diagnostics.OutstandingLeases,
                Is.Empty);
            Assert.That(_service.Diagnostics.OpenCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DetachedCallbackContextExpiresAfterCallbackReturns()
        {
            _service.Register<TestWindow>(Descriptor());
            _backend.EnqueuePrefab(CreatePrefab<TestWindow>("detached"));
            var gate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Task detached = null;
            var rejected = false;
            TestWindow.OpenAction = () =>
            {
                detached = ContinueAfterCallbackAsync();
            };

            var open = _service.OpenAsync<TestWindow>().AsTask();
            yield return WaitFor(open);
            Assert.That(open.IsCompletedSuccessfully, Is.True);
            gate.TrySetResult(true);
            yield return WaitFor(detached);
            Observe(detached);

            Assert.That(rejected, Is.False);
            Assert.That(_service.Diagnostics.OpenCount, Is.Zero);

            async Task ContinueAfterCallbackAsync()
            {
                await gate.Task;
                try
                {
                    await _service.StopAsync();
                }
                catch (InvalidOperationException)
                {
                    rejected = true;
                }
            }
        }

        [UnityTest]
        public IEnumerator ModuleUnloadThenInstallRecreatesScopeOwnedServiceAndRoot()
        {
            var localStop = _service.StopAsync().AsTask();
            yield return WaitFor(localStop);
            Observe(localStop);
            _service = null;
            yield return null;

            var runtime = new FrameworkRuntime();
            var descriptors = new[]
            {
                new ModuleDescriptor(
                    "Resource",
                    Array.Empty<string>(),
                    0,
                    () => new ResourceModule()),
                new ModuleDescriptor(
                    "EventBus",
                    Array.Empty<string>(),
                    1,
                    () => new EventBusModule()),
                new ModuleDescriptor(
                    "Pool",
                    new[] { "Resource" },
                    2,
                    () => new PoolModule()),
                new ModuleDescriptor(
                    "UI",
                    new[] { "Resource", "Pool", "EventBus" },
                    3,
                    () => new UIModule())
            };
            var start = runtime.StartAsync(
                descriptors,
                CancellationToken.None).AsTask();
            yield return WaitFor(start);
            Observe(start);
            var original = runtime.Services.Resolve<IUIService>();

            var unload = runtime.UnloadAsync(
                "UI",
                ModuleUnloadMode.RequireNoDependents,
                CancellationToken.None).AsTask();
            yield return WaitFor(unload);
            var unloadResult = unload.GetAwaiter().GetResult();
            var install = runtime.InstallAsync(
                new ModuleDescriptor(
                    "UI",
                    new[] { "Resource", "Pool", "EventBus" },
                    3,
                    () => new UIModule()),
                CancellationToken.None).AsTask();
            yield return WaitFor(install);
            Observe(install);
            var replacement = runtime.Services.Resolve<IUIService>();

            CollectionAssert.AreEqual(
                new[] { "UI" },
                unloadResult.UnloadedModuleIds);
            Assert.That(replacement, Is.Not.SameAs(original));
            Assert.That(original.Diagnostics.OpenCount, Is.Zero);
            Assert.That(
                Object.FindObjectsOfType<UIRoot>().Length,
                Is.EqualTo(1));
            yield return null;
            var replacementRoot = Object.FindObjectOfType<UIRoot>();
            Assert.That(replacementRoot, Is.Not.Null);
            Assert.That(replacementRoot.EventSystem, Is.Not.Null);
            Assert.That(
                replacementRoot.EventSystem.gameObject.activeSelf,
                Is.True);
            Assert.That(
                Object.FindObjectsOfType<EventSystem>().Length,
                Is.EqualTo(1));

            var runtimeStop = runtime.StopAsync(
                CancellationToken.None).AsTask();
            yield return WaitFor(runtimeStop);
            Observe(runtimeStop);
            var runtimeDispose = runtime.DisposeAsync().AsTask();
            yield return WaitFor(runtimeDispose);
            Observe(runtimeDispose);
        }

        [Test]
        public void ModuleDeclaresExactDependencies()
        {
            var module = new UIModule();
            Assert.That(module.Id, Is.EqualTo("UI"));
            CollectionAssert.AreEqual(
                new[] { "Resource", "Pool", "EventBus" },
                module.Dependencies);
        }

        private static UIWindowDescriptor Descriptor(
            string id = "window",
            string key = "window",
            UILayer layer = UILayer.Normal,
            UIWindowMode mode = UIWindowMode.SingleInstance,
            bool cacheOnClose = false,
            bool requiresMask = false,
            bool closeOnMaskClick = false,
            bool blocksInput = false,
            bool? allowBack = null)
        {
            return new UIWindowDescriptor(
                id,
                new ResourceKey(key),
                layer,
                mode,
                cacheOnClose,
                requiresMask,
                closeOnMaskClick,
                blocksInput,
                allowBack);
        }

        private static GameObject CreatePrefab<T>(string name)
            where T : UIWindow
        {
            var prefab = new GameObject(
                name,
                typeof(RectTransform),
                typeof(T));
            prefab.SetActive(false);
            return prefab;
        }

        private static IEnumerator WaitFor(params Task[] tasks)
        {
            var timeout = Time.realtimeSinceStartup + 5f;
            while (tasks.Any(task => !task.IsCompleted))
            {
                if (Time.realtimeSinceStartup >= timeout)
                {
                    Assert.Fail("Timed out waiting for UI task.");
                }

                yield return null;
            }
        }

        private static IEnumerator WaitUntil(Func<bool> condition)
        {
            var timeout = Time.realtimeSinceStartup + 5f;
            while (!condition())
            {
                if (Time.realtimeSinceStartup >= timeout)
                {
                    Assert.Fail("Timed out waiting for UI state.");
                }

                yield return null;
            }
        }

        private static void Observe(Task task)
        {
            if (task.IsFaulted)
            {
                task.GetAwaiter().GetResult();
            }
        }

        private static HashSet<int> SnapshotRuntimeIds<T>()
            where T : Component
        {
            var ids = new HashSet<int>();
            foreach (var component in Resources.FindObjectsOfTypeAll<T>())
            {
                if (IsRuntimeTestObject(component))
                {
                    ids.Add(component.GetInstanceID());
                }
            }

            return ids;
        }

        private static void DestroyCreated<T>(ISet<int> protectedIds)
            where T : Component
        {
            foreach (var component in Resources.FindObjectsOfTypeAll<T>())
            {
                if (IsRuntimeTestObject(component) &&
                    (protectedIds == null ||
                     !protectedIds.Contains(component.GetInstanceID())))
                {
                    Object.DestroyImmediate(component.gameObject);
                }
            }
        }

        private static bool IsRuntimeTestObject(Component component)
        {
            return component != null &&
                   (component.gameObject.scene.IsValid() ||
                    (component.gameObject.hideFlags &
                     HideFlags.HideAndDontSave) != 0);
        }

        private sealed class TestWindow : UIWindow
        {
            public static int OpenCount;
            public static int TotalCloseCount;
            public static int EventCount;
            public static bool SubscribeDuringOpen;
            public static bool EnabledBeforeLifetime;
            public static Action OpenAction;
            public static TaskCompletionSource<bool> OpenGate;
            public static TaskCompletionSource<bool> CloseGate;
            public static Exception CloseFailure;
            public static bool ThrowOnLifetimeCancellation;
            private CancellationTokenRegistration _lifetimeRegistration;

            private void OnEnable()
            {
                if (!LifetimeToken.CanBeCanceled)
                {
                    EnabledBeforeLifetime = true;
                }
            }

            protected override ValueTask OnOpenAsync(
                object parameter,
                CancellationToken token)
            {
                OpenCount++;
                OpenAction?.Invoke();
                if (ThrowOnLifetimeCancellation)
                {
                    _lifetimeRegistration = LifetimeToken.Register(
                        () => throw new InvalidOperationException(
                            "lifetime cancellation failed"));
                }

                if (SubscribeDuringOpen)
                {
                    Subscribe<int>(_ => EventCount++);
                }

                return OpenGate == null
                    ? default
                    : new ValueTask(OpenGate.Task);
            }

            protected override ValueTask OnCloseAsync(
                CancellationToken token)
            {
                TotalCloseCount++;
                _lifetimeRegistration.Dispose();
                if (CloseGate == null)
                {
                    if (CloseFailure != null)
                    {
                        throw CloseFailure;
                    }

                    return default;
                }

                return new ValueTask(CompleteCloseAsync());
            }

            private static async Task CompleteCloseAsync()
            {
                await CloseGate.Task;
                if (CloseFailure != null)
                {
                    throw CloseFailure;
                }
            }

            public static void ResetCounters()
            {
                OpenCount = 0;
                TotalCloseCount = 0;
                EventCount = 0;
                SubscribeDuringOpen = false;
                EnabledBeforeLifetime = false;
                OpenAction = null;
                OpenGate = null;
                CloseGate = null;
                CloseFailure = null;
                ThrowOnLifetimeCancellation = false;
            }
        }

        private sealed class OtherWindow : UIWindow
        {
            protected override ValueTask OnCloseAsync(
                CancellationToken token)
            {
                TestWindow.TotalCloseCount++;
                return default;
            }
        }

        private sealed class RecordingEventBus : IEventBus
        {
            private readonly Dictionary<Type, List<Action<object>>> _handlers =
                new Dictionary<Type, List<Action<object>>>();

            public EventBusDiagnostics Diagnostics => null;

            public bool ThrowOnDispose { get; set; }

            public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
            {
                if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
                {
                    handlers = new List<Action<object>>();
                    _handlers.Add(typeof(TEvent), handlers);
                }

                Action<object> boxed = value => handler((TEvent)value);
                handlers.Add(boxed);
                return new ActionDisposable(
                    () =>
                    {
                        handlers.Remove(boxed);
                        if (ThrowOnDispose)
                        {
                            throw new InvalidOperationException(
                                "subscription dispose failed");
                        }
                    });
            }

            public IDisposable Subscribe<TEvent>(
                ModuleScope ownerScope,
                Action<TEvent> handler)
            {
                return ownerScope.Own(Subscribe(handler));
            }

            public void Publish<TEvent>(TEvent value)
            {
                if (_handlers.TryGetValue(typeof(TEvent), out var handlers))
                {
                    foreach (var handler in handlers.ToArray())
                    {
                        handler(value);
                    }
                }
            }

            public void Enqueue<TEvent>(TEvent value)
            {
                Publish(value);
            }
        }

        private sealed class RecordingGameObjectPool : IGameObjectPool
        {
            public RecordingGameObjectPool(GameObject instance)
            {
                Handle = new RecordingPooledHandle(instance);
            }

            public RecordingPooledHandle Handle { get; }

            public int RentCount { get; private set; }

            public IReadOnlyDictionary<ResourceKey, PoolDiagnostics> Diagnostics =>
                new Dictionary<ResourceKey, PoolDiagnostics>();

            public ValueTask<IPooledGameObjectHandle> RentAsync(
                ResourceKey key,
                Transform parent = null,
                CancellationToken token = default)
            {
                RentCount++;
                return new ValueTask<IPooledGameObjectHandle>(Handle);
            }

            public ValueTask<IPooledGameObjectHandle> RentAsync(
                ResourceKey key,
                Transform parent,
                Vector3 position,
                Quaternion rotation,
                CancellationToken token = default)
            {
                return RentAsync(key, parent, token);
            }

            public void Return(IPooledGameObjectHandle handle)
            {
                handle?.Dispose();
            }

            public void Clear(ResourceKey key)
            {
            }

            public void ClearAll()
            {
            }
        }

        private sealed class RecordingPooledHandle : IPooledGameObjectHandle
        {
            public RecordingPooledHandle(GameObject instance)
            {
                Instance = instance;
            }

            public GameObject Instance { get; }

            public int DisposeCount { get; private set; }

            public void Dispose()
            {
                DisposeCount++;
            }
        }

        private sealed class AlternativeWindowHandle : IWindowHandle
        {
            public string DescriptorId => "alternative";
            public string WindowId => DescriptorId;
            public Guid InstanceId { get; } = Guid.NewGuid();
            public Type WindowType => typeof(TestWindow);
            public bool IsValid => true;
        }

        private sealed class ActionDisposable : IDisposable
        {
            private Action _dispose;

            public ActionDisposable(Action dispose)
            {
                _dispose = dispose;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _dispose, null)?.Invoke();
            }
        }

        private sealed class FakeResourceBackend : IResourceBackend
        {
            private readonly Queue<PrefabOperation> _prefabs =
                new Queue<PrefabOperation>();

            public int InstantiateCount { get; private set; }

            public int ReleaseCount { get; private set; }

            public List<Transform> RequestedParents { get; } =
                new List<Transform>();

            public PrefabOperation EnqueuePrefab(GameObject instance)
            {
                var operation = new PrefabOperation(instance, gated: false);
                _prefabs.Enqueue(operation);
                return operation;
            }

            public PrefabOperation EnqueueGatedPrefab(GameObject instance)
            {
                var operation = new PrefabOperation(instance, gated: true);
                _prefabs.Enqueue(operation);
                return operation;
            }

            public IResourceOperation<GameObject> InstantiateAsync(
                ResourceKey key,
                Transform parent)
            {
                InstantiateCount++;
                RequestedParents.Add(parent);
                if (_prefabs.Count == 0)
                {
                    throw new InvalidOperationException(
                        "No prefab operation was queued for " + key.Value + ".");
                }

                var operation = _prefabs.Dequeue();
                operation.SetRelease(
                    () =>
                    {
                        ReleaseCount++;
                        if (operation.Instance != null)
                        {
                            Object.DestroyImmediate(operation.Instance);
                        }
                    });
                return operation;
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
        }

        private sealed class PrefabOperation :
            IResourceOperation<GameObject>
        {
            private readonly TaskCompletionSource<GameObject> _completion;
            private Action _release;

            public PrefabOperation(GameObject instance, bool gated)
            {
                Instance = instance;
                _completion = new TaskCompletionSource<GameObject>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!gated)
                {
                    _completion.SetResult(instance);
                }
            }

            public GameObject Instance { get; }

            public Task<GameObject> Task => _completion.Task;

            public void Complete()
            {
                _completion.TrySetResult(Instance);
            }

            public void SetRelease(Action release)
            {
                _release = release;
            }

            public void Release()
            {
                Interlocked.Exchange(ref _release, null)?.Invoke();
            }
        }
    }
}
