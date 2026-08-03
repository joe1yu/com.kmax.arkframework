using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public sealed class AddressablesResourceBackend : IResourceBackend
    {
        private readonly object _sceneSync = new object();
        private readonly Dictionary<SceneInstance, SceneLoadOperation>
            _sceneLoads =
                new Dictionary<SceneInstance, SceneLoadOperation>();

        public IResourceOperation<T> LoadAssetAsync<T>(ResourceKey key)
            where T : Object
        {
            return new HandleOperation<T>(
                Addressables.LoadAssetAsync<T>(key.Value),
                Addressables.Release);
        }

        public IResourceOperation<GameObject> InstantiateAsync(
            ResourceKey key,
            Transform parent)
        {
            return new HandleOperation<GameObject>(
                Addressables.InstantiateAsync(key.Value, parent),
                handle => Addressables.ReleaseInstance(handle));
        }

        public IResourceOperation<IReadOnlyList<T>> LoadByLabelAsync<T>(
            string label)
            where T : Object
        {
            return new LabelOperation<T>(
                Addressables.LoadAssetsAsync<T>(
                    label,
                    null,
                    releaseDependenciesOnFailure: false));
        }

        public IResourceOperation<SceneInstance> LoadSceneAsync(
            ResourceKey key,
            LoadSceneMode mode,
            bool activateOnLoad)
        {
            return new SceneLoadOperation(
                this,
                Addressables.LoadSceneAsync(
                    key.Value,
                    mode,
                    activateOnLoad));
        }

        public IResourceOperation<SceneInstance> UnloadSceneAsync(
            SceneInstance scene)
        {
            SceneLoadOperation loadOperation;
            lock (_sceneSync)
            {
                _sceneLoads.TryGetValue(scene, out loadOperation);
            }

            loadOperation?.TransferHandleToUnload();
            try
            {
                return new SceneUnloadOperation(
                    Addressables.UnloadSceneAsync(
                        scene,
                        autoReleaseHandle: false),
                    loadOperation);
            }
            catch
            {
                loadOperation?.UndoHandleTransfer();
                throw;
            }
        }

        private sealed class SceneUnloadOperation :
            IResourceOperation<SceneInstance>
        {
            private readonly AsyncOperationHandle<SceneInstance> _handle;
            private readonly SceneLoadOperation _loadOperation;
            private int _released;

            public SceneUnloadOperation(
                AsyncOperationHandle<SceneInstance> handle,
                SceneLoadOperation loadOperation)
            {
                _handle = handle;
                _loadOperation = loadOperation;
                Task = ObserveWithRollbackAsync();
            }

            public Task<SceneInstance> Task { get; }

            public void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0 &&
                    _handle.IsValid())
                {
                    Addressables.Release(_handle);
                }
            }

            private async Task<SceneInstance> ObserveWithRollbackAsync()
            {
                try
                {
                    return await ObserveAsync(_handle);
                }
                catch
                {
                    _loadOperation?.UndoHandleTransfer();
                    throw;
                }
            }
        }

        private void TrackScene(
            SceneInstance scene,
            SceneLoadOperation operation)
        {
            lock (_sceneSync)
            {
                _sceneLoads[scene] = operation;
            }
        }

        private void UntrackScene(
            SceneInstance scene,
            SceneLoadOperation operation)
        {
            lock (_sceneSync)
            {
                if (_sceneLoads.TryGetValue(scene, out var current) &&
                    ReferenceEquals(current, operation))
                {
                    _sceneLoads.Remove(scene);
                }
            }
        }

        private sealed class HandleOperation<T> : IResourceOperation<T>
        {
            private readonly AsyncOperationHandle<T> _handle;
            private Action<AsyncOperationHandle<T>> _release;

            public HandleOperation(
                AsyncOperationHandle<T> handle,
                Action<AsyncOperationHandle<T>> release)
            {
                _handle = handle;
                _release = release ??
                    throw new ArgumentNullException(nameof(release));
                Task = ObserveAsync(handle);
            }

            public Task<T> Task { get; }

            public void Release()
            {
                var release = Interlocked.Exchange(ref _release, null);
                if (release != null && _handle.IsValid())
                {
                    release(_handle);
                }
            }
        }

        private sealed class LabelOperation<T> :
            IResourceOperation<IReadOnlyList<T>>
            where T : Object
        {
            private readonly AsyncOperationHandle<IList<T>> _handle;
            private int _released;

            public LabelOperation(AsyncOperationHandle<IList<T>> handle)
            {
                _handle = handle;
                Task = ConvertAsync(ObserveAsync(handle));
            }

            public Task<IReadOnlyList<T>> Task { get; }

            public void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0 &&
                    _handle.IsValid())
                {
                    Addressables.Release(_handle);
                }
            }

            private static async Task<IReadOnlyList<T>> ConvertAsync(
                Task<IList<T>> task)
            {
                var assets = await task;
                if (assets is IReadOnlyList<T> readOnly)
                {
                    return readOnly;
                }

                return new List<T>(assets).AsReadOnly();
            }
        }

        private sealed class SceneLoadOperation :
            IResourceOperation<SceneInstance>
        {
            private readonly AddressablesResourceBackend _owner;
            private readonly AsyncOperationHandle<SceneInstance> _handle;
            private SceneInstance _scene;
            private int _tracked;
            private int _handleTransferred;
            private int _released;

            public SceneLoadOperation(
                AddressablesResourceBackend owner,
                AsyncOperationHandle<SceneInstance> handle)
            {
                _owner = owner ??
                    throw new ArgumentNullException(nameof(owner));
                _handle = handle;
                Task = TrackAsync(ObserveAsync(handle));
            }

            public Task<SceneInstance> Task { get; }

            public void TransferHandleToUnload()
            {
                if (Volatile.Read(ref _released) != 0)
                {
                    throw new ObjectDisposedException(
                        nameof(SceneLoadOperation));
                }

                Interlocked.Exchange(ref _handleTransferred, 1);
            }

            public void UndoHandleTransfer()
            {
                Interlocked.Exchange(ref _handleTransferred, 0);
            }

            public void Release()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0)
                {
                    return;
                }

                if (Volatile.Read(ref _tracked) != 0)
                {
                    _owner.UntrackScene(_scene, this);
                }

                if (Volatile.Read(ref _handleTransferred) == 0 &&
                    _handle.IsValid())
                {
                    Addressables.Release(_handle);
                }
            }

            private async Task<SceneInstance> TrackAsync(
                Task<SceneInstance> task)
            {
                var scene = await task;
                _scene = scene;
                Volatile.Write(ref _tracked, 1);
                _owner.TrackScene(scene, this);
                return scene;
            }
        }

        private static async Task<T> ObserveAsync<T>(
            AsyncOperationHandle<T> handle)
        {
            await handle.Task;
            if (handle.Status == AsyncOperationStatus.Succeeded)
            {
                return handle.Result;
            }

            var exception = handle.OperationException ??
                new InvalidOperationException(
                    $"Addressables operation '{handle.DebugName}' failed.");
            ExceptionDispatchInfo.Capture(exception).Throw();
            throw exception;
        }
    }
}
