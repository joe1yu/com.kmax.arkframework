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
                    BuiltInModuleIds.EventBus,
                    BuiltInModuleIds.Table
                });
        private readonly string _sceneTablePath;
        private SceneService _service;
        private bool _disposed;

        public SceneModule(string sceneTablePath = null)
        {
            _sceneTablePath = string.IsNullOrWhiteSpace(sceneTablePath)
                ? string.Empty
                : sceneTablePath.Trim();
        }

        public string Id => BuiltInModuleIds.Scene;

        public IReadOnlyCollection<string> Dependencies => SceneDependencies;

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
            TableData<SceneTableRow> catalog = null;
            if (!string.IsNullOrEmpty(_sceneTablePath))
            {
                catalog = await context.Services.Resolve<ITableService>()
                    .LoadAsync<SceneTableRow>(
                        _sceneTablePath,
                        token: token);
            }

            var service = SceneService.CreateWithCatalog(
                new ResourceSceneBackend(resources),
                events,
                catalog);
            context.ModuleScope.Own(service);
            context.ModuleScope.RegisterInstance<ISceneService>(service);
            _service = service;
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
