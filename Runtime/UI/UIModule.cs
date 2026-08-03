using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class UIModule : IFrameworkModule
    {
        private static readonly IReadOnlyCollection<string> UIDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Resource,
                    BuiltInModuleIds.Pool,
                    BuiltInModuleIds.EventBus
                });
        private UIService _service;
        private bool _disposed;

        public string Id => BuiltInModuleIds.UI;

        public IReadOnlyCollection<string> Dependencies => UIDependencies;

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
                throw new ObjectDisposedException(nameof(UIModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "UIModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var resources =
                context.Services.Resolve<IResourceService>();
            var pool = context.Services.Resolve<IGameObjectPool>();
            var events = context.Services.Resolve<IEventBus>();
            var root = UIRoot.Create();
            var service = new UIService(
                resources,
                pool,
                events,
                root);
            try
            {
                context.ModuleScope.Own(service);
                context.ModuleScope.RegisterInstance<IUIService>(service);
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
                throw new ObjectDisposedException(nameof(UIModule));
            }

            if (_service == null)
            {
                throw new InvalidOperationException(
                    "UIModule must be initialized before it is started.");
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
