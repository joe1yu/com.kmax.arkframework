using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ArkFramework
{
    public sealed class FsmDiagnostics
    {
        public FsmDiagnostics(
            string machineId,
            string currentStateId,
            string previousStateId,
            bool isFaulted,
            bool isTransitioning,
            int queuedRequestCount,
            IReadOnlyList<StateHistoryEntry> history,
            Exception lastException)
            : this(
                machineId,
                currentStateId,
                previousStateId,
                isFaulted,
                isTransitioning,
                queuedRequestCount,
                history,
                lastException,
                Array.Empty<FsmTransitionDiagnostics>())
        {
        }

        public FsmDiagnostics(
            string machineId,
            string currentStateId,
            string previousStateId,
            bool isFaulted,
            bool isTransitioning,
            int queuedRequestCount,
            IReadOnlyList<StateHistoryEntry> history,
            Exception lastException,
            IReadOnlyList<FsmTransitionDiagnostics> availableTransitions)
        {
            MachineId = machineId;
            CurrentStateId = currentStateId;
            PreviousStateId = previousStateId;
            IsFaulted = isFaulted;
            IsTransitioning = isTransitioning;
            QueuedRequestCount = queuedRequestCount;
            History = history;
            LastException = lastException;
            if (availableTransitions == null)
            {
                throw new ArgumentNullException(nameof(availableTransitions));
            }

            AvailableTransitions =
                new ReadOnlyCollection<FsmTransitionDiagnostics>(
                    new List<FsmTransitionDiagnostics>(
                        availableTransitions));
        }

        public string MachineId { get; }
        public string CurrentStateId { get; }
        public string PreviousStateId { get; }
        public bool IsFaulted { get; }
        public bool IsTransitioning { get; }
        public int QueuedRequestCount { get; }
        public IReadOnlyList<StateHistoryEntry> History { get; }
        public Exception LastException { get; }
        public IReadOnlyList<FsmTransitionDiagnostics> AvailableTransitions
        {
            get;
        }
    }

    public sealed class FsmTransitionDiagnostics
    {
        public FsmTransitionDiagnostics(
            string trigger,
            string targetStateId,
            bool hasGuard)
        {
            Trigger = trigger ?? throw new ArgumentNullException(nameof(trigger));
            TargetStateId = targetStateId ??
                throw new ArgumentNullException(nameof(targetStateId));
            HasGuard = hasGuard;
        }

        public string Trigger { get; }
        public string TargetStateId { get; }
        public bool HasGuard { get; }
    }
}
