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
        private static readonly IReadOnlyList<Canvas> EmptyCanvases =
            Array.AsReadOnly(Array.Empty<Canvas>());

        private readonly GameObject _sourcePrefab;
        private readonly List<PlatformGraphicRaycasterConfigurator>
            _configurators;
        private readonly Dictionary<int, HashSet<int>>
            _configuredCanvasIds = new Dictionary<int, HashSet<int>>();
        private IReadOnlyList<Canvas> _canvases = EmptyCanvases;
        private GameObject _root;
        private EventSystem _eventSystem;
        private bool _disposed;

        public PlatformService(
            GameObject platformPrefab,
            bool dontDestroyOnLoad = true)
        {
            _sourcePrefab = platformPrefab;
            _root = CreateRoot(platformPrefab, dontDestroyOnLoad);
            try
            {
                _eventSystem = ResolveEventSystem();
                _configurators = new List<
                    PlatformGraphicRaycasterConfigurator>(
                    _root.GetComponentsInChildren<
                        PlatformGraphicRaycasterConfigurator>(true));
                ValidateConfigurators();
                RefreshCanvases();
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

        public EventSystem EventSystem
        {
            get
            {
                EnsureActive();
                return _eventSystem;
            }
        }

        public IReadOnlyList<Canvas> Canvases
        {
            get
            {
                EnsureActive();
                return _canvases;
            }
        }

        public void RefreshCanvases()
        {
            EnsureActive();
            var canvases = FindRuntimeCanvases();
            var activeCanvasIds = new HashSet<int>();
            for (var index = 0; index < canvases.Count; index++)
            {
                activeCanvasIds.Add(canvases[index].GetInstanceID());
            }

            for (var index = 0; index < _configurators.Count; index++)
            {
                var configurator = _configurators[index];
                if (configurator == null)
                {
                    continue;
                }

                var configuratorId = configurator.GetInstanceID();
                if (!_configuredCanvasIds.TryGetValue(
                        configuratorId,
                        out var configuredIds))
                {
                    configuredIds = new HashSet<int>();
                    _configuredCanvasIds.Add(
                        configuratorId,
                        configuredIds);
                }

                configuredIds.RemoveWhere(
                    canvasId => !activeCanvasIds.Contains(canvasId));
                var raycasterType =
                    configurator.GetValidatedRaycasterType();
                for (var canvasIndex = 0;
                     canvasIndex < canvases.Count;
                     canvasIndex++)
                {
                    var canvas = canvases[canvasIndex];
                    var canvasId = canvas.GetInstanceID();
                    if (!configurator.AppliesTo(canvas))
                    {
                        configuredIds.Remove(canvasId);
                        continue;
                    }

                    if (configuredIds.Contains(canvasId) &&
                        canvas.GetComponent(raycasterType) != null)
                    {
                        continue;
                    }

                    configurator.EnsureConfigured(
                        canvas,
                        raycasterType);
                    configuredIds.Add(canvasId);
                }
            }

            _canvases = new ReadOnlyCollection<Canvas>(
                canvases.ToArray());
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return default;
            }

            _disposed = true;
            _configuredCanvasIds.Clear();
            _canvases = EmptyCanvases;
            _eventSystem = null;
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

        private EventSystem ResolveEventSystem()
        {
            var rootSystems =
                _root.GetComponentsInChildren<EventSystem>(true);
            if (rootSystems.Length > 1)
            {
                throw new InvalidOperationException(
                    "The platform prefab contains multiple EventSystems.");
            }

            var externalSystems = FindExternalEventSystems();
            if (rootSystems.Length == 1)
            {
                if (externalSystems.Count != 0)
                {
                    throw new InvalidOperationException(
                        "The platform prefab defines an EventSystem while " +
                        "another runtime EventSystem already exists.");
                }

                return rootSystems[0];
            }

            if (externalSystems.Count > 1)
            {
                throw new InvalidOperationException(
                    "Multiple runtime EventSystems exist before platform " +
                    "initialization.");
            }

            if (externalSystems.Count == 1)
            {
                return externalSystems[0];
            }

            var eventObject = new GameObject(
                "ArkFramework.PlatformEventSystem",
                typeof(EventSystem),
                typeof(StandaloneInputModule));
            eventObject.transform.SetParent(_root.transform, false);
            return eventObject.GetComponent<EventSystem>();
        }

        private List<EventSystem> FindExternalEventSystems()
        {
            var result = new List<EventSystem>();
            var candidates = Resources.FindObjectsOfTypeAll<EventSystem>();
            for (var index = 0; index < candidates.Length; index++)
            {
                var candidate = candidates[index];
                if (candidate == null ||
                    IsUnder(candidate.transform, _root.transform) ||
                    IsUnderSourcePrefab(candidate.transform) ||
                    !IsRuntimeObject(candidate.gameObject))
                {
                    continue;
                }

                result.Add(candidate);
            }

            return result;
        }

        private List<Canvas> FindRuntimeCanvases()
        {
            var result = new List<Canvas>();
            var candidates = Resources.FindObjectsOfTypeAll<Canvas>();
            for (var index = 0; index < candidates.Length; index++)
            {
                var candidate = candidates[index];
                if (candidate == null ||
                    IsUnderSourcePrefab(candidate.transform) ||
                    !IsRuntimeObject(candidate.gameObject))
                {
                    continue;
                }

                result.Add(candidate);
            }

            result.Sort(
                (left, right) =>
                    left.GetInstanceID().CompareTo(right.GetInstanceID()));
            return result;
        }

        private void ValidateConfigurators()
        {
            for (var index = 0; index < _configurators.Count; index++)
            {
                var configurator = _configurators[index];
                if (configurator != null)
                {
                    configurator.GetValidatedRaycasterType();
                }
            }
        }

        private bool IsUnderSourcePrefab(Transform candidate)
        {
            return _sourcePrefab != null &&
                   IsUnder(candidate, _sourcePrefab.transform);
        }

        private static bool IsUnder(Transform candidate, Transform root)
        {
            return candidate != null &&
                   root != null &&
                   (candidate == root || candidate.IsChildOf(root));
        }

        private static bool IsRuntimeObject(GameObject value)
        {
            if (value.scene.IsValid())
            {
                return true;
            }

            // HideAndDontSave 通常只设置在根节点，子 Canvas 不会继承 hideFlags。
            for (var current = value.transform;
                 current != null;
                 current = current.parent)
            {
                if ((current.gameObject.hideFlags &
                     HideFlags.HideAndDontSave) != 0)
                {
                    return true;
                }
            }

            return false;
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
