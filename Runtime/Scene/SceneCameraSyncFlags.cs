using System;

namespace ArkFramework
{
    [Flags]
    public enum SceneCameraSyncFlags
    {
        None = 0,
        RigPose = 1 << 0,
        CameraSettings = 1 << 1,
        Components = 1 << 2
    }
}
