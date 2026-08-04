using System;

namespace ArkFramework
{
    public readonly struct SceneRequest
    {
        public SceneRequest(
            ResourceKey key,
            SceneLoadMode mode,
            bool activateOnLoad)
            : this(
                null,
                key,
                mode,
                activateOnLoad,
                default)
        {
        }

        public SceneRequest(
            string id,
            ResourceKey key,
            SceneLoadMode mode,
            bool activateOnLoad,
            SceneCameraSyncOptions cameraSync)
        {
            if (string.IsNullOrWhiteSpace(key.Value))
            {
                throw new ArgumentException(
                    "A scene resource key is required.",
                    nameof(key));
            }

            if (!Enum.IsDefined(typeof(SceneLoadMode), mode))
            {
                throw new ArgumentOutOfRangeException(nameof(mode));
            }

            if (mode == SceneLoadMode.Single && !activateOnLoad)
            {
                throw new ArgumentException(
                    "Single scene transactions must activate the target.",
                    nameof(activateOnLoad));
            }

            Key = key;
            Mode = mode;
            ActivateOnLoad = activateOnLoad;
            Id = string.IsNullOrWhiteSpace(id)
                ? string.Empty
                : id.Trim();
            CameraSync = cameraSync;
        }

        public string Id { get; }

        public ResourceKey Key { get; }

        public SceneLoadMode Mode { get; }

        public bool ActivateOnLoad { get; }

        public SceneCameraSyncOptions CameraSync { get; }
    }
}
