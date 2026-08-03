using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public interface IStateMachine<TContext> : IAsyncDisposable
    {
        string Id { get; }
        string CurrentStateId { get; }
        string PreviousStateId { get; }
        bool IsFaulted { get; }
        IReadOnlyList<StateHistoryEntry> History { get; }

        void RegisterState(string stateId, IState<TContext> state);
        void RegisterTransition(StateTransition<TContext> transition);
        ValueTask StartAsync(
            string stateId,
            CancellationToken token = default);
        ValueTask FireAsync(
            string trigger,
            CancellationToken token = default);
        void Update(float deltaTime);
    }
}
