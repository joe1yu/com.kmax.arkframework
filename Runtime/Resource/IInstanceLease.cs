using System;
using UnityEngine;

namespace ArkFramework
{
    public interface IInstanceLease : IDisposable
    {
        long LeaseId { get; }
        ResourceKey Key { get; }
        DateTime CreatedUtc { get; }
        GameObject Instance { get; }
    }
}
