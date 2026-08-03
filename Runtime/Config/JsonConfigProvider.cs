using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace ArkFramework
{
    public sealed class JsonConfigProvider : IConfigProvider
    {
        public const string DefaultName = "Json";

        private readonly IResourceService _resources;
        private readonly ResourceKey _manifestKey;

        public JsonConfigProvider(
            IResourceService resources,
            ResourceKey manifestKey,
            string name = DefaultName)
        {
            _resources =
                resources ?? throw new ArgumentNullException(nameof(resources));
            if (string.IsNullOrWhiteSpace(manifestKey.Value))
            {
                throw new ArgumentException(
                    "A JSON config manifest key is required.",
                    nameof(manifestKey));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    "A config provider name is required.",
                    nameof(name));
            }

            _manifestKey = manifestKey;
            Name = name;
        }

        public string Name { get; }

        public ValueTask<ConfigProviderSnapshot> LoadAsync(
            CancellationToken token = default)
        {
            return new ValueTask<ConfigProviderSnapshot>(LoadCoreAsync(token));
        }

        private async Task<ConfigProviderSnapshot> LoadCoreAsync(
            CancellationToken token)
        {
            var owners = new List<IDisposable>();
            try
            {
                var manifestLease =
                    await _resources.LoadAsync<TextAsset>(_manifestKey, token);
                owners.Add(manifestLease);
                var manifestAsset = manifestLease.Asset;
                if (manifestAsset == null)
                {
                    throw new InvalidOperationException(
                        "The JSON config manifest asset is null.");
                }

                var manifest =
                    JsonConvert.DeserializeObject<JsonConfigManifest>(
                        manifestAsset.text);
                if (manifest == null || manifest.Entries == null)
                {
                    throw new InvalidOperationException(
                        "The JSON config manifest must contain an entries array.");
                }

                var entries =
                    new List<ConfigEntry>(manifest.Entries.Count);
                for (var index = 0;
                     index < manifest.Entries.Count;
                     index++)
                {
                    token.ThrowIfCancellationRequested();
                    var manifestEntry = manifest.Entries[index];
                    if (manifestEntry == null)
                    {
                        throw new InvalidOperationException(
                            $"JSON config manifest entry {index} is null.");
                    }

                    if (string.IsNullOrWhiteSpace(manifestEntry.Type))
                    {
                        throw new ArgumentException(
                            $"JSON config manifest entry {index} has no type.");
                    }

                    var configType = Type.GetType(
                        manifestEntry.Type,
                        throwOnError: false);
                    if (configType == null)
                    {
                        throw new InvalidOperationException(
                            $"JSON config type '{manifestEntry.Type}' could not " +
                            "be resolved.");
                    }

                    var key = new ConfigKey(configType, manifestEntry.Key);
                    if (manifestEntry.Version == null)
                    {
                        throw new ArgumentNullException(
                            nameof(manifestEntry.Version),
                            $"JSON config '{key}' has a null version.");
                    }

                    if (string.IsNullOrWhiteSpace(manifestEntry.Address))
                    {
                        throw new ArgumentException(
                            $"JSON config '{key}' has an invalid address.");
                    }

                    var payloadLease =
                        await _resources.LoadAsync<TextAsset>(
                            new ResourceKey(manifestEntry.Address),
                            token);
                    owners.Add(payloadLease);
                    var payloadAsset = payloadLease.Asset;
                    if (payloadAsset == null)
                    {
                        throw new InvalidOperationException(
                            $"JSON config asset for '{key}' is null.");
                    }

                    var payload = JsonConvert.DeserializeObject(
                        payloadAsset.text,
                        configType);
                    if (payload == null)
                    {
                        throw new InvalidOperationException(
                            $"JSON config payload for '{key}' is null.");
                    }

                    entries.Add(
                        new ConfigEntry(
                            key,
                            payload,
                            Name,
                            manifestEntry.Version,
                            payloadLease));
                }

                return new ConfigProviderSnapshot(entries, owners);
            }
            catch (Exception primary)
            {
                var cleanup = ConfigCleanup.DisposeAll(owners);
                ConfigCleanup.ThrowPrimaryWithCleanup(primary, cleanup);
                throw;
            }
        }
    }
}
