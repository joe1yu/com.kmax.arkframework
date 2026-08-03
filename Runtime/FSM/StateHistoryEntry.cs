using System;

namespace ArkFramework
{
    public sealed class StateHistoryEntry
    {
        public StateHistoryEntry(
            string from,
            string to,
            string trigger,
            DateTime timestampUtc)
        {
            if (string.IsNullOrWhiteSpace(to))
            {
                throw new ArgumentException(
                    "A history target state ID is required.",
                    nameof(to));
            }

            if (string.IsNullOrWhiteSpace(trigger))
            {
                throw new ArgumentException(
                    "A history trigger is required.",
                    nameof(trigger));
            }

            From = from;
            To = to;
            Trigger = trigger;
            TimestampUtc = timestampUtc;
        }

        public string From { get; }
        public string To { get; }
        public string Trigger { get; }
        public DateTime TimestampUtc { get; }
    }
}
