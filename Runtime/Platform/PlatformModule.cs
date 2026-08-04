using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ArkFramework
{
    public sealed class PlatformModule : IFrameworkModule
    {
        private static readonly IReadOnlyCollection<string> NoDependencies =
            Array.Empty<string>();

        private readonly GameObject _platformPrefab;
        private readonly bool _dontDestroyOnLoad;
        private PlatformService _service;
        private bool _disposed;

        public PlatformModule(
            GameObject platformPrefab = null,
            bool dontDestroyOnLoad = true)
        {
            _platformPrefab = platformPrefab;
            _dontDestroyOnLoad = dontDestroyOnLoad;
        }

        public string Id => BuiltInModuleIds.Platform;

        public IReadOnlyCollection<string> Dependencies => NoDependencies;

        public async ValueTask InitializeAsync(
            ModuleContext context,
            CancellationToken token)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PlatformModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "PlatformModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var service = new PlatformService(
                _platformPrefab,
                _dontDestroyOnLoad);
            try
            {
                context.ModuleScope.RegisterInstance<IPlatformService>(
                    service);
                _service = service;
            }
            catch
            {
                await service.DisposeAsync();
                throw;
            }
        }

        public ValueTask StartAsync(CancellationToken token)
        {
            EnsureInitialized();
            token.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask StopAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return default;
            }

            _disposed = true;
            _service = null;
            return default;
        }

        private void EnsureInitialized()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PlatformModule));
            }

            if (_service == null)
            {
                throw new InvalidOperationException(
                    "PlatformModule must be initialized before start.");
            }
        }
    }
}
