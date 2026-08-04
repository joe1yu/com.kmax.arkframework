using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
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
        private readonly Dictionary<string, RectTransform> _namedRoots =
            new Dictionary<string, RectTransform>(StringComparer.Ordinal);
        private IReadOnlyList<LayerRoot> _layers;
        [SerializeField]
        private bool _initialized;
        [SerializeField]
        private RectTransform _stagingRoot;
        [SerializeField]
        private RectTransform[] _serializedLayerRoots =
            new RectTransform[LayerOrder.Length];
        [SerializeField]
        private string[] _serializedRootIds = Array.Empty<string>();
        [SerializeField]
        private RectTransform[] _serializedRoots =
            Array.Empty<RectTransform>();
        [SerializeField]
        private Button _mask;
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

        public Button Mask => _mask;

        internal int OwnerThreadId { get; private set; }

        internal RectTransform StagingRoot => _stagingRoot;

        public static UIRoot Create(bool dontDestroyOnLoad = true)
        {
            return CreateInternal(
                dontDestroyOnLoad,
                null,
                null);
        }

        public static UIRoot Create(IPlatformService platform)
        {
            if (platform == null)
            {
                throw new ArgumentNullException(nameof(platform));
            }

            if (platform.UIRoots.Count == 0)
            {
                throw new InvalidOperationException(
                    "The platform prefab does not define any UI roots.");
            }

            return CreateInternal(
                false,
                platform.Root.transform,
                platform.UIRoots);
        }

        private static UIRoot CreateInternal(
            bool dontDestroyOnLoad,
            Transform owner,
            IReadOnlyList<PlatformUIRoot> platformRoots)
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
            if (owner != null)
            {
                rootObject.transform.SetParent(owner, false);
            }

            rootObject.hideFlags = dontDestroyOnLoad
                ? HideFlags.None
                : HideFlags.HideAndDontSave;
            var root = rootObject.GetComponent<UIRoot>();
            root.OwnerThreadId = currentThreadId;
            try
            {
                if (platformRoots == null)
                {
                    root.Build(dontDestroyOnLoad);
                }
                else
                {
                    root.BindPlatformRoots(platformRoots);
                }
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

        public RectTransform GetRoot(string id)
        {
            EnsureOwnerThread();
            if (string.IsNullOrWhiteSpace(id) ||
                !_namedRoots.TryGetValue(id.Trim(), out var root))
            {
                throw new KeyNotFoundException(
                    "UI root '" + id + "' was not found.");
            }

            return root;
        }

        internal RectTransform GetWindowRoot(
            UIWindowDescriptor descriptor)
        {
            return GetRoot(descriptor.RootId);
        }

        internal void PlaceMask(RectTransform parent)
        {
            EnsureOwnerThread();
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            var transform = (RectTransform)_mask.transform;
            if (transform.parent != parent)
            {
                transform.SetParent(parent, false);
                Stretch(transform);
            }
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

            BuildStagingRoot();

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
                _namedRoots.Add(layer.ToString(), transform);
                _serializedLayerRoots[index] = transform;
                layerValues[index] = new LayerRoot(
                    layer,
                    transform,
                    SortingOrders[index]);
            }

            _layers = new ReadOnlyCollection<LayerRoot>(layerValues);
            SerializeNamedRoots();
            BuildMask();
            _initialized = true;
        }

        private void BindPlatformRoots(
            IReadOnlyList<PlatformUIRoot> platformRoots)
        {
            BuildStagingRoot();
            for (var index = 0; index < platformRoots.Count; index++)
            {
                var platformRoot = platformRoots[index];
                if (platformRoot == null ||
                    platformRoot.RectTransform == null ||
                    string.IsNullOrWhiteSpace(platformRoot.Id))
                {
                    throw new InvalidOperationException(
                        "The platform contains an invalid UI root.");
                }

                var id = platformRoot.Id.Trim();
                if (_namedRoots.ContainsKey(id))
                {
                    throw new InvalidOperationException(
                        "UI root ID '" + id + "' is duplicated.");
                }

                _namedRoots.Add(id, platformRoot.RectTransform);
            }

            RestoreLayerMappings();
            SerializeNamedRoots();
            BuildMask();
            _initialized = true;
        }

        private void BuildStagingRoot()
        {
            var stagingObject = new GameObject(
                "ArkFramework.UIStaging",
                typeof(RectTransform));
            _stagingRoot = stagingObject.GetComponent<RectTransform>();
            _stagingRoot.SetParent(base.transform, false);
            Stretch(_stagingRoot);
            stagingObject.SetActive(false);
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
            transform.SetParent(base.transform, false);
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

            if (_serializedRootIds == null ||
                _serializedRoots == null ||
                _serializedRootIds.Length != _serializedRoots.Length ||
                _serializedRootIds.Length == 0)
            {
                throw new InvalidOperationException(
                    "The runtime UIRoot candidate has invalid root references.");
            }

            _namedRoots.Clear();
            for (var index = 0; index < _serializedRootIds.Length; index++)
            {
                var id = _serializedRootIds[index];
                var transform = _serializedRoots[index];
                if (string.IsNullOrWhiteSpace(id) || transform == null ||
                    _namedRoots.ContainsKey(id))
                {
                    throw new InvalidOperationException(
                        "The runtime UIRoot candidate has an invalid named root.");
                }

                _namedRoots.Add(id, transform);
            }

            RestoreLayerMappings();
            if (_mask == null)
            {
                throw new InvalidOperationException(
                    "The runtime UIRoot candidate has an invalid popup mask.");
            }

            OwnerThreadId = ownerThreadId;
        }

        private void RestoreLayerMappings()
        {
            _layerRoots.Clear();
            var layerValues = new List<LayerRoot>(LayerOrder.Length);
            for (var index = 0; index < LayerOrder.Length; index++)
            {
                var layer = LayerOrder[index];
                // 未显式填写 RootId 的窗口按 UILayer 名称查找兼容根节点。
                if (!_namedRoots.TryGetValue(
                        layer.ToString(),
                        out var transform))
                {
                    _serializedLayerRoots[index] = null;
                    continue;
                }

                _layerRoots.Add(layer, transform);
                _serializedLayerRoots[index] = transform;
                var canvas = transform.GetComponentInParent<Canvas>();
                layerValues.Add(new LayerRoot(
                    layer,
                    transform,
                    canvas == null
                        ? SortingOrders[index]
                        : canvas.sortingOrder));
            }

            _layers = new ReadOnlyCollection<LayerRoot>(
                layerValues.ToArray());
        }

        private void SerializeNamedRoots()
        {
            _serializedRootIds = new string[_namedRoots.Count];
            _serializedRoots = new RectTransform[_namedRoots.Count];
            var index = 0;
            foreach (var pair in _namedRoots)
            {
                _serializedRootIds[index] = pair.Key;
                _serializedRoots[index] = pair.Value;
                index++;
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
}
