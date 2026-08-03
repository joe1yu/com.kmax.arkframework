using System;
using System.Threading;
using UnityEngine.ResourceManagement.ResourceProviders;

namespace ArkFramework
{
    public sealed class SceneLease : ISceneLease
    {
        private readonly SceneInstance _scene;
        private readonly object _sync = new object();
        private SceneLeaseRegistration _registration;
        private bool _disposeRequested;
        private bool _unloadInProgress;

        internal SceneLease(
            long leaseId,
            ResourceKey key,
            SceneInstance scene,
            DateTime createdUtc,
            SceneLeaseRegistration registration)
        {
            LeaseId = leaseId;
            Key = key;
            _scene = scene;
            CreatedUtc = createdUtc;
            _registration = registration ??
                throw new ArgumentNullException(nameof(registration));
        }

        public long LeaseId { get; }

        public ResourceKey Key { get; }

        public DateTime CreatedUtc { get; }

        public SceneInstance Scene
        {
            get
            {
                lock (_sync)
                {
                    if (_registration == null)
                    {
                        throw new ObjectDisposedException(nameof(SceneLease));
                    }

                    return _scene;
                }
            }
        }

        public void Dispose()
        {
            SceneLeaseRegistration registration;
            lock (_sync)
            {
                _disposeRequested = true;
                registration = _registration;
                _registration = null;
            }

            registration?.Dispose();
        }

        internal SceneLeaseRegistration TransferForUnload(ResourceService owner)
        {
            SceneLeaseRegistration registration;
            lock (_sync)
            {
                registration = _registration;
                if (registration == null)
                {
                    throw new ObjectDisposedException(nameof(SceneLease));
                }

                if (!registration.IsOwnedBy(owner))
                {
                    throw new ArgumentException(
                        "The scene lease belongs to a different resource service.",
                        nameof(owner));
                }

                _registration = null;
                _unloadInProgress = true;
            }

            registration.RemoveLease();
            return registration;
        }

        internal bool RestoreAfterFailedUnload(
            ResourceService owner,
            SceneLeaseRegistration registration)
        {
            if (registration == null)
            {
                throw new ArgumentNullException(nameof(registration));
            }

            if (!registration.IsOwnedBy(owner))
            {
                throw new ArgumentException(
                    "The scene lease belongs to a different resource service.",
                    nameof(owner));
            }

            lock (_sync)
            {
                if (!_unloadInProgress || _registration != null)
                {
                    throw new InvalidOperationException(
                        "The scene lease is not awaiting unload recovery.");
                }

                _unloadInProgress = false;
                if (_disposeRequested)
                {
                    registration.ReleaseBackend();
                    return false;
                }

                try
                {
                    registration.RestoreLease(this, Key, CreatedUtc);
                    _registration = registration;
                    return true;
                }
                catch
                {
                    registration.ReleaseBackend();
                    throw;
                }
            }
        }
    }

    internal sealed class SceneLeaseRegistration : IDisposable
    {
        private readonly ResourceService _owner;
        private readonly long _leaseId;
        private Action _releaseBackend;
        private int _removed;

        public SceneLeaseRegistration(
            ResourceService owner,
            long leaseId,
            Action releaseBackend)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _leaseId = leaseId;
            _releaseBackend = releaseBackend ??
                throw new ArgumentNullException(nameof(releaseBackend));
        }

        public bool IsOwnedBy(ResourceService owner)
        {
            return ReferenceEquals(_owner, owner);
        }

        public void RemoveLease()
        {
            if (Interlocked.Exchange(ref _removed, 1) == 0)
            {
                _owner.RemoveLease(_leaseId);
            }
        }

        public void RestoreLease(
            SceneLease lease,
            ResourceKey key,
            DateTime createdUtc)
        {
            if (Interlocked.CompareExchange(ref _removed, 0, 1) != 1)
            {
                throw new InvalidOperationException(
                    "The scene lease registration was not removed.");
            }

            try
            {
                _owner.RestoreSceneLease(
                    _leaseId,
                    key,
                    createdUtc,
                    lease);
            }
            catch
            {
                Interlocked.Exchange(ref _removed, 1);
                throw;
            }
        }

        public void ReleaseBackend()
        {
            Interlocked.Exchange(ref _releaseBackend, null)?.Invoke();
        }

        public void Dispose()
        {
            RemoveLease();
            ReleaseBackend();
        }
    }
}
