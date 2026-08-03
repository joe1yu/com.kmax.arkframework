using System;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public sealed class AssetLease<T> : IAssetLease<T> where T : Object
    {
        private readonly T _asset;
        private Action _release;

        internal AssetLease(
            long leaseId,
            ResourceKey key,
            string label,
            T asset,
            DateTime createdUtc,
            Action release)
        {
            LeaseId = leaseId;
            Key = key;
            Label = label;
            _asset = asset;
            CreatedUtc = createdUtc;
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }

        public long LeaseId { get; }

        public ResourceKey Key { get; }

        public string Label { get; }

        public string KeyOrLabel =>
            Label ?? Key.Value ?? string.Empty;

        public DateTime CreatedUtc { get; }

        public T Asset
        {
            get
            {
                ThrowIfDisposed();
                return _asset;
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _release) == null)
            {
                throw new ObjectDisposedException(
                    $"{nameof(AssetLease<T>)}<{typeof(T).Name}>");
            }
        }
    }
}
