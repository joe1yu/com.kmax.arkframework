using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.ResourceManagement.ResourceProviders;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ArkFramework.Tests
{
    public sealed class ResourceContractTests
    {
        [Test]
        public void AlternativeServiceCanImplementAllPublicLeaseContracts()
        {
            var service = new AlternativeResourceService();
            IResourceService resources = service;
            ISceneResourceLoader scenes = service;
            ISceneTransactionResourceLoader transactions = service;

            var asset = resources
                .LoadAsync<TextAsset>(new ResourceKey("asset"))
                .AsTask()
                .GetAwaiter()
                .GetResult();
            var instance = resources
                .InstantiateAsync(new ResourceKey("instance"))
                .AsTask()
                .GetAwaiter()
                .GetResult();
            var scene = scenes
                .LoadSceneAsync(
                    new ResourceKey("scene"),
                    LoadSceneMode.Additive,
                    activateOnLoad: false)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            var transaction = transactions
                .LoadSceneTransactionAsync(
                    new ResourceKey("transaction"),
                    LoadSceneMode.Additive,
                    activateOnLoad: false)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            Assert.That(asset.Key.Value, Is.EqualTo("asset"));
            Assert.That(instance.Key.Value, Is.EqualTo("instance"));
            Assert.That(scene.Key.Value, Is.EqualTo("scene"));
            Assert.That(transaction.Key.Value, Is.EqualTo("transaction"));
            Assert.DoesNotThrow(
                () => scenes
                    .UnloadSceneAsync(scene)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult());
            Assert.That(service.LastUnloadedScene, Is.SameAs(scene));
        }

        private sealed class AlternativeResourceService :
            IResourceService,
            ISceneResourceLoader,
            ISceneTransactionResourceLoader
        {
            public ISceneLease LastUnloadedScene { get; private set; }

            public ResourceDiagnostics Diagnostics => null;

            public ValueTask<IAssetLease<T>> LoadAsync<T>(
                ResourceKey key,
                CancellationToken token = default)
                where T : Object
            {
                return new ValueTask<IAssetLease<T>>(
                    new FakeAssetLease<T>(key));
            }

            public ValueTask<IInstanceLease> InstantiateAsync(
                ResourceKey key,
                Transform parent = null,
                CancellationToken token = default)
            {
                return new ValueTask<IInstanceLease>(
                    new FakeInstanceLease(key));
            }

            public ValueTask<IReadOnlyList<IAssetLease<T>>> LoadByLabelAsync<T>(
                string label,
                CancellationToken token = default)
                where T : Object
            {
                IReadOnlyList<IAssetLease<T>> leases =
                    Array.AsReadOnly(
                        new IAssetLease<T>[]
                        {
                            new FakeAssetLease<T>(
                                default,
                                label)
                        });
                return new ValueTask<IReadOnlyList<IAssetLease<T>>>(leases);
            }

            public ValueTask<ISceneLease> LoadSceneAsync(
                ResourceKey key,
                LoadSceneMode mode,
                bool activateOnLoad,
                CancellationToken token = default)
            {
                return new ValueTask<ISceneLease>(new FakeSceneLease(key));
            }

            public ValueTask UnloadSceneAsync(
                ISceneLease lease,
                CancellationToken token = default)
            {
                LastUnloadedScene = lease;
                return default;
            }

            public ValueTask<ISceneLease> LoadSceneTransactionAsync(
                ResourceKey key,
                LoadSceneMode mode,
                bool activateOnLoad,
                CancellationToken token = default)
            {
                return new ValueTask<ISceneLease>(new FakeSceneLease(key));
            }
        }

        private sealed class FakeAssetLease<T> : IAssetLease<T>
            where T : Object
        {
            public FakeAssetLease(ResourceKey key, string label = null)
            {
                Key = key;
                Label = label;
                CreatedUtc = DateTime.UtcNow;
            }

            public long LeaseId => 1;
            public ResourceKey Key { get; }
            public string Label { get; }
            public string KeyOrLabel => Label ?? Key.Value ?? string.Empty;
            public DateTime CreatedUtc { get; }
            public T Asset => null;
            public void Dispose()
            {
            }
        }

        private sealed class FakeInstanceLease : IInstanceLease
        {
            public FakeInstanceLease(ResourceKey key)
            {
                Key = key;
                CreatedUtc = DateTime.UtcNow;
            }

            public long LeaseId => 2;
            public ResourceKey Key { get; }
            public DateTime CreatedUtc { get; }
            public GameObject Instance => null;
            public void Dispose()
            {
            }
        }

        private sealed class FakeSceneLease : ISceneLease
        {
            public FakeSceneLease(ResourceKey key)
            {
                Key = key;
                CreatedUtc = DateTime.UtcNow;
            }

            public long LeaseId => 3;
            public ResourceKey Key { get; }
            public DateTime CreatedUtc { get; }
            public SceneInstance Scene => default;
            public void Dispose()
            {
            }
        }
    }
}
