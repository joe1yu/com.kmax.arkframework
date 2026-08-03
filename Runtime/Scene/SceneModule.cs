using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class SceneModule : IFrameworkModule
    {
        private static readonly IReadOnlyCollection<string> SceneDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Resource,
                    BuiltInModuleIds.EventBus
                });
        private SceneService _service;
        private bool _disposed;

        public string Id => BuiltInModuleIds.Scene;

        public IReadOnlyCollection<string> Dependencies => SceneDependencies;

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
                throw new ObjectDisposedException(nameof(SceneModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "SceneModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var resources =
                context.Services.Resolve<ISceneResourceLoader>();
            var events = context.Services.Resolve<IEventBus>();
            var service = new SceneService(
                new ResourceSceneBackend(resources),
                events);
            context.ModuleScope.Own(service);
            context.ModuleScope.RegisterInstance<ISceneService>(service);
            _service = service;
            return default;
        }

        public ValueTask StartAsync(CancellationToken token)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SceneModule));
            }

            if (_service == null)
            {
                throw new InvalidOperationException(
                    "SceneModule must be initialized before it is started.");
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
