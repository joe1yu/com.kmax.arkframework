using System;

namespace ArkFramework
{
    [Serializable]
    public sealed class SceneTableRow
    {
        public string Id { get; set; }

        public string Address { get; set; }

        public SceneLoadMode Mode { get; set; }

        public bool ActivateOnLoad { get; set; }

        public string RigId { get; set; }

        public bool SyncRigPose { get; set; }

        public bool SyncCameraSettings { get; set; }

        public bool SyncComponents { get; set; }

        public string[] ComponentTypes { get; set; }

        public bool DisableSceneCameras { get; set; }

        public SceneRequest CreateRequest()
        {
            if (string.IsNullOrWhiteSpace(Id))
            {
                throw new InvalidOperationException(
                    "Scene table rows must define a non-empty ID.");
            }

            var flags = SceneCameraSyncFlags.None;
            if (SyncRigPose)
            {
                flags |= SceneCameraSyncFlags.RigPose;
            }

            if (SyncCameraSettings)
            {
                flags |= SceneCameraSyncFlags.CameraSettings;
            }

            if (SyncComponents)
            {
                flags |= SceneCameraSyncFlags.Components;
            }

            return new SceneRequest(
                Id,
                new ResourceKey(Address),
                Mode,
                ActivateOnLoad,
                new SceneCameraSyncOptions(
                    RigId,
                    flags,
                    ComponentTypes,
                    DisableSceneCameras));
        }
    }
}
