using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework.Samples
{
    public sealed class SampleProcedureModule : IFrameworkModule
    {
        private static readonly IReadOnlyCollection<string> ModuleDependencies =
            Array.AsReadOnly(
                new[]
                {
                    BuiltInModuleIds.Fsm,
                    BuiltInModuleIds.Config,
                    BuiltInModuleIds.Table,
                    BuiltInModuleIds.Scene,
                    BuiltInModuleIds.Rig,
                    BuiltInModuleIds.UI,
                    BuiltInModuleIds.Audio,
                    BuiltInModuleIds.ActionKit
                });

        private ProcedureService _service;
        private SampleFlowController _flow;
        private bool _disposed;

        public string Id => BuiltInModuleIds.Procedure;

        public IReadOnlyCollection<string> Dependencies =>
            ModuleDependencies;

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
                throw new ObjectDisposedException(
                    nameof(SampleProcedureModule));
            }

            if (_service != null)
            {
                throw new InvalidOperationException(
                    "SampleProcedureModule has already been initialized.");
            }

            token.ThrowIfCancellationRequested();
            var flow = new SampleFlowController();
            var service = new ProcedureService(
                context.Services.Resolve<IFsmService>(),
                context.Services);
            try
            {
                service.Register(new BootstrapProcedure());
                service.Register(new MainMenuProcedure(flow));
                service.Register(new GameplayProcedure(flow));

                var ui = context.Services.Resolve<IUIService>();
                var table = await context.Services.Resolve<ITableService>()
                    .LoadAsync<SampleUIRow>(
                        SampleContent.UITablePath,
                        token: token);
                var sampleUI = new SampleUIService(ui, table);

                context.ModuleScope.RegisterInstance<IProcedureService>(
                    service);
                context.ModuleScope.RegisterInstance<ISampleFlow>(flow);
                context.ModuleScope.RegisterInstance<ISampleUIService>(
                    sampleUI);
                _service = service;
                _flow = flow;
            }
            catch
            {
                await service.DisposeAsync();
                throw;
            }
        }

        public async ValueTask StartAsync(CancellationToken token)
        {
            EnsureInitialized();
            await _service.StartAsync(
                SampleContent.BootstrapProcedureId,
                token);
            await _service.ChangeAsync(
                SampleContent.MainMenuProcedureId,
                token);
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
            _flow = null;
            return default;
        }

        private void EnsureInitialized()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(SampleProcedureModule));
            }

            if (_service == null || _flow == null)
            {
                throw new InvalidOperationException(
                    "SampleProcedureModule must be initialized before start.");
            }
        }
    }
}
