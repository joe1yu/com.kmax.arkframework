using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace ArkFramework
{
    public sealed class ObjectPool<T> : IObjectPool<T>
    {
        private readonly Func<T> _factory;
        private readonly int _maxIdleCapacity;
        private readonly Action<T> _onCreate;
        private readonly Action<T> _onRent;
        private readonly Action<T> _onReturn;
        private readonly Action<T> _onDestroy;
        private readonly Stack<T> _idle = new Stack<T>();
        private long _totalCreatedCount;
        private long _rentCount;
        private long _hitCount;
        private int _activeCount;
        private int _peakActiveCount;

        public ObjectPool(
            Func<T> factory,
            int initialCapacity = 0,
            int maxIdleCapacity = 32,
            Action<T> onCreate = null,
            Action<T> onRent = null,
            Action<T> onReturn = null,
            Action<T> onDestroy = null)
        {
            if (typeof(T).IsValueType)
            {
                throw new NotSupportedException(
                    "ObjectPool<T> only supports reference types.");
            }

            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            if (initialCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialCapacity),
                    "Initial capacity cannot be negative.");
            }

            if (maxIdleCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxIdleCapacity),
                    "Maximum idle capacity cannot be negative.");
            }

            if (initialCapacity > maxIdleCapacity)
            {
                throw new ArgumentException(
                    "Initial capacity cannot exceed maximum idle capacity.",
                    nameof(initialCapacity));
            }

            _maxIdleCapacity = maxIdleCapacity;
            _onCreate = onCreate;
            _onRent = onRent;
            _onReturn = onReturn;
            _onDestroy = onDestroy;

            try
            {
                Prewarm(initialCapacity);
            }
            catch
            {
                try
                {
                    Clear();
                }
                catch
                {
                    // Preserve the construction failure after attempting cleanup.
                }

                throw;
            }
        }

        public PoolDiagnostics Diagnostics
        {
            get
            {
                return new PoolDiagnostics(
                    _totalCreatedCount,
                    _activeCount,
                    _idle.Count,
                    _peakActiveCount,
                    _rentCount == 0
                        ? 0d
                        : (double)_hitCount / _rentCount);
            }
        }

        public T Rent()
        {
            T item;
            PoolOwnershipRecord ownership;
            var hit = _idle.Count != 0;
            if (hit)
            {
                item = _idle.Pop();
                ownership = GetOwnership(item);
                if (!ReferenceEquals(ownership.Owner, this) ||
                    ownership.State != PoolItemState.Idle)
                {
                    throw new InvalidOperationException(
                        "The pool idle stack contains an item with invalid ownership.");
                }
            }
            else
            {
                item = CreateItem();
                ownership = GetOwnership(item);
            }

            ownership.State = PoolItemState.Renting;
            try
            {
                _onRent?.Invoke(item);
            }
            catch (Exception exception)
            {
                FailAndDestroy(item, ownership, exception);
                throw;
            }

            ownership.State = PoolItemState.Active;
            _activeCount++;
            if (_activeCount > _peakActiveCount)
            {
                _peakActiveCount = _activeCount;
            }

            _rentCount++;
            if (hit)
            {
                _hitCount++;
            }

            return item;
        }

        public void Return(T item)
        {
            if (ReferenceEquals(item, null))
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (!ObjectPoolOwnershipRegistry.TryGet(item, out var ownership))
            {
                throw new InvalidOperationException(
                    "The returned item was not created by this pool.");
            }

            if (!ReferenceEquals(ownership.Owner, this))
            {
                throw new InvalidOperationException(
                    "The returned item belongs to a different pool.");
            }

            if (ownership.State != PoolItemState.Active)
            {
                throw new InvalidOperationException(
                    "The item has already been returned or is not currently rented.");
            }

            ownership.State = PoolItemState.Returning;
            try
            {
                _onReturn?.Invoke(item);
            }
            catch (Exception exception)
            {
                _activeCount--;
                FailAndDestroy(item, ownership, exception);
                throw;
            }

            _activeCount--;
            if (_idle.Count >= _maxIdleCapacity)
            {
                DestroyItem(item, ownership);
                return;
            }

            ownership.State = PoolItemState.Idle;
            _idle.Push(item);
        }

        public void Prewarm(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    "Prewarm count cannot be negative.");
            }

            if (count > _maxIdleCapacity - _idle.Count)
            {
                throw new ArgumentException(
                    "Prewarm count exceeds the remaining idle capacity.",
                    nameof(count));
            }

            for (var index = 0; index < count; index++)
            {
                var item = CreateItem();
                var ownership = GetOwnership(item);
                ownership.State = PoolItemState.Idle;
                _idle.Push(item);
            }
        }

        public void Clear()
        {
            ExceptionDispatchInfo firstFailure = null;
            while (_idle.Count != 0)
            {
                var item = _idle.Pop();
                var ownership = GetOwnership(item);
                try
                {
                    DestroyItem(item, ownership);
                }
                catch (Exception exception)
                {
                    if (firstFailure == null)
                    {
                        firstFailure = ExceptionDispatchInfo.Capture(exception);
                    }
                }
            }

            firstFailure?.Throw();
        }

        private T CreateItem()
        {
            var item = _factory();
            if (ReferenceEquals(item, null))
            {
                throw new InvalidOperationException(
                    "The object pool factory returned null.");
            }

            var ownership = new PoolOwnershipRecord(this);
            if (!ObjectPoolOwnershipRegistry.TryClaim(item, ownership))
            {
                throw new InvalidOperationException(
                    "The object pool factory returned an item that is already owned by a pool.");
            }

            try
            {
                _onCreate?.Invoke(item);
            }
            catch (Exception exception)
            {
                FailCreation(item, ownership, exception);
            }

            if (!ObjectPoolOwnershipRegistry.TryCommitClaim(item, ownership))
            {
                FailCreation(
                    item,
                    ownership,
                    new InvalidOperationException(
                        "The object pool static ownership generation changed while an item was being created."));
            }

            _totalCreatedCount++;
            return item;
        }

        private void FailCreation(
            T item,
            PoolOwnershipRecord ownership,
            Exception primaryFailure)
        {
            try
            {
                ownership.State = PoolItemState.Destroyed;
                _onDestroy?.Invoke(item);
            }
            catch (Exception destroyFailure)
            {
                throw new AggregateException(primaryFailure, destroyFailure);
            }
            finally
            {
                ObjectPoolOwnershipRegistry.ReleaseClaim(item, ownership);
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        private PoolOwnershipRecord GetOwnership(T item)
        {
            if (!ObjectPoolOwnershipRegistry.TryGet(item, out var ownership))
            {
                throw new InvalidOperationException(
                    "The pool item has no ownership record.");
            }

            return ownership;
        }

        private void FailAndDestroy(
            T item,
            PoolOwnershipRecord ownership,
            Exception primaryFailure)
        {
            try
            {
                DestroyItem(item, ownership);
            }
            catch (Exception destroyFailure)
            {
                throw new AggregateException(primaryFailure, destroyFailure);
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        private void DestroyItem(
            T item,
            PoolOwnershipRecord ownership)
        {
            ownership.State = PoolItemState.Destroyed;
            _onDestroy?.Invoke(item);
        }
    }

    internal enum PoolItemState
    {
        Created,
        Renting,
        Active,
        Returning,
        Idle,
        Destroyed
    }

    internal sealed class PoolOwnershipRecord
    {
        public PoolOwnershipRecord(object owner)
        {
            Owner = owner;
            State = PoolItemState.Created;
        }

        public object Owner { get; }

        public long Generation { get; set; }

        public PoolItemState State { get; set; }
    }

    internal static class ObjectPoolOwnershipRegistry
    {
        private static readonly object Sync = new object();
        private static ConditionalWeakTable<object, PoolOwnershipRecord>
            _ownership =
                new ConditionalWeakTable<object, PoolOwnershipRecord>();
        // In-flight claims survive a generation reset as hazard markers.
        // They are removed by the creating operation after it balances cleanup.
        private static readonly ConditionalWeakTable<
            object,
            PoolOwnershipRecord> _pendingClaims =
                new ConditionalWeakTable<object, PoolOwnershipRecord>();
        private static long _generation;

        static ObjectPoolOwnershipRegistry()
        {
            FrameworkStaticReset.Register(Reset);
        }

        public static bool TryClaim(
            object item,
            PoolOwnershipRecord ownership)
        {
            lock (Sync)
            {
                if (_ownership.TryGetValue(item, out _) ||
                    _pendingClaims.TryGetValue(item, out _))
                {
                    return false;
                }

                ownership.Generation = _generation;
                _pendingClaims.Add(item, ownership);
                return true;
            }
        }

        public static bool TryCommitClaim(
            object item,
            PoolOwnershipRecord ownership)
        {
            lock (Sync)
            {
                if (ownership.Generation != _generation ||
                    !_pendingClaims.TryGetValue(
                        item,
                        out var pendingOwnership) ||
                    !ReferenceEquals(pendingOwnership, ownership) ||
                    _ownership.TryGetValue(item, out _))
                {
                    return false;
                }

                _pendingClaims.Remove(item);
                _ownership.Add(item, ownership);
                return true;
            }
        }

        public static void ReleaseClaim(
            object item,
            PoolOwnershipRecord ownership)
        {
            lock (Sync)
            {
                if (_pendingClaims.TryGetValue(
                        item,
                        out var pendingOwnership) &&
                    ReferenceEquals(pendingOwnership, ownership))
                {
                    _pendingClaims.Remove(item);
                }
            }
        }

        public static bool TryGet(
            object item,
            out PoolOwnershipRecord ownership)
        {
            lock (Sync)
            {
                return _ownership.TryGetValue(item, out ownership);
            }
        }

        private static void Reset()
        {
            lock (Sync)
            {
                unchecked
                {
                    _generation++;
                }

                _ownership =
                    new ConditionalWeakTable<object, PoolOwnershipRecord>();
            }
        }
    }
}
