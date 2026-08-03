using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArkFramework
{
    internal sealed class ResourceSceneBackend : ISceneBackend
    {
        private readonly ISceneResourceLoader _resources;

        public ResourceSceneBackend(ISceneResourceLoader resources)
        {
            _resources = resources ??
                throw new ArgumentNullException(nameof(resources));
        }

        public ISceneBackendScene CaptureActiveScene()
        {
            return new BackendScene(
                SceneManager.GetActiveScene(),
                default,
                null);
        }

        public async ValueTask<ISceneBackendScene> LoadAsync(
            ResourceKey key,
            Action<float> progress,
            CancellationToken token)
        {
            if (progress == null)
            {
                throw new ArgumentNullException(nameof(progress));
            }

            progress(0f);
            ISceneLease lease;
            if (_resources is ISceneTransactionResourceLoader transaction)
            {
                lease = await transaction.LoadSceneTransactionAsync(
                    key,
                    UnityEngine.SceneManagement.LoadSceneMode.Additive,
                    false,
                    token);
            }
            else
            {
                lease = await _resources.LoadSceneAsync(
                    key,
                    UnityEngine.SceneManagement.LoadSceneMode.Additive,
                    false,
                    token);
            }

            progress(1f);
            return new BackendScene(lease.Scene.Scene, key, lease);
        }

        public async ValueTask ActivateAsync(
            ISceneBackendScene scene,
            CancellationToken token)
        {
            var backendScene = RequireBackendScene(scene);
            if (backendScene.Lease == null)
            {
                throw new InvalidOperationException(
                    "Only a service-owned scene can be activated.");
            }

            var operation = backendScene.Lease.Scene.ActivateAsync();
            await AwaitOperationAsync(operation, token);
        }

        public void SetActiveScene(ISceneBackendScene scene)
        {
            var backendScene = RequireBackendScene(scene);
            if (!SceneManager.SetActiveScene(backendScene.Scene))
            {
                throw new InvalidOperationException(
                    $"Unity rejected scene '{backendScene.Name}' as active.");
            }
        }

        public async ValueTask UnloadAsync(
            ISceneBackendScene scene,
            CancellationToken token)
        {
            var backendScene = RequireBackendScene(scene);
            if (backendScene.IsOwned)
            {
                var lease = backendScene.RequireLease();
                await _resources.UnloadSceneAsync(
                    lease,
                    token);
                backendScene.MarkUnloaded(lease);
                return;
            }

            if (!backendScene.Scene.IsValid() ||
                !backendScene.Scene.isLoaded)
            {
                return;
            }

            var operation = SceneManager.UnloadSceneAsync(
                backendScene.Scene);
            if (operation == null)
            {
                throw new InvalidOperationException(
                    $"Unity could not unload scene '{backendScene.Name}'.");
            }

            await AwaitOperationAsync(operation, token);
        }

        private static BackendScene RequireBackendScene(
            ISceneBackendScene scene)
        {
            if (!(scene is BackendScene backendScene))
            {
                throw new ArgumentException(
                    "The scene belongs to a different backend.",
                    nameof(scene));
            }

            return backendScene;
        }

        private static async Task AwaitOperationAsync(
            AsyncOperation operation,
            CancellationToken token)
        {
            if (operation.isDone)
            {
                token.ThrowIfCancellationRequested();
                return;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            operation.completed += _ => completion.TrySetResult(true);
            if (!token.CanBeCanceled)
            {
                await completion.Task;
                return;
            }

            var canceled = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(
                       () => canceled.TrySetResult(true)))
            {
                if (await Task.WhenAny(completion.Task, canceled.Task) !=
                    completion.Task)
                {
                    throw new OperationCanceledException(token);
                }
            }

            await completion.Task;
        }

        private sealed class BackendScene : ISceneBackendScene
        {
            private ISceneLease _lease;
            private readonly bool _owned;

            public BackendScene(
                UnityEngine.SceneManagement.Scene scene,
                ResourceKey key,
                ISceneLease lease)
            {
                Scene = scene;
                Key = key;
                _lease = lease;
                _owned = lease != null;
            }

            public UnityEngine.SceneManagement.Scene Scene { get; }

            public string Name => Scene.name;

            public ResourceKey Key { get; }

            public bool IsOwned => _owned;

            public ISceneLease Lease => _lease;

            public ISceneLease RequireLease()
            {
                var lease = Volatile.Read(ref _lease);
                if (lease == null)
                {
                    throw new ObjectDisposedException(nameof(BackendScene));
                }

                return lease;
            }

            public void MarkUnloaded(ISceneLease lease)
            {
                if (!ReferenceEquals(
                        Interlocked.CompareExchange(
                            ref _lease,
                            null,
                            lease),
                        lease))
                {
                    throw new InvalidOperationException(
                        "The owned scene lease changed during unload.");
                }
            }
        }
    }
}
