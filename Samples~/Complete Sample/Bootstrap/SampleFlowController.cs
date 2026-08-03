using System;

namespace ArkFramework.Samples
{
    public interface ISampleFlow
    {
        string ActiveProcedureId { get; }

        UIWindow ActiveWindow { get; }
    }

    public sealed class SampleFlowController : ISampleFlow
    {
        public string ActiveProcedureId { get; private set; }

        public UIWindow ActiveWindow { get; private set; }

        internal void Publish(string procedureId, UIWindow window)
        {
            if (string.IsNullOrWhiteSpace(procedureId))
            {
                throw new ArgumentException(
                    "An active sample Procedure ID is required.",
                    nameof(procedureId));
            }

            ActiveProcedureId = procedureId;
            ActiveWindow = window ??
                throw new ArgumentNullException(nameof(window));
        }

        internal void Clear(string procedureId)
        {
            if (string.Equals(
                    ActiveProcedureId,
                    procedureId,
                    StringComparison.Ordinal))
            {
                ActiveProcedureId = null;
                ActiveWindow = null;
            }
        }
    }
}
