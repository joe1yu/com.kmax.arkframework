using System.Collections.Generic;

namespace ArkFramework
{
    public interface IRigService
    {
        IReadOnlyList<CameraRig> Rigs { get; }

        CameraRig ActiveRig { get; }

        string ActiveRigId { get; }

        RigSyncResult LastSyncResult { get; }

        bool TryGetRig(string id, out CameraRig rig);

        CameraRig GetRig(string id);

        void ActivateRig(string id);

        void RegisterComponentSynchronizer(
            IRigComponentSynchronizer synchronizer);

        RigSyncResult SynchronizeActiveScene(
            SceneCameraSyncOptions options);
    }
}
