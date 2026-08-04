using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class RigModule : IFrameworkModule
    {
        private static readonly IReadOnlyCollection<string> RigDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Platform,
                    BuiltInModuleIds.EventBus
                });
        private RigService _service;
        private bool _disposed;

        public string Id => BuiltInModuleIds.Rig;

        public IReadOnlyCollection<string> Dependencies => RigDependencies;

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
                throw new ObjectDisposedException(nameof(RigModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "RigModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var service = new RigService(
                context.Services.Resolve<IPlatformService>());
            context.ModuleScope.RegisterInstance<IRigService>(service);
            context.Services.Resolve<IEventBus>()
                .Subscribe<SceneTransitionEvent>(
                    context.ModuleScope,
                    service.HandleSceneTransition);
            _service = service;
            return default;
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
                throw new ObjectDisposedException(nameof(RigModule));
            }

            if (_service == null)
            {
                throw new InvalidOperationException(
                    "RigModule must be initialized before start.");
            }
        }
    }
}
