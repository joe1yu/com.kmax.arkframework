using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class PoolModule : IFrameworkModule
    {
        private static readonly IReadOnlyCollection<string> PoolDependencies =
            Array.AsReadOnly(new[] { BuiltInModuleIds.Resource });
        private GameObjectPool _pool;
        private bool _disposed;

        public string Id => BuiltInModuleIds.Pool;

        public IReadOnlyCollection<string> Dependencies => PoolDependencies;

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
                throw new ObjectDisposedException(nameof(PoolModule));
            }

            if (_pool != null)
            {
                throw new InvalidOperationException(
                    "PoolModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var resourceService =
                context.Services.Resolve<IResourceService>();
            var pool = new GameObjectPool(resourceService);
            context.ModuleScope.RegisterInstance<IGameObjectPool>(pool);
            _pool = pool;
            return default;
        }

        public ValueTask StartAsync(CancellationToken token)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PoolModule));
            }

            if (_pool == null)
            {
                throw new InvalidOperationException(
                    "PoolModule must be initialized before it is started.");
            }

            token.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask StopAsync(CancellationToken token)
        {
            _pool?.ClearAll();
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
            _pool = null;
            return default;
        }
    }
}
