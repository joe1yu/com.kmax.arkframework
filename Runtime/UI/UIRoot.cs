using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    [ExecuteAlways]
    public sealed class UIRoot :
        MonoBehaviour,
        IDisposable,
        IAsyncDisposable
    {
        private static readonly UILayer[] LayerOrder =
        {
            UILayer.Background,
            UILayer.Normal,
            UILayer.Popup,
            UILayer.Overlay,
            UILayer.System
        };

        private static readonly int[] SortingOrders =
        {
            0,
            100,
            200,
            300,
            400
        };
        private static int _unityThreadId;
        private static UIRoot _instance;

        static UIRoot()
        {
            FrameworkStaticReset.Register(ResetStatics);
        }

        private readonly Dictionary<UILayer, RectTransform> _layerRoots =
            new Dictionary<UILayer, RectTransform>();
        private IReadOnlyList<LayerRoot> _layers;
        [SerializeField]
        private bool _initialized;
        [SerializeField]
        private RectTransform _stagingRoot;
        [SerializeField]
        private RectTransform[] _serializedLayerRoots =
            new RectTransform[LayerOrder.Length];
        [SerializeField]
        private Button _mask;
        [SerializeField]
        private EventSystem _eventSystem;
        [SerializeField]
        private bool _ownsEventSystem;
        private bool _disposed;
        private readonly TaskCompletionSource<bool> _destroyCompletion =
            new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<LayerRoot> Layers
        {
            get
            {
                EnsureOwnerThread();
                return _layers;
            }
        }

        public EventSystem EventSystem
        {
            get
            {
                EnsureOwnerThread();
                return _eventSystem;
            }
        }

        public Button Mask => _mask;

        internal int OwnerThreadId { get; private set; }

        internal RectTransform StagingRoot => _stagingRoot;

        public static UIRoot Create(bool dontDestroyOnLoad = true)
        {
            var currentThreadId = ValidateUnityMainThread();
            var candidates = new List<UIRoot>();
            foreach (var candidate in Resources.FindObjectsOfTypeAll<UIRoot>())
            {
                if (candidate != null &&
                    !candidate._disposed &&
                    IsRuntimeRoot(candidate))
                {
                    candidates.Add(candidate);
                }
            }

            if (candidates.Count > 1)
            {
                throw new InvalidOperationException(
                    "Multiple runtime UIRoot candidates were found.");
            }

            if (candidates.Count == 1)
            {
                var candidate = candidates[0];
                candidate.RestoreRuntimeState(currentThreadId);
                _instance = candidate;
                return candidate;
            }

            if (_instance != null && !_instance._disposed)
            {
                throw new InvalidOperationException(
                    "The registered UIRoot is not a valid runtime candidate.");
            }

            var rootObject = new GameObject(
                "ArkFramework.UIRoot",
                typeof(RectTransform),
                typeof(UIRoot));
            rootObject.hideFlags = dontDestroyOnLoad
                ? HideFlags.None
                : HideFlags.HideAndDontSave;
            var root = rootObject.GetComponent<UIRoot>();
            root.OwnerThreadId = currentThreadId;
            try
            {
                root.Build(dontDestroyOnLoad);
            }
            catch
            {
                root.BeginDispose();
                throw;
            }

            _instance = root;
            return root;
        }

        private static void ResetStatics()
        {
            _instance = null;
            _unityThreadId = 0;
        }

        public RectTransform GetLayerRoot(UILayer layer)
        {
            EnsureOwnerThread();
            if (!_layerRoots.TryGetValue(layer, out var root))
            {
                throw new ArgumentOutOfRangeException(nameof(layer));
            }

            return root;
        }

        public void Dispose()
        {
            BeginDispose();
        }

        public ValueTask DisposeAsync()
        {
            BeginDispose();
            return new ValueTask(_destroyCompletion.Task);
        }

        private void BeginDispose()
        {
            EnsureOwnerThread();
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _initialized = false;
            DisposeOwnedEventSystem();
            if (this == null)
            {
                _destroyCompletion.TrySetResult(true);
                return;
            }

            if (gameObject != null)
            {
                DestroyOwnedObject(gameObject);
            }

            if (!Application.isPlaying)
            {
                _destroyCompletion.TrySetResult(true);
            }
        }

        private void OnDestroy()
        {
            _disposed = true;
            _initialized = false;
            DisposeOwnedEventSystem();
            if (_instance == this)
            {
                _instance = null;
            }

            _destroyCompletion.TrySetResult(true);
        }

        private void Build(bool dontDestroyOnLoad)
        {
            if (Application.isPlaying && dontDestroyOnLoad)
            {
                Object.DontDestroyOnLoad(gameObject);
            }

            var stagingObject = new GameObject(
                "ArkFramework.UIStaging",
                typeof(RectTransform));
            _stagingRoot = stagingObject.GetComponent<RectTransform>();
            _stagingRoot.SetParent(base.transform, false);
            Stretch(_stagingRoot);
            stagingObject.SetActive(false);

            var layerValues = new LayerRoot[LayerOrder.Length];
            for (var index = 0; index < LayerOrder.Length; index++)
            {
                var layer = LayerOrder[index];
                var layerObject = new GameObject(
                    layer + "Canvas",
                    typeof(RectTransform),
                    typeof(Canvas),
                    typeof(CanvasScaler),
                    typeof(GraphicRaycaster));
                var transform = layerObject.GetComponent<RectTransform>();
                transform.SetParent(base.transform, false);
                Stretch(transform);
                var canvas = layerObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true;
                canvas.sortingOrder = SortingOrders[index];
                var scaler = layerObject.GetComponent<CanvasScaler>();
                scaler.uiScaleMode =
                    CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                _layerRoots.Add(layer, transform);
                _serializedLayerRoots[index] = transform;
                layerValues[index] = new LayerRoot(
                    layer,
                    transform,
                    SortingOrders[index]);
            }

            _layers = new ReadOnlyCollection<LayerRoot>(layerValues);
            BuildMask();
            EnsureEventSystem(dontDestroyOnLoad);
            _initialized = true;
        }

        private void BuildMask()
        {
            var maskObject = new GameObject(
                "ArkFramework.PopupMask",
                typeof(RectTransform),
                typeof(CanvasRenderer),
                typeof(Image),
                typeof(Button));
            var transform = maskObject.GetComponent<RectTransform>();
            transform.SetParent(GetLayerRoot(UILayer.Popup), false);
            Stretch(transform);
            var image = maskObject.GetComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.55f);
            image.raycastTarget = false;
            _mask = maskObject.GetComponent<Button>();
            _mask.transition = Selectable.Transition.None;
            maskObject.SetActive(false);
        }

        private void RestoreRuntimeState(int ownerThreadId)
        {
            if (!_initialized)
            {
                throw new InvalidOperationException(
                    "The runtime UIRoot candidate is not initialized.");
            }

            if (_stagingRoot == null ||
                _stagingRoot.parent != base.transform ||
                _stagingRoot.gameObject.activeSelf)
            {
                throw new InvalidOperationException(
                    "The runtime UIRoot candidate has an invalid staging root.");
            }

            if (_serializedLayerRoots == null ||
                _serializedLayerRoots.Length != LayerOrder.Length)
            {
                throw new InvalidOperationException(
                    "The runtime UIRoot candidate has invalid layer references.");
            }

            _layerRoots.Clear();
            var layerValues = new LayerRoot[LayerOrder.Length];
            for (var index = 0; index < LayerOrder.Length; index++)
            {
                var transform = _serializedLayerRoots[index];
                if (transform == null ||
                    transform.parent != base.transform)
                {
                    throw new InvalidOperationException(
                        "The runtime UIRoot candidate has an invalid layer root.");
                }

                var canvas = transform.GetComponent<Canvas>();
                if (canvas == null ||
                    transform.GetComponent<CanvasScaler>() == null ||
                    transform.GetComponent<GraphicRaycaster>() == null ||
                    canvas.renderMode != RenderMode.ScreenSpaceOverlay ||
                    canvas.sortingOrder != SortingOrders[index])
                {
                    throw new InvalidOperationException(
                        "The runtime UIRoot candidate has an invalid canvas layer " +
                        index + " (canvas=" + (canvas != null) +
                        ", scaler=" +
                        (transform.GetComponent<CanvasScaler>() != null) +
                        ", raycaster=" +
                        (transform.GetComponent<GraphicRaycaster>() != null) +
                        ", renderMode=" +
                        (canvas == null
                            ? "missing"
                            : canvas.renderMode.ToString()) +
                        ", sortingOrder=" +
                        (canvas == null ? -1 : canvas.sortingOrder) + ").");
                }

                var layer = LayerOrder[index];
                _layerRoots.Add(layer, transform);
                layerValues[index] = new LayerRoot(
                    layer,
                    transform,
                    SortingOrders[index]);
            }

            if (_mask == null ||
                _mask.transform.parent !=
                _serializedLayerRoots[(int)UILayer.Popup])
            {
                throw new InvalidOperationException(
                    "The runtime UIRoot candidate has an invalid popup mask.");
            }

            if (_eventSystem == null ||
                (_ownsEventSystem &&
                 _eventSystem.GetComponent<UIEventSystemOwnership>() == null))
            {
                throw new InvalidOperationException(
                    "The runtime UIRoot candidate has an invalid EventSystem.");
            }

            OwnerThreadId = ownerThreadId;
            _layers = new ReadOnlyCollection<LayerRoot>(layerValues);
        }

        private void EnsureEventSystem(bool dontDestroyOnLoad)
        {
            var eventSystems =
                Object.FindObjectsOfType<EventSystem>();
            for (var index = 0; index < eventSystems.Length; index++)
            {
                var candidate = eventSystems[index];
                var ownership =
                    candidate.GetComponent<UIEventSystemOwnership>();
                if (ownership == null || !ownership.IsDisposing)
                {
                    _eventSystem = candidate;
                    return;
                }
            }

            var eventObject = new GameObject(
                "ArkFramework.EventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule),
                typeof(UIEventSystemOwnership));
            _eventSystem = eventObject.GetComponent<EventSystem>();
            _ownsEventSystem = true;
            if (Application.isPlaying && dontDestroyOnLoad)
            {
                Object.DontDestroyOnLoad(eventObject);
            }
            else if (!dontDestroyOnLoad)
            {
                eventObject.hideFlags = HideFlags.HideAndDontSave;
            }
        }

        private static void Stretch(RectTransform transform)
        {
            transform.anchorMin = Vector2.zero;
            transform.anchorMax = Vector2.one;
            transform.offsetMin = Vector2.zero;
            transform.offsetMax = Vector2.zero;
            transform.localScale = Vector3.one;
        }

        private static void DestroyOwnedObject(Object value)
        {
            if (value == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(value);
            }
            else
            {
                Object.DestroyImmediate(value);
            }
        }

        private static bool IsRuntimeRoot(UIRoot candidate)
        {
            return candidate.gameObject.scene.IsValid() ||
                   (candidate.gameObject.hideFlags &
                    HideFlags.HideAndDontSave) != 0;
        }

        private void DisposeOwnedEventSystem()
        {
            if (_ownsEventSystem && _eventSystem != null)
            {
                var ownership =
                    _eventSystem.GetComponent<UIEventSystemOwnership>();
                if (ownership != null)
                {
                    ownership.IsDisposing = true;
                }

                _eventSystem.gameObject.SetActive(false);
                DestroyOwnedObject(_eventSystem.gameObject);
            }

            _eventSystem = null;
            _ownsEventSystem = false;
        }

        private void EnsureOwnerThread()
        {
            if (OwnerThreadId != 0 &&
                Thread.CurrentThread.ManagedThreadId != OwnerThreadId)
            {
                throw new InvalidOperationException(
                    "UIRoot operations must run on the Unity main thread.");
            }
        }

        private static int ValidateUnityMainThread()
        {
            var currentThreadId = Thread.CurrentThread.ManagedThreadId;
            var knownThreadId = Volatile.Read(ref _unityThreadId);
            if (knownThreadId == 0)
            {
                var context = SynchronizationContext.Current;
                if (context == null ||
                    !string.Equals(
                        context.GetType().FullName,
                        "UnityEngine.UnitySynchronizationContext",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "UIRoot must be created on the Unity main thread.");
                }

                Interlocked.CompareExchange(
                    ref _unityThreadId,
                    currentThreadId,
                    0);
                knownThreadId = Volatile.Read(ref _unityThreadId);
            }

            if (currentThreadId != knownThreadId)
            {
                throw new InvalidOperationException(
                    "UIRoot must be created on the Unity main thread.");
            }

            return currentThreadId;
        }

        public sealed class LayerRoot
        {
            internal LayerRoot(
                UILayer layer,
                RectTransform root,
                int sortingOrder)
            {
                Layer = layer;
                Root = root;
                SortingOrder = sortingOrder;
            }

            public UILayer Layer { get; }

            public RectTransform Root { get; }

            public int SortingOrder { get; }
        }
    }

    internal sealed class UIEventSystemOwnership : MonoBehaviour
    {
        public bool IsDisposing { get; set; }
    }
}
