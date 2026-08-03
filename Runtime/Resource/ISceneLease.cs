using System;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace ArkFramework
{
    public interface ISceneLease : IDisposable
    {
        long LeaseId { get; }
        ResourceKey Key { get; }
        DateTime CreatedUtc { get; }
        SceneInstance Scene { get; }
    }
}
