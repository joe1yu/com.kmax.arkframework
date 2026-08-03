using System;

namespace ArkFramework
{
    public interface IEventBus
    {
        IDisposable Subscribe<TEvent>(Action<TEvent> handler);
        IDisposable Subscribe<TEvent>(
            ModuleScope ownerScope,
            Action<TEvent> handler);
        void Publish<TEvent>(TEvent value);
        void Enqueue<TEvent>(TEvent value);
        EventBusDiagnostics Diagnostics { get; }
    }
}
