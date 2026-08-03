using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class ActionKitModule : IFrameworkModule, IUpdateModule
    {
        private ActionService _service;
        private bool _started;
        private bool _disposed;

        public string Id => BuiltInModuleIds.ActionKit;

        public IReadOnlyCollection<string> Dependencies => Array.Empty<string>();

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
                throw new ObjectDisposedException(nameof(ActionKitModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "ActionKitModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var service = new ActionService(context.Logger);
            try
            {
                context.ModuleScope.RegisterInstance<IActionService>(service);
                _service = service;
            }
            catch
            {
                service.Dispose();
                throw;
            }

            return default;
        }

        public ValueTask StartAsync(CancellationToken token)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ActionKitModule));
            }

            if (_service == null)
            {
                throw new InvalidOperationException(
                    "ActionKitModule must be initialized before it is started.");
            }

            token.ThrowIfCancellationRequested();
            _started = true;
            return default;
        }

        public ValueTask StopAsync(CancellationToken token)
        {
            _started = false;
            _service?.Dispose();
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
                _service.Update(deltaTime);
            }
        }
    }
}
