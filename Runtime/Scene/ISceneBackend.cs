using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    internal interface ISceneBackendScene
    {
        string Name { get; }

        ResourceKey Key { get; }

        bool IsOwned { get; }
    }

    internal interface ISceneBackend
    {
        ISceneBackendScene CaptureActiveScene();

        ValueTask<ISceneBackendScene> LoadAsync(
            ResourceKey key,
            Action<float> progress,
            CancellationToken token);

        ValueTask ActivateAsync(
            ISceneBackendScene scene,
            CancellationToken token);

        void SetActiveScene(ISceneBackendScene scene);

        ValueTask UnloadAsync(
            ISceneBackendScene scene,
            CancellationToken token);
    }
}
