using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class TableModule : IFrameworkModule
    {
        private TableService _service;
        private bool _disposed;

        public string Id => BuiltInModuleIds.Table;

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
                throw new ObjectDisposedException(nameof(TableModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "TableModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var service = new TableService();
            context.ModuleScope.Own(service);
            context.ModuleScope.RegisterInstance<ITableService>(service);
            _service = service;
            return default;
        }

        public ValueTask StartAsync(CancellationToken token)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TableModule));
            }

            if (_service == null)
            {
                throw new InvalidOperationException(
                    "TableModule must be initialized before it is started.");
            }

            token.ThrowIfCancellationRequested();
            return default;
        }

        public ValueTask StopAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _service?.Clear();
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
    }
}
