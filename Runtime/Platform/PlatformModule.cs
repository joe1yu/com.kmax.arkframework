using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ArkFramework
{
    public sealed class PlatformModule : IFrameworkModule, IUpdateModule
    {
        private static readonly IReadOnlyCollection<string> NoDependencies =
            Array.Empty<string>();

        private readonly GameObject _platformPrefab;
        private readonly bool _dontDestroyOnLoad;
        private PlatformService _service;
        private bool _started;
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
            _service.RefreshCanvases();
            _started = true;
            return default;
        }

        public ValueTask StopAsync(CancellationToken token)
        {
            _started = false;
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
            _started = false;
            _service = null;
            return default;
        }

        public void Update(float deltaTime)
        {
            if (_started)
            {
                _service.RefreshCanvases();
            }
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
