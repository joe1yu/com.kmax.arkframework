using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class ScriptableObjectConfigProvider : IConfigProvider
    {
        public const string DefaultName = "ScriptableObject";

        private readonly IResourceService _resources;
        private readonly string _label;

        public ScriptableObjectConfigProvider(
            IResourceService resources,
            string label,
            string name = DefaultName)
        {
            _resources =
                resources ?? throw new ArgumentNullException(nameof(resources));
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new ArgumentException(
                    "A ScriptableObject config label is required.",
                    nameof(label));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException(
                    "A config provider name is required.",
                    nameof(name));
            }

            _label = label;
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
                var leases =
                    await _resources
                        .LoadByLabelAsync<ScriptableObjectConfigAsset>(
                            _label,
                            token);
                if (leases == null)
                {
                    throw new InvalidOperationException(
                        "The resource service returned a null config label result.");
                }

                for (var index = 0; index < leases.Count; index++)
                {
                    var lease = leases[index];
                    if (lease != null)
                    {
                        owners.Add(lease);
                    }
                }

                var entries = new List<ConfigEntry>(leases.Count);
                for (var index = 0; index < leases.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var lease = leases[index];
                    if (lease == null)
                    {
                        throw new InvalidOperationException(
                            "The resource service returned a null config lease.");
                    }

                    var asset = lease.Asset;
                    if (asset == null)
                    {
                        throw new InvalidOperationException(
                            "A ScriptableObject config asset is null.");
                    }

                    var payloadType = asset.PayloadType;
                    if (payloadType == null)
                    {
                        throw new InvalidOperationException(
                            "A ScriptableObject config payload type is null.");
                    }

                    var key = new ConfigKey(payloadType, asset.Key);
                    if (asset.Version == null)
                    {
                        throw new ArgumentNullException(
                            nameof(asset.Version),
                            $"Config asset '{key}' has a null version.");
                    }

                    var payload = asset.GetPayload();
                    entries.Add(
                        new ConfigEntry(
                            key,
                            payload,
                            Name,
                            asset.Version,
                            lease));
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
