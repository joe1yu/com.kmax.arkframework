using System;

namespace ArkFramework
{
    public readonly struct SceneRequest
    {
        public SceneRequest(
            ResourceKey key,
            SceneLoadMode mode,
            bool activateOnLoad)
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
        }

        public ResourceKey Key { get; }

        public SceneLoadMode Mode { get; }

        public bool ActivateOnLoad { get; }
    }
}
