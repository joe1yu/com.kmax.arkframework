using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public sealed class ResourceService :
        IResourceService,
        ISceneResourceLoader,
        ISceneTransactionResourceLoader,
        IAsyncDisposable
    {
        private const string ModuleId = BuiltInModuleIds.Resource;
        private const string CleanupCategory = "Cleanup";

        private readonly object _sync = new object();
        private readonly IResourceBackend _backend;
        private readonly IFrameworkLogger _logger;
        private readonly CancellationTokenSource _lifetime =
            new CancellationTokenSource();
        private readonly Dictionary<long, InflightOperation> _inflight =
            new Dictionary<long, InflightOperation>();
        private readonly Dictionary<long, ResourceLeaseDiagnostics> _leases =
            new Dictionary<long, ResourceLeaseDiagnostics>();
        private readonly Dictionary<long, IDisposable> _leaseOwners =
            new Dictionary<long, IDisposable>();
        private long _nextOperationId;
        private long _nextLeaseId;
        private bool _stopped;
        private int _disposed;
        private Task _stopTask;

        public ResourceService(
            IResourceBackend backend,
            IFrameworkLogger logger = null)
        {
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _logger = logger ?? new UnityFrameworkLogger();
        }

        public ResourceDiagnostics Diagnostics
        {
            get
            {
                lock (_sync)
                {
                    var entries =
                        new ResourceLeaseDiagnostics[_leases.Count];
                    var index = 0;
                    foreach (var entry in _leases.Values)
                    {
                        entries[index++] = entry;
                    }

                    Array.Sort(
                        entries,
                        (left, right) => left.LeaseId.CompareTo(right.LeaseId));
                    return new ResourceDiagnostics(
                        Array.AsReadOnly(entries),
                        _inflight.Count);
                }
            }
        }

        public ValueTask<IAssetLease<T>> LoadAsync<T>(
            ResourceKey key,
            CancellationToken token = default)
            where T : Object
        {
            ValidateKey(key);
            token.ThrowIfCancellationRequested();
            var operation = StartOperation(
                () => _backend.LoadAssetAsync<T>(key));
            return new ValueTask<IAssetLease<T>>(
                LoadAssetCoreAsync(operation, key, token));
        }

        public ValueTask<IInstanceLease> InstantiateAsync(
            ResourceKey key,
            Transform parent = null,
            CancellationToken token = default)
        {
            ValidateKey(key);
            token.ThrowIfCancellationRequested();
            var operation = StartOperation(
                () => _backend.InstantiateAsync(key, parent));
            return new ValueTask<IInstanceLease>(
                InstantiateCoreAsync(operation, key, token));
        }

        public ValueTask<IReadOnlyList<IAssetLease<T>>> LoadByLabelAsync<T>(
            string label,
            CancellationToken token = default)
            where T : Object
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new ArgumentException(
                    "A resource label cannot be null, empty, or whitespace.",
                    nameof(label));
            }

            token.ThrowIfCancellationRequested();
            var operation = StartOperation(
                () => _backend.LoadByLabelAsync<T>(label));
            return new ValueTask<IReadOnlyList<IAssetLease<T>>>(
                LoadLabelCoreAsync(operation, label, token));
        }

        public ValueTask<ISceneLease> LoadSceneAsync(
            ResourceKey key,
            LoadSceneMode mode,
            bool activateOnLoad,
            CancellationToken token = default)
        {
            ValidateKey(key);
            token.ThrowIfCancellationRequested();
            var operation = StartOperation(
                () => _backend.LoadSceneAsync(key, mode, activateOnLoad));
            return new ValueTask<ISceneLease>(
                LoadSceneCoreAsync(operation, key, token));
        }

        public ValueTask<ISceneLease> LoadSceneTransactionAsync(
            ResourceKey key,
            LoadSceneMode mode,
            bool activateOnLoad,
            CancellationToken token = default)
        {
            ValidateKey(key);
            token.ThrowIfCancellationRequested();
            var operation = StartOperation(
                () => _backend.LoadSceneAsync(key, mode, activateOnLoad));
            return new ValueTask<ISceneLease>(
                LoadSceneTransactionCoreAsync(
                    operation,
                    key,
                    token));
        }

        public ValueTask UnloadSceneAsync(
            ISceneLease lease,
            CancellationToken token = default)
        {
            if (lease == null)
            {
                throw new ArgumentNullException(nameof(lease));
            }

            if (!(lease is SceneLease ownedLease))
            {
                throw new ArgumentException(
                    "The scene lease was not created by the default ResourceService.",
                    nameof(lease));
            }

            token.ThrowIfCancellationRequested();
            EnsureRunning();
            var scene = ownedLease.Scene;
            var registration = ownedLease.TransferForUnload(this);
            try
            {
                var backendOperation = StartOperation(
                    () => _backend.UnloadSceneAsync(scene));
                return new ValueTask(
                    UnloadSceneCoreAsync(
                        backendOperation,
                        ownedLease,
                        registration,
                        token));
            }
            catch (Exception primary)
            {
                try
                {
                    ownedLease.RestoreAfterFailedUnload(this, registration);
                }
                catch (Exception recovery)
                {
                    throw new AggregateException(primary, recovery);
                }

                throw;
            }
        }

        public ValueTask StopAsync(CancellationToken token = default)
        {
            Task stopTask;
            InflightOperation[] inflight = null;
            IDisposable[] leases = null;
            TaskCompletionSource<bool> completion = null;
            lock (_sync)
            {
                if (_stopTask == null)
                {
                    _stopped = true;
                    inflight = new InflightOperation[_inflight.Count];
                    _inflight.Values.CopyTo(inflight, 0);
                    leases = SnapshotLeaseOwners();
                    completion = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _stopTask = completion.Task;
                }

                stopTask = _stopTask;
            }

            if (completion != null)
            {
                _ = StopCoreAsync(inflight, leases, completion);
            }

            return token.CanBeCanceled
                ? new ValueTask(ApplyStopCancellationAsync(stopTask, token))
                : new ValueTask(stopTask);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
            }
            finally
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _lifetime.Dispose();
                }
            }
        }

        internal void RemoveLease(long leaseId)
        {
            lock (_sync)
            {
                _leases.Remove(leaseId);
                _leaseOwners.Remove(leaseId);
            }
        }

        internal void RestoreSceneLease(
            long leaseId,
            ResourceKey key,
            DateTime createdUtc,
            SceneLease lease)
        {
            lock (_sync)
            {
                EnsureRunningNoLock();
                AddLease(
                    leaseId,
                    ResourceLeaseKind.Scene,
                    key.Value,
                    typeof(SceneInstance),
                    createdUtc,
                    lease);
            }
        }

        private async Task<IAssetLease<T>> LoadAssetCoreAsync<T>(
            InflightOperation<T> operation,
            ResourceKey key,
            CancellationToken token)
            where T : Object
        {
            T asset;
            try
            {
                asset = await AwaitOperationAsync(operation, token);
            }
            catch (OperationCanceledException)
            {
                BeginCleanup(operation);
                throw;
            }
            catch
            {
                ReleaseFailedOperation(operation);
                throw;
            }

            lock (_sync)
            {
                _inflight.Remove(operation.Id);
                if (_stopped)
                {
                    operation.Release();
                    throw new OperationCanceledException(_lifetime.Token);
                }

                var leaseId = NextLeaseId();
                var createdUtc = DateTime.UtcNow;
                var lease = new AssetLease<T>(
                    leaseId,
                    key,
                    null,
                    asset,
                    createdUtc,
                    () => ReleaseLease(leaseId, operation.Release));
                AddLease(
                    leaseId,
                    ResourceLeaseKind.Asset,
                    key.Value,
                    typeof(T),
                    createdUtc,
                    lease);
                return lease;
            }
        }

        private async Task<IInstanceLease> InstantiateCoreAsync(
            InflightOperation<GameObject> operation,
            ResourceKey key,
            CancellationToken token)
        {
            GameObject instance;
            try
            {
                instance = await AwaitOperationAsync(operation, token);
            }
            catch (OperationCanceledException)
            {
                BeginCleanup(operation);
                throw;
            }
            catch
            {
                ReleaseFailedOperation(operation);
                throw;
            }

            lock (_sync)
            {
                _inflight.Remove(operation.Id);
                if (_stopped)
                {
                    operation.Release();
                    throw new OperationCanceledException(_lifetime.Token);
                }

                var leaseId = NextLeaseId();
                var createdUtc = DateTime.UtcNow;
                var lease = new InstanceLease(
                    leaseId,
                    key,
                    instance,
                    createdUtc,
                    () => ReleaseLease(leaseId, operation.Release));
                AddLease(
                    leaseId,
                    ResourceLeaseKind.Instance,
                    key.Value,
                    typeof(GameObject),
                    createdUtc,
                    lease);
                return lease;
            }
        }

        private async Task<IReadOnlyList<IAssetLease<T>>> LoadLabelCoreAsync<T>(
            InflightOperation<IReadOnlyList<T>> operation,
            string label,
            CancellationToken token)
            where T : Object
        {
            IReadOnlyList<T> assets;
            try
            {
                assets = await AwaitOperationAsync(operation, token);
            }
            catch (OperationCanceledException)
            {
                BeginCleanup(operation);
                throw;
            }
            catch
            {
                ReleaseFailedOperation(operation);
                throw;
            }

            if (assets == null)
            {
                ReleaseFailedOperation(operation);
                throw new InvalidOperationException(
                    "The resource backend returned a null label result.");
            }

            lock (_sync)
            {
                _inflight.Remove(operation.Id);
                if (_stopped)
                {
                    operation.Release();
                    throw new OperationCanceledException(_lifetime.Token);
                }

                if (assets.Count == 0)
                {
                    operation.Release();
                    return Array.Empty<IAssetLease<T>>();
                }

                var sharedRelease =
                    new SharedReleaseState(assets.Count, operation.Release);
                var leases = new IAssetLease<T>[assets.Count];
                for (var index = 0; index < assets.Count; index++)
                {
                    var leaseId = NextLeaseId();
                    var createdUtc = DateTime.UtcNow;
                    var capturedLeaseId = leaseId;
                    var lease = new AssetLease<T>(
                        leaseId,
                        default,
                        label,
                        assets[index],
                        createdUtc,
                        () => ReleaseLease(
                            capturedLeaseId,
                            sharedRelease.ReleaseOne));
                    leases[index] = lease;
                    AddLease(
                        leaseId,
                        ResourceLeaseKind.Label,
                        label,
                        typeof(T),
                        createdUtc,
                        lease);
                }

                return Array.AsReadOnly(leases);
            }
        }

        private async Task<ISceneLease> LoadSceneCoreAsync(
            InflightOperation<SceneInstance> operation,
            ResourceKey key,
            CancellationToken token)
        {
            SceneInstance scene;
            try
            {
                scene = await AwaitOperationAsync(operation, token);
            }
            catch (OperationCanceledException)
            {
                BeginCleanup(operation);
                throw;
            }
            catch
            {
                ReleaseFailedOperation(operation);
                throw;
            }

            lock (_sync)
            {
                _inflight.Remove(operation.Id);
                if (_stopped)
                {
                    operation.Release();
                    throw new OperationCanceledException(_lifetime.Token);
                }

                var leaseId = NextLeaseId();
                var createdUtc = DateTime.UtcNow;
                var registration = new SceneLeaseRegistration(
                    this,
                    leaseId,
                    operation.Release);
                var lease = new SceneLease(
                    leaseId,
                    key,
                    scene,
                    createdUtc,
                    registration);
                AddLease(
                    leaseId,
                    ResourceLeaseKind.Scene,
                    key.Value,
                    typeof(SceneInstance),
                    createdUtc,
                    lease);
                return lease;
            }
        }

        private async Task<ISceneLease> LoadSceneTransactionCoreAsync(
            InflightOperation<SceneInstance> operation,
            ResourceKey key,
            CancellationToken callerToken)
        {
            SceneInstance scene;
            try
            {
                scene = await AwaitOperationAsync(
                    operation,
                    _lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                BeginCleanup(operation);
                throw;
            }
            catch
            {
                ReleaseFailedOperation(operation);
                throw;
            }

            SceneLease lease;
            lock (_sync)
            {
                _inflight.Remove(operation.Id);
                if (_stopped)
                {
                    operation.Release();
                    throw new OperationCanceledException(_lifetime.Token);
                }

                var leaseId = NextLeaseId();
                var createdUtc = DateTime.UtcNow;
                var registration = new SceneLeaseRegistration(
                    this,
                    leaseId,
                    operation.Release);
                lease = new SceneLease(
                    leaseId,
                    key,
                    scene,
                    createdUtc,
                    registration);
                AddLease(
                    leaseId,
                    ResourceLeaseKind.Scene,
                    key.Value,
                    typeof(SceneInstance),
                    createdUtc,
                    lease);
            }

            if (!callerToken.IsCancellationRequested)
            {
                return lease;
            }

            var cancellation =
                new OperationCanceledException(callerToken);
            try
            {
                await UnloadSceneAsync(
                    lease,
                    CancellationToken.None);
            }
            catch (Exception cleanup)
            {
                try
                {
                    lease.Dispose();
                }
                catch (Exception terminalRelease)
                {
                    throw new AggregateException(
                        cancellation,
                        cleanup,
                        terminalRelease);
                }

                throw new AggregateException(cancellation, cleanup);
            }

            throw cancellation;
        }

        private async Task CompleteWithoutLeaseAsync<T>(
            InflightOperation<T> operation,
            CancellationToken token)
        {
            try
            {
                await AwaitOperationAsync(operation, token);
            }
            catch (OperationCanceledException)
            {
                BeginCleanup(operation);
                throw;
            }
            catch
            {
                ReleaseFailedOperation(operation);
                throw;
            }

            lock (_sync)
            {
                _inflight.Remove(operation.Id);
            }

            operation.Release();
        }

        private async Task UnloadSceneCoreAsync(
            InflightOperation<SceneInstance> operation,
            SceneLease lease,
            SceneLeaseRegistration registration,
            CancellationToken token)
        {
            try
            {
                await AwaitOperationAsync(operation, token);
            }
            catch (OperationCanceledException)
            {
                operation.AttachRelease(registration.ReleaseBackend);
                BeginCleanup(operation);
                throw;
            }
            catch (Exception primary)
            {
                ReleaseFailedOperation(operation);
                try
                {
                    lease.RestoreAfterFailedUnload(this, registration);
                }
                catch (Exception recovery)
                {
                    throw new AggregateException(primary, recovery);
                }

                throw;
            }

            lock (_sync)
            {
                _inflight.Remove(operation.Id);
            }

            try
            {
                operation.Release();
            }
            finally
            {
                registration.ReleaseBackend();
            }
        }

        private InflightOperation<T> StartOperation<T>(
            Func<IResourceOperation<T>> start)
        {
            lock (_sync)
            {
                EnsureRunningNoLock();
                var backendOperation = start();
                if (backendOperation == null)
                {
                    throw new InvalidOperationException(
                        "The resource backend returned a null operation.");
                }

                if (backendOperation.Task == null)
                {
                    backendOperation.Release();
                    throw new InvalidOperationException(
                        "The resource backend returned an operation with a null task.");
                }

                var operation = new InflightOperation<T>(
                    ++_nextOperationId,
                    backendOperation);
                _inflight.Add(operation.Id, operation);
                return operation;
            }
        }

        private async Task<T> AwaitOperationAsync<T>(
            InflightOperation<T> operation,
            CancellationToken callerToken)
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                       callerToken,
                       _lifetime.Token))
            {
                return await AwaitWithCancellationAsync(
                    operation.TypedTask,
                    linked.Token);
            }
        }

        private static async Task<T> AwaitWithCancellationAsync<T>(
            Task<T> operation,
            CancellationToken token)
        {
            if (!token.CanBeCanceled || operation.IsCompleted)
            {
                return await operation;
            }

            var canceled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(
                       state =>
                           ((TaskCompletionSource<bool>)state).TrySetResult(true),
                       canceled))
            {
                if (operation != await Task.WhenAny(operation, canceled.Task))
                {
                    throw new OperationCanceledException(token);
                }
            }

            return await operation;
        }

        private Task BeginCleanup(InflightOperation operation)
        {
            Task cleanupTask;
            var observeCleanup = false;
            lock (_sync)
            {
                if (operation.CleanupTask == null)
                {
                    operation.CleanupTask = CleanupOperationAsync(operation);
                    observeCleanup = true;
                }

                cleanupTask = operation.CleanupTask;
            }

            if (observeCleanup)
            {
                _ = ObserveCleanupAsync(cleanupTask);
            }

            return cleanupTask;
        }

        private async Task ObserveCleanupAsync(Task cleanupTask)
        {
            try
            {
                await cleanupTask;
            }
            catch (Exception exception)
            {
                SafeLogError(
                    "Asynchronous backend operation cleanup failed.",
                    exception);
            }
        }

        private async Task CleanupOperationAsync(InflightOperation operation)
        {
            try
            {
                await operation.Task;
            }
            catch
            {
                // Cleanup observes backend failures after caller cancellation.
            }
            finally
            {
                try
                {
                    operation.Release();
                }
                finally
                {
                    lock (_sync)
                    {
                        _inflight.Remove(operation.Id);
                    }
                }
            }
        }

        private void ReleaseFailedOperation(InflightOperation operation)
        {
            lock (_sync)
            {
                _inflight.Remove(operation.Id);
            }

            try
            {
                operation.Release();
            }
            catch (Exception exception)
            {
                SafeLogError(
                    "Failed to release a failed backend operation.",
                    exception);
            }
        }

        private void SafeLogError(string message, Exception exception)
        {
            try
            {
                _logger.Error(
                    ModuleId,
                    CleanupCategory,
                    message,
                    exception);
            }
            catch
            {
                // Logging must not replace resource operation failures.
            }
        }

        private async Task StopCoreAsync(
            InflightOperation[] inflight,
            IDisposable[] leases,
            TaskCompletionSource<bool> completion)
        {
            ExceptionDispatchInfo firstFailure = null;
            try
            {
                _lifetime.Cancel();
                for (var index = 0; index < leases.Length; index++)
                {
                    try
                    {
                        leases[index].Dispose();
                    }
                    catch (Exception exception)
                    {
                        if (firstFailure == null)
                        {
                            firstFailure =
                                ExceptionDispatchInfo.Capture(exception);
                        }
                    }
                }

                var cleanupTasks = new Task[inflight.Length];
                for (var index = 0; index < inflight.Length; index++)
                {
                    cleanupTasks[index] = BeginCleanup(inflight[index]);
                }

                await Task.WhenAll(cleanupTasks);
                if (firstFailure != null)
                {
                    firstFailure.Throw();
                }

                completion.TrySetResult(true);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private static async Task ApplyStopCancellationAsync(
            Task stopTask,
            CancellationToken token)
        {
            await stopTask;
            token.ThrowIfCancellationRequested();
        }

        private IDisposable[] SnapshotLeaseOwners()
        {
            var owners = new IDisposable[_leases.Count];
            var index = 0;
            foreach (var lease in _leases.Values)
            {
                owners[index++] = _leaseOwners[lease.LeaseId];
            }

            return owners;
        }

        private void AddLease(
            long leaseId,
            ResourceLeaseKind kind,
            string keyOrLabel,
            Type assetType,
            DateTime createdUtc,
            IDisposable owner)
        {
            _leases.Add(
                leaseId,
                new ResourceLeaseDiagnostics(
                    leaseId,
                    kind,
                    keyOrLabel,
                    assetType,
                    createdUtc));
            _leaseOwners.Add(leaseId, owner);
        }

        private void ReleaseLease(long leaseId, Action releaseBackend)
        {
            RemoveLease(leaseId);
            releaseBackend();
        }

        private long NextLeaseId()
        {
            return ++_nextLeaseId;
        }

        private void EnsureRunning()
        {
            lock (_sync)
            {
                EnsureRunningNoLock();
            }
        }

        private void EnsureRunningNoLock()
        {
            if (_stopped)
            {
                throw new ObjectDisposedException(nameof(ResourceService));
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

        private abstract class InflightOperation
        {
            private readonly ReleaseCallbackState _releaseState;

            protected InflightOperation(long id, Task task, Action release)
            {
                Id = id;
                Task = task ?? throw new ArgumentNullException(nameof(task));
                _releaseState = new ReleaseCallbackState(release);
            }

            public long Id { get; }

            public Task Task { get; }

            public Task CleanupTask { get; set; }

            public void AttachRelease(Action release)
            {
                _releaseState.Attach(release);
            }

            public void Release()
            {
                _releaseState.Release();
            }
        }

        private sealed class InflightOperation<T> : InflightOperation
        {
            public InflightOperation(
                long id,
                IResourceOperation<T> operation)
                : base(id, operation.Task, operation.Release)
            {
                TypedTask = operation.Task;
            }

            public Task<T> TypedTask { get; }
        }

        private sealed class SharedReleaseState
        {
            private int _remaining;
            private Action _release;

            public SharedReleaseState(int count, Action release)
            {
                _remaining = count;
                _release = release ??
                    throw new ArgumentNullException(nameof(release));
            }

            public void ReleaseOne()
            {
                if (Interlocked.Decrement(ref _remaining) == 0)
                {
                    Interlocked.Exchange(ref _release, null)?.Invoke();
                }
            }
        }

    }

    internal sealed class ReleaseCallbackState
    {
        private Action _release;

        public ReleaseCallbackState(Action release)
        {
            _release = release ??
                throw new ArgumentNullException(nameof(release));
        }

        public void Attach(Action release)
        {
            if (release == null)
            {
                throw new ArgumentNullException(nameof(release));
            }

            while (true)
            {
                var current = Volatile.Read(ref _release);
                if (current == null)
                {
                    release();
                    return;
                }

                Action combined = () =>
                {
                    try
                    {
                        current();
                    }
                    finally
                    {
                        release();
                    }
                };
                if (ReferenceEquals(
                        Interlocked.CompareExchange(
                            ref _release,
                            combined,
                            current),
                        current))
                {
                    return;
                }
            }
        }

        public void Release()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}
