using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class ResourceModule : IFrameworkModule
    {
        private static readonly IReadOnlyCollection<string> NoDependencies =
            Array.Empty<string>();

        private ResourceService _service;
        private bool _disposed;

        public string Id => BuiltInModuleIds.Resource;

        public IReadOnlyCollection<string> Dependencies => NoDependencies;

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
                throw new ObjectDisposedException(nameof(ResourceModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "ResourceModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var service = new ResourceService(
                new AddressablesResourceBackend(),
                context.Logger);
            context.ModuleScope.Own(service);
            context.ModuleScope.RegisterInstance<IResourceService>(service);
            context.ModuleScope.RegisterInstance<ISceneResourceLoader>(service);
            context.ModuleScope.RegisterInstance<
                ISceneTransactionResourceLoader>(service);
            _service = service;
            return default;
        }

        public ValueTask StartAsync(CancellationToken token)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ResourceModule));
            }

            if (_service == null)
            {
                throw new InvalidOperationException(
                    "ResourceModule must be initialized before it is started.");
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
