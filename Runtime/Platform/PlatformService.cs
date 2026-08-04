using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public sealed class PlatformService : IPlatformService
    {
        private static readonly IReadOnlyList<PlatformUIRoot> EmptyUIRoots =
            Array.AsReadOnly(Array.Empty<PlatformUIRoot>());
        private static readonly IReadOnlyList<Canvas> EmptyCanvases =
            Array.AsReadOnly(Array.Empty<Canvas>());

        private readonly Dictionary<string, RectTransform> _uiRoots =
            new Dictionary<string, RectTransform>(StringComparer.Ordinal);
        private IReadOnlyList<PlatformUIRoot> _uiRootValues = EmptyUIRoots;
        private IReadOnlyList<Canvas> _canvasValues = EmptyCanvases;
        private GameObject _root;
        private bool _disposed;

        public PlatformService(
            GameObject platformPrefab,
            bool dontDestroyOnLoad = true)
        {
            _root = CreateRoot(platformPrefab, dontDestroyOnLoad);
            try
            {
                CollectUIRoots();
                ConfigurePrefabCanvases();
            }
            catch
            {
                DestroyRoot();
                throw;
            }
        }

        public GameObject Root
        {
            get
            {
                EnsureActive();
                return _root;
            }
        }

        public IReadOnlyList<PlatformUIRoot> UIRoots
        {
            get
            {
                EnsureActive();
                return _uiRootValues;
            }
        }

        [Obsolete(
            "仅用于兼容旧版已导入 Sample；平台模块不管理 EventSystem。")]
        public EventSystem EventSystem
        {
            get
            {
                EnsureActive();
                // 只返回预制体已有组件，不创建、不复用也不校验。
                return _root.GetComponentInChildren<EventSystem>(true);
            }
        }

        [Obsolete(
            "仅用于兼容旧版已导入 Sample；请通过平台预制体自行管理 Canvas。")]
        public IReadOnlyList<Canvas> Canvases
        {
            get
            {
                EnsureActive();
                return _canvasValues;
            }
        }

        public bool TryGetUIRoot(string id, out RectTransform root)
        {
            EnsureActive();
            if (string.IsNullOrWhiteSpace(id))
            {
                root = null;
                return false;
            }

            return _uiRoots.TryGetValue(id.Trim(), out root);
        }

        public RectTransform GetUIRoot(string id)
        {
            EnsureActive();
            if (!TryGetUIRoot(id, out var root))
            {
                throw new KeyNotFoundException(
                    "Platform UI root '" + id + "' was not found.");
            }

            return root;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return default;
            }

            _disposed = true;
            _uiRoots.Clear();
            _uiRootValues = EmptyUIRoots;
            _canvasValues = EmptyCanvases;
            DestroyRoot();
            return default;
        }

        private static GameObject CreateRoot(
            GameObject platformPrefab,
            bool dontDestroyOnLoad)
        {
            var root = platformPrefab == null
                ? new GameObject("ArkFramework.Platform")
                : Object.Instantiate(platformPrefab);
            if (platformPrefab != null)
            {
                root.name = platformPrefab.name;
            }

            if (Application.isPlaying && dontDestroyOnLoad)
            {
                Object.DontDestroyOnLoad(root);
            }

            return root;
        }

        private void CollectUIRoots()
        {
            var values = _root.GetComponentsInChildren<PlatformUIRoot>(true);
            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                if (string.IsNullOrWhiteSpace(value.Id))
                {
                    throw new InvalidOperationException(
                        "Platform UI roots must define a non-empty ID.");
                }

                if (value.RectTransform == null)
                {
                    throw new InvalidOperationException(
                        "Platform UI root '" + value.Id +
                        "' must use a RectTransform.");
                }

                var id = value.Id.Trim();
                if (_uiRoots.ContainsKey(id))
                {
                    throw new InvalidOperationException(
                        "Platform UI root ID '" + id +
                        "' is duplicated.");
                }

                _uiRoots.Add(id, value.RectTransform);
            }

            _uiRootValues = new ReadOnlyCollection<PlatformUIRoot>(values);
        }

        private void ConfigurePrefabCanvases()
        {
            var configurators = _root.GetComponentsInChildren<
                PlatformGraphicRaycasterConfigurator>(true);
            var canvases = _root.GetComponentsInChildren<Canvas>(true);
            _canvasValues = new ReadOnlyCollection<Canvas>(canvases);
            for (var index = 0; index < configurators.Length; index++)
            {
                var configurator = configurators[index];
                if (configurator == null)
                {
                    continue;
                }

                var raycasterType = configurator.GetValidatedRaycasterType();
                for (var canvasIndex = 0;
                     canvasIndex < canvases.Length;
                     canvasIndex++)
                {
                    configurator.EnsureConfigured(
                        canvases[canvasIndex],
                        raycasterType);
                }
            }
        }

        private void DestroyRoot()
        {
            if (_root == null)
            {
                return;
            }

            _root.SetActive(false);
            if (Application.isPlaying)
            {
                Object.Destroy(_root);
            }
            else
            {
                Object.DestroyImmediate(_root);
            }

            _root = null;
        }

        private void EnsureActive()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PlatformService));
            }
        }
    }
}
