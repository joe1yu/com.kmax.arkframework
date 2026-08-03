using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ArkFramework
{
    public interface IResourceService
    {
        ValueTask<IAssetLease<T>> LoadAsync<T>(
            ResourceKey key,
            CancellationToken token = default)
            where T : Object;

        ValueTask<IInstanceLease> InstantiateAsync(
            ResourceKey key,
            Transform parent = null,
            CancellationToken token = default);

        ValueTask<IReadOnlyList<IAssetLease<T>>> LoadByLabelAsync<T>(
            string label,
            CancellationToken token = default)
            where T : Object;

        ResourceDiagnostics Diagnostics { get; }
    }

    public interface ISceneResourceLoader
    {
        ValueTask<ISceneLease> LoadSceneAsync(
            ResourceKey key,
            LoadSceneMode mode,
            bool activateOnLoad,
            CancellationToken token = default);

        ValueTask UnloadSceneAsync(
            ISceneLease lease,
            CancellationToken token = default);
    }

    public interface ISceneTransactionResourceLoader
    {
        ValueTask<ISceneLease> LoadSceneTransactionAsync(
            ResourceKey key,
            LoadSceneMode mode,
            bool activateOnLoad,
            CancellationToken token = default);
    }
}
