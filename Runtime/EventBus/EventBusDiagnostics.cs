using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ArkFramework
{
    public sealed class EventBusDiagnostics
    {
        private static readonly IReadOnlyDictionary<Type, EventTypeDiagnostics>
            EmptyEntries = new ReadOnlyDictionary<Type, EventTypeDiagnostics>(
                new Dictionary<Type, EventTypeDiagnostics>());

        private readonly IReadOnlyDictionary<Type, EventTypeDiagnostics> _entries;

        internal EventBusDiagnostics(
            IDictionary<Type, EventTypeDiagnostics> entries)
        {
            _entries = entries.Count == 0
                ? EmptyEntries
                : new ReadOnlyDictionary<Type, EventTypeDiagnostics>(
                    new Dictionary<Type, EventTypeDiagnostics>(entries));
        }

        public IReadOnlyDictionary<Type, EventTypeDiagnostics> Entries => _entries;

        public EventTypeDiagnostics Get<TEvent>()
        {
            return Get(typeof(TEvent));
        }

        public EventTypeDiagnostics Get(Type eventType)
        {
            if (eventType == null)
            {
                throw new ArgumentNullException(nameof(eventType));
            }

            return _entries.TryGetValue(eventType, out var diagnostics)
                ? diagnostics
                : EventTypeDiagnostics.Empty;
        }
    }

    public sealed class EventTypeDiagnostics
    {
        internal static readonly EventTypeDiagnostics Empty =
            new EventTypeDiagnostics(0, 0, 0, null);

        internal EventTypeDiagnostics(
            int listenerCount,
            long dispatchCount,
            long exceptionCount,
            DateTime? lastDispatchUtc)
        {
            ListenerCount = listenerCount;
            DispatchCount = dispatchCount;
            ExceptionCount = exceptionCount;
            LastDispatchUtc = lastDispatchUtc;
        }

        public int ListenerCount { get; }

        public long DispatchCount { get; }

        public long ExceptionCount { get; }

        public DateTime? LastDispatchUtc { get; }
    }
}
