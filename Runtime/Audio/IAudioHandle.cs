using System;

namespace ArkFramework
{
    public interface IAudioHandle
    {
        Guid InstanceId { get; }
        ResourceKey ResourceKey { get; }
        AudioChannel Channel { get; }
        bool IsValid { get; }
    }
}
