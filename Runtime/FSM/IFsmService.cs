using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ArkFramework
{
    public interface IFsmService : IAsyncDisposable
    {
        IReadOnlyList<FsmDiagnostics> Diagnostics { get; }

        IStateMachine<TContext> Create<TContext>(
            string id,
            TContext context,
            int historyCapacity = 32);
        IStateMachine<TContext> Get<TContext>(string id);
        bool TryGet<TContext>(
            string id,
            out IStateMachine<TContext> machine);
        ValueTask RemoveAsync(string id);
        void Update(float deltaTime);
    }
}
