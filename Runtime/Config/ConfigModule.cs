using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class ConfigModule : IFrameworkModule
    {
        private static readonly IReadOnlyCollection<string> ConfigDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Resource,
                    BuiltInModuleIds.EventBus
                });
        private readonly string _scriptableObjectLabel;
        private readonly ResourceKey _jsonManifestKey;
        private ConfigService _service;
        private bool _started;
        private bool _disposed;

        public ConfigModule(
            string scriptableObjectLabel = "config-scriptable",
            ResourceKey jsonManifestKey = default)
        {
            if (string.IsNullOrWhiteSpace(scriptableObjectLabel))
            {
                throw new ArgumentException(
                    "A ScriptableObject config label is required.",
                    nameof(scriptableObjectLabel));
            }

            _scriptableObjectLabel = scriptableObjectLabel;
            _jsonManifestKey = string.IsNullOrWhiteSpace(jsonManifestKey.Value)
                ? new ResourceKey("config/manifest")
                : jsonManifestKey;
        }

        public string Id => BuiltInModuleIds.Config;

        public IReadOnlyCollection<string> Dependencies => ConfigDependencies;

        public ValueTask InitializeAsync(
            ModuleContext context,
            CancellationToken token)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ConfigModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "ConfigModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var resources = context.Services.Resolve<IResourceService>();
            var eventBus = context.Services.Resolve<IEventBus>();
            var providers = new IConfigProvider[]
            {
                new ScriptableObjectConfigProvider(
                    resources,
                    _scriptableObjectLabel),
                new JsonConfigProvider(resources, _jsonManifestKey)
            };
            var service = new ConfigService(
                providers,
                eventBus,
                context.Logger);
            context.ModuleScope.Own(service);
            context.ModuleScope.RegisterInstance<IConfigService>(service);
            _service = service;
            return default;
        }

        public ValueTask StartAsync(CancellationToken token)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ConfigModule));
            }

            if (_service == null)
            {
                throw new InvalidOperationException(
                    "ConfigModule must be initialized before it is started.");
            }

            token.ThrowIfCancellationRequested();
            _started = true;
            return default;
        }

        public ValueTask StopAsync(CancellationToken token)
        {
            _started = false;
            return _service?.StopAsync(token) ?? default;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return default;
            }

            _disposed = true;
            _started = false;
            _service = null;
            return default;
        }
    }
}
