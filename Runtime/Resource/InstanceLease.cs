using System;
using System.Threading;
using UnityEngine;

namespace ArkFramework
{
    public sealed class InstanceLease : IInstanceLease
    {
        private readonly GameObject _instance;
        private Action _release;

        internal InstanceLease(
            long leaseId,
            ResourceKey key,
            GameObject instance,
            DateTime createdUtc,
            Action release)
        {
            LeaseId = leaseId;
            Key = key;
            _instance = instance;
            CreatedUtc = createdUtc;
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }

        public long LeaseId { get; }

        public ResourceKey Key { get; }

        public DateTime CreatedUtc { get; }

        public GameObject Instance
        {
            get
            {
                if (Volatile.Read(ref _release) == null)
                {
                    throw new ObjectDisposedException(nameof(InstanceLease));
                }

                return _instance;
            }
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
