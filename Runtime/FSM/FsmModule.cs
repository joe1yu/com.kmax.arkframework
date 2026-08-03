using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class FsmModule : IFrameworkModule, IUpdateModule
    {
        private FsmService _service;
        private bool _started;
        private bool _disposed;

        public string Id => BuiltInModuleIds.Fsm;

        public IReadOnlyCollection<string> Dependencies =>
            Array.Empty<string>();

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
                throw new ObjectDisposedException(nameof(FsmModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "FsmModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var service = new FsmService();
            context.ModuleScope.RegisterInstance<IFsmService>(service);
            _service = service;
            return default;
        }

        public ValueTask StartAsync(CancellationToken token)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(FsmModule));
            }

            if (_service == null)
            {
                throw new InvalidOperationException(
                    "FsmModule must be initialized before it is started.");
            }

            token.ThrowIfCancellationRequested();
            _started = true;
            return default;
        }

        public ValueTask StopAsync(CancellationToken token)
        {
            _started = false;
            var cleanup = _service?.DisposeAsync() ?? default;
            return token.IsCancellationRequested
                ? CompleteCanceledStopAsync(cleanup, token)
                : cleanup;
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

        private static async ValueTask CompleteCanceledStopAsync(
            ValueTask cleanup,
            CancellationToken token)
        {
            await cleanup;
            token.ThrowIfCancellationRequested();
        }
    }
}
