using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ArkFramework
{
    public readonly struct SceneCameraSyncOptions
    {
        private static readonly IReadOnlyList<string> EmptyComponentTypes =
            Array.AsReadOnly(Array.Empty<string>());

        private readonly IReadOnlyList<string> _componentTypeNames;

        public SceneCameraSyncOptions(
            string rigId,
            SceneCameraSyncFlags flags,
            IEnumerable<string> componentTypeNames = null,
            bool disableSceneCameras = false)
        {
            if ((flags & ~(
                    SceneCameraSyncFlags.RigPose |
                    SceneCameraSyncFlags.CameraSettings |
                    SceneCameraSyncFlags.Components)) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(flags));
            }

            RigId = string.IsNullOrWhiteSpace(rigId)
                ? string.Empty
                : rigId.Trim();
            Flags = flags;
            DisableSceneCameras = disableSceneCameras;
            var values = componentTypeNames == null
                ? Array.Empty<string>()
                : componentTypeNames
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            _componentTypeNames = new ReadOnlyCollection<string>(values);
        }

        public string RigId { get; }

        public SceneCameraSyncFlags Flags { get; }

        public IReadOnlyList<string> ComponentTypeNames =>
            _componentTypeNames ?? EmptyComponentTypes;

        public bool DisableSceneCameras { get; }

        public bool Enabled =>
            !string.IsNullOrEmpty(RigId) ||
            Flags != SceneCameraSyncFlags.None ||
            DisableSceneCameras;
    }
}
