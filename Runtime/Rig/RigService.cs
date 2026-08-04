using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArkFramework
{
    public sealed class RigService : IRigService, IDisposable
    {
        private readonly IPlatformService _platform;
        private readonly Dictionary<string, RigState> _states =
            new Dictionary<string, RigState>(StringComparer.Ordinal);
        private readonly List<IRigComponentSynchronizer> _synchronizers =
            new List<IRigComponentSynchronizer>();
        private IReadOnlyList<CameraRig> _rigs;
        private RigState _active;
        private bool _disposed;

        internal RigService(IPlatformService platform)
        {
            _platform = platform ??
                throw new ArgumentNullException(nameof(platform));
            CollectRigs();
        }

        public IReadOnlyList<CameraRig> Rigs
        {
            get
            {
                EnsureActive();
                return _rigs;
            }
        }

        public CameraRig ActiveRig
        {
            get
            {
                EnsureActive();
                return _active.Rig;
            }
        }

        public string ActiveRigId
        {
            get
            {
                EnsureActive();
                return _active.Id;
            }
        }

        public RigSyncResult LastSyncResult { get; private set; } =
            RigSyncResult.Empty;

        public bool TryGetRig(string id, out CameraRig rig)
        {
            EnsureActive();
            if (!string.IsNullOrWhiteSpace(id) &&
                _states.TryGetValue(id.Trim(), out var state))
            {
                rig = state.Rig;
                return true;
            }

            rig = null;
            return false;
        }

        public CameraRig GetRig(string id)
        {
            EnsureActive();
            if (!TryGetRig(id, out var rig))
            {
                throw new KeyNotFoundException(
                    "Camera rig '" + id + "' was not found.");
            }

            return rig;
        }

        public void ActivateRig(string id)
        {
            EnsureActive();
            if (string.IsNullOrWhiteSpace(id) ||
                !_states.TryGetValue(id.Trim(), out var selected))
            {
                throw new KeyNotFoundException(
                    "Camera rig '" + id + "' was not found.");
            }

            foreach (var state in _states.Values)
            {
                state.Rig.gameObject.SetActive(
                    ReferenceEquals(state, selected));
            }

            _active = selected;
        }

        public void RegisterComponentSynchronizer(
            IRigComponentSynchronizer synchronizer)
        {
            EnsureActive();
            if (synchronizer == null)
            {
                throw new ArgumentNullException(nameof(synchronizer));
            }

            if (!_synchronizers.Contains(synchronizer))
            {
                _synchronizers.Add(synchronizer);
            }
        }

        public RigSyncResult SynchronizeActiveScene(
            SceneCameraSyncOptions options)
        {
            EnsureActive();
            var scene = SceneManager.GetActiveScene();
            var errors = new List<string>();
            var state = ResolveTargetRig(options.RigId, errors);
            if (state == null)
            {
                return SetResult(
                    scene.name,
                    options.RigId,
                    0,
                    0,
                    0,
                    0,
                    0,
                    errors);
            }

            var matches = CollectMatches(scene, state, errors);
            var poseCount = SynchronizePose(
                state,
                matches,
                options.Flags,
                errors);
            var cameraCount = SynchronizeCameras(
                matches,
                options.Flags,
                errors);
            var componentCount = SynchronizeComponents(
                matches,
                options,
                errors);
            var disabledCount = options.DisableSceneCameras
                ? DisableSceneCameras(matches)
                : 0;
            return SetResult(
                scene.name,
                state.Id,
                matches.Count,
                poseCount,
                cameraCount,
                componentCount,
                disabledCount,
                errors);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _states.Clear();
            _synchronizers.Clear();
            _rigs = Array.AsReadOnly(Array.Empty<CameraRig>());
            _active = null;
        }

        internal void HandleSceneTransition(SceneTransitionEvent transition)
        {
            if (_disposed ||
                transition.Stage != SceneTransitionStage.Completed ||
                !transition.CameraSync.Enabled)
            {
                return;
            }

            try
            {
                SynchronizeActiveScene(transition.CameraSync);
            }
            catch (Exception exception)
            {
                LastSyncResult = new RigSyncResult(
                    SceneManager.GetActiveScene().name,
                    transition.CameraSync.RigId,
                    0,
                    0,
                    0,
                    0,
                    0,
                    new[] { exception.Message });
                Debug.LogException(exception);
            }
        }

        private void CollectRigs()
        {
            var values = _platform.Root
                .GetComponentsInChildren<CameraRig>(true);
            if (values.Length == 0)
            {
                throw new InvalidOperationException(
                    "The platform prefab must contain at least one CameraRig.");
            }

            RigState defaultState = null;
            for (var index = 0; index < values.Length; index++)
            {
                var rig = values[index];
                ValidateRigHierarchy(rig);
                var id = RequireId(rig.Id, "Camera rig");
                if (_states.ContainsKey(id))
                {
                    throw new InvalidOperationException(
                        "Camera rig ID '" + id + "' is duplicated.");
                }

                var state = new RigState(rig, id);
                _states.Add(id, state);
                if (rig.ActiveByDefault)
                {
                    if (defaultState != null)
                    {
                        throw new InvalidOperationException(
                            "Only one camera rig can be active by default.");
                    }

                    defaultState = state;
                }
            }

            _rigs = new ReadOnlyCollection<CameraRig>(values);
            ActivateRig((defaultState ?? _states.Values.First()).Id);
        }

        private static void ValidateRigHierarchy(CameraRig rig)
        {
            var parent = rig.transform.parent;
            while (parent != null)
            {
                if (parent.GetComponent<CameraRig>() != null)
                {
                    throw new InvalidOperationException(
                        "Camera rigs cannot be nested because activation " +
                        "would become ambiguous.");
                }

                parent = parent.parent;
            }
        }

        private RigState ResolveTargetRig(
            string requestedId,
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(requestedId))
            {
                return _active;
            }

            var id = requestedId.Trim();
            if (!_states.TryGetValue(id, out var state))
            {
                errors.Add("Camera rig '" + id + "' was not found.");
                return null;
            }

            if (!ReferenceEquals(_active, state))
            {
                ActivateRig(id);
            }

            return state;
        }

        private List<CameraMatch> CollectMatches(
            Scene scene,
            RigState state,
            ICollection<string> errors)
        {
            var matches = new List<CameraMatch>();
            var matchedSlots = new HashSet<string>(StringComparer.Ordinal);
            var cameras = CollectSceneCameras(scene)
                .Where(camera =>
                    !camera.transform.IsChildOf(_platform.Root.transform))
                .ToList();
            for (var index = 0; index < cameras.Count; index++)
            {
                var source = cameras[index];
                var binding = source.GetComponent<SceneCameraBinding>();
                if (binding != null &&
                    !string.IsNullOrWhiteSpace(binding.RigId) &&
                    !string.Equals(
                        binding.RigId.Trim(),
                        state.Id,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                var slotId = binding == null ||
                    string.IsNullOrWhiteSpace(binding.SlotId)
                    ? source.name
                    : binding.SlotId.Trim();
                if (!state.Slots.TryGetValue(slotId, out var target))
                {
                    continue;
                }

                if (!matchedSlots.Add(slotId))
                {
                    errors.Add(
                        "Scene camera slot '" + slotId +
                        "' is mapped more than once.");
                    continue;
                }

                matches.Add(new CameraMatch(source, target, binding));
            }

            // 单相机场景和单槽位 Rig 无需额外挂映射组件。
            if (matches.Count == 0 &&
                cameras.Count == 1 &&
                state.Slots.Count == 1 &&
                IsBindingForRig(
                    cameras[0].GetComponent<SceneCameraBinding>(),
                    state.Id))
            {
                matches.Add(new CameraMatch(
                    cameras[0],
                    state.Slots.Values.First(),
                    cameras[0].GetComponent<SceneCameraBinding>()));
            }

            return matches;
        }

        private static bool IsBindingForRig(
            SceneCameraBinding binding,
            string rigId)
        {
            return binding == null ||
                string.IsNullOrWhiteSpace(binding.RigId) ||
                string.Equals(
                    binding.RigId.Trim(),
                    rigId,
                    StringComparison.Ordinal);
        }

        private static List<Camera> CollectSceneCameras(Scene scene)
        {
            var result = new List<Camera>();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return result;
            }

            var roots = scene.GetRootGameObjects();
            for (var index = 0; index < roots.Length; index++)
            {
                result.AddRange(
                    roots[index].GetComponentsInChildren<Camera>(true));
            }

            return result;
        }

        private static int SynchronizePose(
            RigState state,
            IReadOnlyList<CameraMatch> matches,
            SceneCameraSyncFlags flags,
            ICollection<string> errors)
        {
            if ((flags & SceneCameraSyncFlags.RigPose) == 0 ||
                matches.Count == 0)
            {
                return 0;
            }

            var explicitSources = matches
                .Where(match => match.Binding != null &&
                    match.Binding.PoseSource)
                .ToArray();
            if (explicitSources.Length > 1)
            {
                errors.Add(
                    "More than one scene camera is marked as the rig pose " +
                    "source.");
                return 0;
            }

            var sourceMatch = explicitSources.Length == 1
                ? explicitSources[0].Source.transform
                : matches[0].Source.transform;
            var selectedMatch = explicitSources.Length == 1
                ? explicitSources[0]
                : matches[0];
            var targetCamera = selectedMatch.Target.Camera.transform;
            var targetRoot = state.Rig.PoseRoot;
            if (targetCamera != targetRoot &&
                !targetCamera.IsChildOf(targetRoot))
            {
                errors.Add(
                    "Rig pose root must be an ancestor of the pose source " +
                    "camera slot.");
                return 0;
            }

            // 对 PoseRoot 应用刚体变换，使目标槽位相机而不只是根节点精确对齐场景相机。
            var rotationDelta = sourceMatch.rotation *
                Quaternion.Inverse(targetCamera.rotation);
            var rootPosition = sourceMatch.position +
                rotationDelta * (targetRoot.position - targetCamera.position);
            var rootRotation = rotationDelta * targetRoot.rotation;
            targetRoot.SetPositionAndRotation(rootPosition, rootRotation);
            return 1;
        }

        private static int SynchronizeCameras(
            IReadOnlyList<CameraMatch> matches,
            SceneCameraSyncFlags flags,
            ICollection<string> errors)
        {
            if ((flags & SceneCameraSyncFlags.CameraSettings) == 0)
            {
                return 0;
            }

            var count = 0;
            for (var index = 0; index < matches.Count; index++)
            {
                var match = matches[index];
                try
                {
                    match.Target.Camera.CopyFrom(match.Source);
                    count++;
                }
                catch (Exception exception)
                {
                    errors.Add(
                        "Failed to copy Camera for slot '" +
                        match.Target.Id + "': " + exception.Message);
                }
            }

            return count;
        }

        private int SynchronizeComponents(
            IReadOnlyList<CameraMatch> matches,
            SceneCameraSyncOptions options,
            ICollection<string> errors)
        {
            if ((options.Flags & SceneCameraSyncFlags.Components) == 0)
            {
                return 0;
            }

            var componentTypes = ResolveComponentTypes(
                options.ComponentTypeNames,
                errors);
            var count = 0;
            for (var matchIndex = 0;
                matchIndex < matches.Count;
                matchIndex++)
            {
                var match = matches[matchIndex];
                for (var typeIndex = 0;
                    typeIndex < componentTypes.Count;
                    typeIndex++)
                {
                    var type = componentTypes[typeIndex];
                    var source = match.Source.GetComponent(type);
                    if (source == null)
                    {
                        continue;
                    }

                    try
                    {
                        var target = match.Target.Camera.GetComponent(type) ??
                            match.Target.Camera.gameObject.AddComponent(type);
                        SynchronizeComponent(type, source, target);
                        count++;
                    }
                    catch (Exception exception)
                    {
                        errors.Add(
                            "Failed to copy component '" + type.FullName +
                            "' for slot '" + match.Target.Id + "': " +
                            exception.Message);
                    }
                }
            }

            return count;
        }

        private void SynchronizeComponent(
            Type type,
            Component source,
            Component target)
        {
            for (var index = _synchronizers.Count - 1;
                index >= 0;
                index--)
            {
                var synchronizer = _synchronizers[index];
                if (synchronizer.CanSynchronize(type))
                {
                    synchronizer.Synchronize(source, target);
                    return;
                }
            }

            // Unity 序列化能覆盖大部分普通 MonoBehaviour；特殊组件应注册同步器。
            var json = JsonUtility.ToJson(source);
            JsonUtility.FromJsonOverwrite(json, target);
        }

        private static List<Type> ResolveComponentTypes(
            IReadOnlyList<string> names,
            ICollection<string> errors)
        {
            var result = new List<Type>();
            for (var index = 0; index < names.Count; index++)
            {
                var name = names[index];
                var type = ResolveType(name);
                if (type == null)
                {
                    errors.Add(
                        "Camera component type '" + name +
                        "' could not be resolved.");
                    continue;
                }

                if (!typeof(Component).IsAssignableFrom(type) ||
                    typeof(Transform).IsAssignableFrom(type) ||
                    type == typeof(Camera) ||
                    type == typeof(CameraRig) ||
                    type == typeof(RigCameraSlot) ||
                    type == typeof(SceneCameraBinding))
                {
                    errors.Add(
                        "Type '" + name +
                        "' is not a supported camera component.");
                    continue;
                }

                if (!result.Contains(type))
                {
                    result.Add(type);
                }
            }

            return result;
        }

        private static Type ResolveType(string name)
        {
            var type = Type.GetType(name, false);
            if (type != null)
            {
                return type;
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var index = 0; index < assemblies.Length; index++)
            {
                type = assemblies[index].GetType(name, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static int DisableSceneCameras(
            IReadOnlyList<CameraMatch> matches)
        {
            var count = 0;
            for (var index = 0; index < matches.Count; index++)
            {
                if (matches[index].Source.enabled)
                {
                    matches[index].Source.enabled = false;
                    count++;
                }
            }

            return count;
        }

        private RigSyncResult SetResult(
            string sceneName,
            string rigId,
            int matchedCameraCount,
            int synchronizedPoseCount,
            int synchronizedCameraCount,
            int synchronizedComponentCount,
            int disabledSceneCameraCount,
            IList<string> errors)
        {
            LastSyncResult = new RigSyncResult(
                sceneName,
                rigId,
                matchedCameraCount,
                synchronizedPoseCount,
                synchronizedCameraCount,
                synchronizedComponentCount,
                disabledSceneCameraCount,
                errors);
            return LastSyncResult;
        }

        private static string RequireId(string value, string owner)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    owner + " must define a non-empty ID.");
            }

            return value.Trim();
        }

        private void EnsureActive()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RigService));
            }
        }

        private sealed class RigState
        {
            public RigState(CameraRig rig, string id)
            {
                Rig = rig;
                Id = id;
                Slots = new Dictionary<string, RigCameraSlot>(
                    StringComparer.Ordinal);
                var values = rig.GetComponentsInChildren<
                    RigCameraSlot>(true);
                for (var index = 0; index < values.Length; index++)
                {
                    var slot = values[index];
                    var slotId = RequireId(slot.Id, "Camera slot");
                    if (Slots.ContainsKey(slotId))
                    {
                        throw new InvalidOperationException(
                            "Camera slot ID '" + slotId +
                            "' is duplicated in rig '" + id + "'.");
                    }

                    Slots.Add(slotId, slot);
                }

                if (Slots.Count == 0)
                {
                    throw new InvalidOperationException(
                        "Camera rig '" + id +
                        "' must contain at least one RigCameraSlot.");
                }
            }

            public CameraRig Rig { get; }

            public string Id { get; }

            public Dictionary<string, RigCameraSlot> Slots { get; }
        }

        private readonly struct CameraMatch
        {
            public CameraMatch(
                Camera source,
                RigCameraSlot target,
                SceneCameraBinding binding)
            {
                Source = source;
                Target = target;
                Binding = binding;
            }

            public Camera Source { get; }

            public RigCameraSlot Target { get; }

            public SceneCameraBinding Binding { get; }
        }
    }
}
