using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class ProcedureModule : IFrameworkModule
    {
        private static readonly IReadOnlyCollection<string> ModuleDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Fsm,
                    BuiltInModuleIds.Config,
                    BuiltInModuleIds.Scene,
                    BuiltInModuleIds.UI,
                    BuiltInModuleIds.Audio
                });
        private ProcedureService _service;
        private bool _disposed;

        public string Id => BuiltInModuleIds.Procedure;
        public IReadOnlyCollection<string> Dependencies => ModuleDependencies;

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
                throw new ObjectDisposedException(nameof(ProcedureModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "ProcedureModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var fsmService = context.Services.Resolve<IFsmService>();
            var service = new ProcedureService(
                fsmService,
                context.Services);
            try
            {
                context.ModuleScope.RegisterInstance<IProcedureService>(
                    service);
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
            EnsureInitialized();
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

        private void EnsureInitialized()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ProcedureModule));
            }

            if (_service == null)
            {
                throw new InvalidOperationException(
                    "ProcedureModule must be initialized before it is started.");
            }
        }
    }
}
