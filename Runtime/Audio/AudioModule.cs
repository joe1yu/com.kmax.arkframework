using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class AudioModule : IFrameworkModule
    {
        private static readonly IReadOnlyCollection<string> AudioDependencies =
            Array.AsReadOnly(
                new[] { BuiltInModuleIds.Resource, BuiltInModuleIds.Pool });
        private AudioService _service;
        private bool _disposed;

        public string Id => BuiltInModuleIds.Audio;

        public IReadOnlyCollection<string> Dependencies => AudioDependencies;

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
                throw new ObjectDisposedException(nameof(AudioModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "AudioModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var resources = context.Services.Resolve<IResourceService>();
            var service = new AudioService(resources);
            try
            {
                context.ModuleScope.Own(service);
                context.ModuleScope.RegisterInstance<IAudioService>(service);
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
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AudioModule));
            }

            if (_service == null)
            {
                throw new InvalidOperationException(
                    "AudioModule must be initialized before it is started.");
            }

            token.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask StopAsync(CancellationToken token)
        {
            return _service?.StopAsync(token) ?? default;
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
    }
}
