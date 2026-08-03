using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public interface IAssetLease<out T> : IDisposable where T : Object
    {
        long LeaseId { get; }
        ResourceKey Key { get; }
        string Label { get; }
        string KeyOrLabel { get; }
        DateTime CreatedUtc { get; }
        T Asset { get; }
    }
}
