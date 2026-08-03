using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;

namespace ArkFramework
{
    public interface IResourceOperation<T>
    {
        Task<T> Task { get; }

        void Release();
    }

    public interface IResourceBackend
    {
        IResourceOperation<T> LoadAssetAsync<T>(ResourceKey key)
            where T : Object;

        IResourceOperation<GameObject> InstantiateAsync(
            ResourceKey key,
            Transform parent);

        IResourceOperation<IReadOnlyList<T>> LoadByLabelAsync<T>(string label)
            where T : Object;

        IResourceOperation<SceneInstance> LoadSceneAsync(
            ResourceKey key,
            LoadSceneMode mode,
            bool activateOnLoad);

        IResourceOperation<SceneInstance> UnloadSceneAsync(SceneInstance scene);
    }
}
