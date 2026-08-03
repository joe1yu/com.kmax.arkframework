using System;

namespace ArkFramework
{
    public sealed class StateTransition<TContext>
    {
        public StateTransition(
            string from,
            string trigger,
            string to,
            Func<TContext, bool> guard = null)
        {
            if (string.IsNullOrWhiteSpace(from))
            {
                throw new ArgumentException(
                    "A transition source state ID is required.",
                    nameof(from));
            }

            if (string.IsNullOrWhiteSpace(trigger))
            {
                throw new ArgumentException(
                    "A transition trigger is required.",
                    nameof(trigger));
            }

            if (string.IsNullOrWhiteSpace(to))
            {
                throw new ArgumentException(
                    "A transition target state ID is required.",
                    nameof(to));
            }

            From = from;
            Trigger = trigger;
            To = to;
            Guard = guard;
        }

        public string From { get; }
        public string Trigger { get; }
        public string To { get; }
        public Func<TContext, bool> Guard { get; }
    }
}
