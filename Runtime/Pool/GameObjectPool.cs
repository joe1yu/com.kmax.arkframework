using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public sealed class GameObjectPool : IGameObjectPool, IDisposable
    {
        private readonly IResourceService _resourceService;
        private readonly int _defaultMaxIdleCapacity;
        private readonly Dictionary<ResourceKey, PrefabPool> _pools =
            new Dictionary<ResourceKey, PrefabPool>();
        private readonly GameObject _poolRoot;
        private int _clearGeneration;
        private bool _clearAllInProgress;
        private bool _disposed;

        public GameObjectPool(
            IResourceService resourceService,
            int defaultMaxIdleCapacity = 32)
        {
            _resourceService = resourceService ??
                throw new ArgumentNullException(nameof(resourceService));
            if (defaultMaxIdleCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(defaultMaxIdleCapacity),
                    "Maximum idle capacity cannot be negative.");
            }

            _defaultMaxIdleCapacity = defaultMaxIdleCapacity;
            _poolRoot = new GameObject("ArkFramework.GameObjectPool");
            _poolRoot.hideFlags = HideFlags.HideAndDontSave;
            _poolRoot.SetActive(false);
        }

        public IReadOnlyDictionary<ResourceKey, PoolDiagnostics> Diagnostics
        {
            get
            {
                if (_disposed || _pools.Count == 0)
                {
                    return new ReadOnlyDictionary<ResourceKey, PoolDiagnostics>(
                        new Dictionary<ResourceKey, PoolDiagnostics>());
                }

                var snapshot =
                    new Dictionary<ResourceKey, PoolDiagnostics>(_pools.Count);
                foreach (var pair in _pools)
                {
                    var pool = pair.Value;
                    snapshot.Add(
                        pair.Key,
                        new PoolDiagnostics(
                            pool.TotalCreatedCount,
                            pool.Active.Count,
                            pool.Idle.Count,
                            pool.PeakActiveCount,
                            pool.RentCount == 0
                                ? 0d
                                : (double)pool.HitCount / pool.RentCount));
                }

                return new ReadOnlyDictionary<ResourceKey, PoolDiagnostics>(
                    snapshot);
            }
        }

        public ValueTask<IPooledGameObjectHandle> RentAsync(
            ResourceKey key,
            Transform parent = null,
            CancellationToken token = default)
        {
            return RentAsync(
                key,
                parent,
                Vector3.zero,
                Quaternion.identity,
                token);
        }

        public ValueTask<IPooledGameObjectHandle> RentAsync(
            ResourceKey key,
            Transform parent,
            Vector3 position,
            Quaternion rotation,
            CancellationToken token = default)
        {
            ValidateKey(key);
            EnsureActive();
            EnsureMutationAllowed();
            token.ThrowIfCancellationRequested();
            return new ValueTask<IPooledGameObjectHandle>(
                RentCoreAsync(key, parent, position, rotation, token));
        }

        public void Return(IPooledGameObjectHandle handle)
        {
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            if (!(handle is PooledGameObjectHandle pooledHandle))
            {
                throw new ArgumentException(
                    "The handle was not created by the default GameObjectPool.",
                    nameof(handle));
            }

            EnsureMutationAllowed();
            var entry = pooledHandle.Detach(this);
            if (entry == null)
            {
                return;
            }

            EnsureActive();
            var prefabPool = entry.Pool;
            if (entry.State != PooledInstanceState.Active ||
                !ReferenceEquals(entry.ActiveHandle, pooledHandle) ||
                !prefabPool.Active.Contains(entry))
            {
                throw new InvalidOperationException(
                    "The GameObject handle is not an active rental from this pool.");
            }

            entry.ActiveHandle = null;
            entry.State = PooledInstanceState.Returning;
            var failure = InvokeReturnCallbacksDuringTransition(
                entry,
                prefabPool);
            if (!IsRegisteredTransition(prefabPool, entry))
            {
                CloseTransitionCapturing(entry, failure)?.Throw();
                return;
            }

            failure = CombineFailures(
                failure,
                DeactivateAndReparentDuringTransition(
                    entry,
                    prefabPool));
            if (!IsRegisteredTransition(prefabPool, entry))
            {
                CloseTransitionCapturing(entry, failure)?.Throw();
                return;
            }

            if (!prefabPool.Active.Remove(entry))
            {
                CloseTransitionCapturing(entry, failure)?.Throw();
                return;
            }

            if (failure != null)
            {
                ThrowAfterRelease(entry, failure);
            }

            if (prefabPool.Idle.Count >= prefabPool.MaxIdleCapacity)
            {
                ReleaseEntry(entry);
                return;
            }

            entry.State = PooledInstanceState.Idle;
            prefabPool.Idle.Push(entry);
        }

        public void Clear(ResourceKey key)
        {
            ValidateKey(key);
            EnsureActive();
            EnsureMutationAllowed();
            if (!_pools.TryGetValue(key, out var prefabPool))
            {
                return;
            }

            var failure = ReleaseIdle(prefabPool);
            if (prefabPool.Active.Count == 0)
            {
                _pools.Remove(key);
            }

            failure?.Throw();
        }

        public void ClearAll()
        {
            if (_disposed)
            {
                return;
            }

            EnsureMutationAllowed();
            ExceptionDispatchInfo firstFailure = null;
            _clearAllInProgress = true;
            try
            {
                _clearGeneration++;
                var pools = new PrefabPool[_pools.Count];
                _pools.Values.CopyTo(pools, 0);
                _pools.Clear();
                for (var poolIndex = 0;
                     poolIndex < pools.Length;
                     poolIndex++)
                {
                    var prefabPool = pools[poolIndex];
                    CaptureFailure(
                        ReleaseIdle(prefabPool),
                        ref firstFailure);

                    if (prefabPool.Active.Count == 0)
                    {
                        continue;
                    }

                    var active =
                        new PooledInstance[prefabPool.Active.Count];
                    prefabPool.Active.CopyTo(active);
                    prefabPool.Active.Clear();
                    for (var index = 0; index < active.Length; index++)
                    {
                        var entry = active[index];
                        var previousState = entry.State;
                        entry.ActiveHandle?.Invalidate(this);
                        entry.ActiveHandle = null;
                        entry.State = PooledInstanceState.Returning;
                        var callbackFailure =
                            previousState == PooledInstanceState.Active
                                ? InvokeReturnCallbacks(entry)
                                : null;
                        if (previousState == PooledInstanceState.Active)
                        {
                            callbackFailure = CombineFailures(
                                callbackFailure,
                                DeactivateAndReparent(entry));
                        }

                        CaptureFailure(
                            ReleaseEntryCapturing(
                                entry,
                                callbackFailure),
                            ref firstFailure);
                    }
                }
            }
            finally
            {
                _clearAllInProgress = false;
            }

            firstFailure?.Throw();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            EnsureMutationAllowed();
            ExceptionDispatchInfo failure = null;
            try
            {
                try
                {
                    ClearAll();
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            }
            finally
            {
                _disposed = true;
                DestroyPoolRoot();
            }

            failure?.Throw();
        }

        private async Task<IPooledGameObjectHandle> RentCoreAsync(
            ResourceKey key,
            Transform parent,
            Vector3 position,
            Quaternion rotation,
            CancellationToken token)
        {
            var generation = _clearGeneration;
            var prefabPool = GetOrCreatePool(key);
            PooledInstance entry;
            var reusedIdle = false;
            var deactivateNewInstance = false;
            if (prefabPool.Idle.Count != 0)
            {
                entry = prefabPool.Idle.Pop();
                reusedIdle = true;
            }
            else
            {
                var lease = await _resourceService.InstantiateAsync(
                    key,
                    _poolRoot.transform,
                    token);
                if (_disposed || generation != _clearGeneration)
                {
                    lease.Dispose();
                    throw new InvalidOperationException(
                        "The GameObject pool was cleared while a rent was in progress.");
                }

                prefabPool = GetOrCreatePool(key);
                GameObject instance;
                try
                {
                    instance = lease.Instance;
                    if (instance == null)
                    {
                        throw new InvalidOperationException(
                            "The resource service returned a null GameObject instance.");
                    }

                    entry = new PooledInstance(
                        prefabPool,
                        lease,
                        instance,
                        FindPoolables(instance));
                    prefabPool.TotalCreatedCount++;
                    deactivateNewInstance = true;
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }
            }

            entry.State = PooledInstanceState.Renting;
            var handle = new PooledGameObjectHandle(this, entry);
            entry.ActiveHandle = handle;
            prefabPool.Active.Add(entry);
            try
            {
                if (deactivateNewInstance)
                {
                    entry.Instance.SetActive(false);
                    EnsureRentTransition(prefabPool, entry);
                }

                var transform = entry.Instance.transform;
                transform.SetParent(parent, false);
                EnsureRentTransition(prefabPool, entry);
                transform.position = position;
                EnsureRentTransition(prefabPool, entry);
                transform.rotation = rotation;
                EnsureRentTransition(prefabPool, entry);
                entry.Instance.SetActive(true);
                EnsureRentTransition(prefabPool, entry);
                for (var index = 0;
                     index < entry.Poolables.Length;
                     index++)
                {
                    entry.Poolables[index].OnRent();
                    EnsureRentTransition(prefabPool, entry);
                }
            }
            catch (Exception exception)
            {
                ThrowAfterFailedRent(
                    entry,
                    prefabPool,
                    handle,
                    exception);
            }

            entry.State = PooledInstanceState.Active;
            prefabPool.RentCount++;
            if (reusedIdle)
            {
                prefabPool.HitCount++;
            }

            prefabPool.PeakActiveCount = Math.Max(
                prefabPool.PeakActiveCount,
                prefabPool.Active.Count);
            return handle;
        }

        private PrefabPool GetOrCreatePool(ResourceKey key)
        {
            if (_pools.TryGetValue(key, out var prefabPool))
            {
                return prefabPool;
            }

            prefabPool = new PrefabPool(
                key,
                _defaultMaxIdleCapacity);
            _pools.Add(key, prefabPool);
            return prefabPool;
        }

        private ExceptionDispatchInfo ReleaseIdle(PrefabPool prefabPool)
        {
            ExceptionDispatchInfo firstFailure = null;
            while (prefabPool.Idle.Count != 0)
            {
                var entry = prefabPool.Idle.Pop();
                CaptureFailure(
                    ReleaseEntryCapturing(entry, null),
                    ref firstFailure);
            }

            return firstFailure;
        }

        private void EnsureRentTransition(
            PrefabPool prefabPool,
            PooledInstance entry)
        {
            if (!IsRegisteredTransition(prefabPool, entry))
            {
                throw new InvalidOperationException(
                    "The GameObject rental was cleared during OnRent.");
            }
        }

        private bool IsRegisteredTransition(
            PrefabPool prefabPool,
            PooledInstance entry)
        {
            return !entry.Released &&
                   _pools.TryGetValue(
                       prefabPool.Key,
                       out var registeredPool) &&
                   ReferenceEquals(registeredPool, prefabPool) &&
                   prefabPool.Active.Contains(entry);
        }

        private Exception InvokeReturnCallbacksDuringTransition(
            PooledInstance entry,
            PrefabPool prefabPool)
        {
            Exception firstFailure = null;
            for (var index = 0; index < entry.Poolables.Length; index++)
            {
                try
                {
                    entry.Poolables[index].OnReturn();
                }
                catch (Exception exception)
                {
                    firstFailure =
                        CombineFailures(firstFailure, exception);
                }

                if (!IsRegisteredTransition(prefabPool, entry))
                {
                    break;
                }
            }

            return firstFailure;
        }

        private static Exception InvokeReturnCallbacks(
            PooledInstance entry)
        {
            Exception firstFailure = null;
            for (var index = 0; index < entry.Poolables.Length; index++)
            {
                try
                {
                    entry.Poolables[index].OnReturn();
                }
                catch (Exception exception)
                {
                    firstFailure =
                        CombineFailures(firstFailure, exception);
                }
            }

            return firstFailure;
        }

        private Exception DeactivateAndReparentDuringTransition(
            PooledInstance entry,
            PrefabPool prefabPool)
        {
            Exception firstFailure = null;
            try
            {
                entry.Instance.SetActive(false);
            }
            catch (Exception exception)
            {
                firstFailure = CombineFailures(firstFailure, exception);
            }

            if (!IsRegisteredTransition(prefabPool, entry))
            {
                return firstFailure;
            }

            try
            {
                entry.Instance.transform.SetParent(
                    _poolRoot.transform,
                    false);
            }
            catch (Exception exception)
            {
                firstFailure = CombineFailures(firstFailure, exception);
            }

            return firstFailure;
        }

        private Exception DeactivateAndReparent(PooledInstance entry)
        {
            Exception firstFailure = null;
            try
            {
                entry.Instance.SetActive(false);
            }
            catch (Exception exception)
            {
                firstFailure = CombineFailures(firstFailure, exception);
            }

            try
            {
                entry.Instance.transform.SetParent(
                    _poolRoot.transform,
                    false);
            }
            catch (Exception exception)
            {
                firstFailure = CombineFailures(firstFailure, exception);
            }

            return firstFailure;
        }

        private void ThrowAfterFailedRent(
            PooledInstance entry,
            PrefabPool prefabPool,
            PooledGameObjectHandle handle,
            Exception primaryFailure)
        {
            handle.Invalidate(this);
            entry.ActiveHandle = null;
            if (entry.Released)
            {
                ExceptionDispatchInfo.Capture(primaryFailure).Throw();
            }

            entry.State = PooledInstanceState.Returning;
            var cleanupFailure =
                IsRegisteredTransition(prefabPool, entry)
                    ? DeactivateAndReparentDuringTransition(
                        entry,
                        prefabPool)
                    : null;
            var failure = CombineFailures(
                primaryFailure,
                cleanupFailure);
            if (IsRegisteredTransition(prefabPool, entry))
            {
                prefabPool.Active.Remove(entry);
            }

            if (entry.Released)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            ThrowAfterRelease(entry, failure);
        }

        private static IPoolable[] FindPoolables(GameObject instance)
        {
            var behaviours = instance.GetComponents<MonoBehaviour>();
            if (behaviours.Length == 0)
            {
                return Array.Empty<IPoolable>();
            }

            var poolables = new List<IPoolable>(behaviours.Length);
            for (var index = 0; index < behaviours.Length; index++)
            {
                if (behaviours[index] is IPoolable poolable)
                {
                    poolables.Add(poolable);
                }
            }

            return poolables.ToArray();
        }

        private void ThrowAfterRelease(
            PooledInstance entry,
            Exception primaryFailure)
        {
            var releaseFailure = ReleaseEntryCapturing(entry, null);
            if (releaseFailure != null)
            {
                throw new AggregateException(
                    primaryFailure,
                    releaseFailure.SourceException);
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        private static ExceptionDispatchInfo ReleaseEntryCapturing(
            PooledInstance entry,
            Exception primaryFailure)
        {
            try
            {
                ReleaseEntry(entry);
            }
            catch (Exception releaseFailure)
            {
                return ExceptionDispatchInfo.Capture(
                    primaryFailure == null
                        ? releaseFailure
                        : new AggregateException(
                            primaryFailure,
                            releaseFailure));
            }

            return primaryFailure == null
                ? null
                : ExceptionDispatchInfo.Capture(primaryFailure);
        }

        private static ExceptionDispatchInfo CloseTransitionCapturing(
            PooledInstance entry,
            Exception primaryFailure)
        {
            if (!entry.Released)
            {
                return ReleaseEntryCapturing(
                    entry,
                    primaryFailure);
            }

            return primaryFailure == null
                ? null
                : ExceptionDispatchInfo.Capture(primaryFailure);
        }

        private static void ReleaseEntry(PooledInstance entry)
        {
            entry.State = PooledInstanceState.Destroyed;
            if (entry.Released)
            {
                return;
            }

            entry.Released = true;
            entry.Lease.Dispose();
        }

        private static Exception CombineFailures(
            Exception first,
            Exception second)
        {
            if (first == null)
            {
                return second;
            }

            if (second == null)
            {
                return first;
            }

            return new AggregateException(first, second);
        }

        private static void CaptureFailure(
            ExceptionDispatchInfo candidate,
            ref ExceptionDispatchInfo firstFailure)
        {
            if (firstFailure == null && candidate != null)
            {
                firstFailure = candidate;
            }
        }

        private void DestroyPoolRoot()
        {
            if (_poolRoot == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(_poolRoot);
            }
            else
            {
                Object.DestroyImmediate(_poolRoot);
            }
        }

        private void EnsureActive()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(GameObjectPool));
            }
        }

        private void EnsureMutationAllowed()
        {
            if (_clearAllInProgress)
            {
                throw new InvalidOperationException(
                    "The GameObject pool cannot be modified while ClearAll is in progress.");
            }
        }

        private static void ValidateKey(ResourceKey key)
        {
            if (string.IsNullOrWhiteSpace(key.Value))
            {
                throw new ArgumentException(
                    "A resource key cannot be the default value.",
                    nameof(key));
            }
        }

        internal sealed class PrefabPool
        {
            public PrefabPool(ResourceKey key, int maxIdleCapacity)
            {
                Key = key;
                MaxIdleCapacity = maxIdleCapacity;
            }

            public ResourceKey Key { get; }

            public int MaxIdleCapacity { get; }

            public Stack<PooledInstance> Idle { get; } =
                new Stack<PooledInstance>();

            public HashSet<PooledInstance> Active { get; } =
                new HashSet<PooledInstance>();

            public long TotalCreatedCount { get; set; }

            public long RentCount { get; set; }

            public long HitCount { get; set; }

            public int PeakActiveCount { get; set; }
        }

        internal sealed class PooledInstance
        {
            internal PooledInstance(
                PrefabPool pool,
                IInstanceLease lease,
                GameObject instance,
                IPoolable[] poolables)
            {
                Pool = pool;
                Lease = lease;
                Instance = instance;
                Poolables = poolables;
            }

            internal PrefabPool Pool { get; }

            internal IInstanceLease Lease { get; }

            internal GameObject Instance { get; }

            internal IPoolable[] Poolables { get; }

            internal PooledGameObjectHandle ActiveHandle { get; set; }

            internal PooledInstanceState State { get; set; }

            internal bool Released { get; set; }
        }

        internal enum PooledInstanceState
        {
            Renting,
            Active,
            Returning,
            Idle,
            Destroyed
        }
    }

    public sealed class PooledGameObjectHandle : IPooledGameObjectHandle
    {
        private GameObjectPool _owner;
        private GameObjectPool.PooledInstance _entry;

        internal PooledGameObjectHandle(
            GameObjectPool owner,
            GameObjectPool.PooledInstance entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public GameObject Instance
        {
            get
            {
                if (_owner == null || _entry == null)
                {
                    throw new ObjectDisposedException(
                        nameof(PooledGameObjectHandle));
                }

                return _entry.Instance;
            }
        }

        public void Dispose()
        {
            _owner?.Return(this);
        }

        internal GameObjectPool.PooledInstance Detach(
            GameObjectPool expectedOwner)
        {
            if (_owner == null)
            {
                return null;
            }

            if (!ReferenceEquals(_owner, expectedOwner))
            {
                throw new ArgumentException(
                    "The GameObject handle belongs to a different pool.");
            }

            var entry = _entry;
            _owner = null;
            _entry = null;
            return entry;
        }

        internal void Invalidate(GameObjectPool expectedOwner)
        {
            if (ReferenceEquals(_owner, expectedOwner))
            {
                _owner = null;
                _entry = null;
            }
        }
    }
}
