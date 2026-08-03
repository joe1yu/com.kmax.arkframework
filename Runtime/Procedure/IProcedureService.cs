using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public interface IProcedureService : IAsyncDisposable
    {
        string CurrentProcedureId { get; }
        string PreviousProcedureId { get; }
        bool IsStarted { get; }
        bool IsFaulted { get; }
        ProcedureDiagnostics Diagnostics { get; }

        void Register(ProcedureBase procedure);
        ValueTask StartAsync(
            string initialId,
            CancellationToken token = default);
        ValueTask ChangeAsync(
            string targetId,
            CancellationToken token = default);
        ValueTask StopAsync(CancellationToken token = default);
    }
}
