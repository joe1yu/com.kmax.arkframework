using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ArkFramework
{
    public interface IPooledGameObjectHandle : IDisposable
    {
        GameObject Instance { get; }
    }

    public interface IGameObjectPool
    {
        ValueTask<IPooledGameObjectHandle> RentAsync(
            ResourceKey key,
            Transform parent = null,
            CancellationToken token = default);

        ValueTask<IPooledGameObjectHandle> RentAsync(
            ResourceKey key,
            Transform parent,
            Vector3 position,
            Quaternion rotation,
            CancellationToken token = default);

        void Return(IPooledGameObjectHandle handle);

        void Clear(ResourceKey key);

        void ClearAll();

        IReadOnlyDictionary<ResourceKey, PoolDiagnostics> Diagnostics { get; }
    }
}
