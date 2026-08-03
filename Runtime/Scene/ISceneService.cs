using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public interface ISceneService : IAsyncDisposable
    {
        ValueTask LoadAsync(
            SceneRequest request,
            CancellationToken token = default);

        ResourceKey ActiveSceneKey { get; }

        string ActiveSceneName { get; }

        bool IsTransitioning { get; }

        int QueueLength { get; }

        SceneDiagnostics Diagnostics { get; }

        ValueTask StopAsync(CancellationToken token = default);
    }
}
