using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework.Samples
{
    public sealed class BootstrapProcedure : ProcedureBase
    {
        public override string Id => SampleContent.BootstrapProcedureId;

        public override async ValueTask EnterAsync(
            ProcedureContext context,
            CancellationToken token)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var config = context.Resolve<IConfigService>();
            await config.ReloadAsync(token);
            if (!config.TryGet<GameplayConfig>(
                    SampleContent.GameplayConfigKey,
                    out var payload) ||
                payload == null)
            {
                throw new InvalidOperationException(
                    "The sample gameplay config was not loaded.");
            }
        }

        public override ValueTask ExitAsync(
            ProcedureContext context,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return default;
        }
    }
}
