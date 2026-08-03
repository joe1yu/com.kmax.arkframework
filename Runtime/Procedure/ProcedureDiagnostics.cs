using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ArkFramework
{
    public sealed class ProcedureDiagnostics
    {
        internal ProcedureDiagnostics(
            string machineId,
            string currentProcedureId,
            string previousProcedureId,
            bool isStarted,
            bool isFaulted,
            IReadOnlyList<string> registeredProcedureIds,
            IReadOnlyList<StateHistoryEntry> history,
            Exception lastException)
            : this(
                machineId,
                currentProcedureId,
                previousProcedureId,
                isStarted,
                isFaulted,
                registeredProcedureIds,
                history,
                lastException,
                Array.Empty<string>())
        {
        }

        internal ProcedureDiagnostics(
            string machineId,
            string currentProcedureId,
            string previousProcedureId,
            bool isStarted,
            bool isFaulted,
            IReadOnlyList<string> registeredProcedureIds,
            IReadOnlyList<StateHistoryEntry> history,
            Exception lastException,
            IReadOnlyList<string> availableTargetProcedureIds)
        {
            MachineId = machineId;
            CurrentProcedureId = currentProcedureId;
            PreviousProcedureId = previousProcedureId;
            IsStarted = isStarted;
            IsFaulted = isFaulted;
            RegisteredProcedureIds = registeredProcedureIds;
            History = history;
            LastException = lastException;
            AvailableTargetProcedureIds =
                new ReadOnlyCollection<string>(
                    new List<string>(
                        availableTargetProcedureIds ??
                        throw new ArgumentNullException(
                            nameof(availableTargetProcedureIds))));
        }

        public string MachineId { get; }
        public string CurrentProcedureId { get; }
        public string PreviousProcedureId { get; }
        public bool IsStarted { get; }
        public bool IsFaulted { get; }
        public IReadOnlyList<string> RegisteredProcedureIds { get; }
        public IReadOnlyList<StateHistoryEntry> History { get; }
        public Exception LastException { get; }
        public IReadOnlyList<string> AvailableTargetProcedureIds { get; }
    }
}
