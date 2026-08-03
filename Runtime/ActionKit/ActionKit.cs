using System;
using System.Threading;
using System.Threading.Tasks;
using Object = UnityEngine.Object;

namespace ArkFramework
{
    public static class ActionKit
    {
        public static SequenceAction Sequence()
        {
            return new SequenceAction();
        }

        public static ParallelAction Parallel()
        {
            return new ParallelAction();
        }

        public static RepeatAction Repeat(int repeatCount = -1)
        {
            return new RepeatAction(repeatCount);
        }

        public static CallbackAction Callback(Action callback)
        {
            return new CallbackAction(callback);
        }

        public static SequenceAction Delay(
            float duration,
            Action callback = null)
        {
            return Sequence().Delay(duration, callback);
        }

        public static SequenceAction DelayFrame(
            int frameCount,
            Action callback = null)
        {
            return Sequence().DelayFrame(frameCount, callback);
        }

        public static SequenceAction NextFrame(Action callback = null)
        {
            return Sequence().NextFrame(callback);
        }

        public static ConditionAction Condition(Func<bool> predicate)
        {
            return new ConditionAction(predicate);
        }

        public static AsyncAction Async(
            Func<CancellationToken, Task> operation)
        {
            return new AsyncAction(operation);
        }

        public static CustomAction Custom(Action<CustomAction> configure)
        {
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            var action = new CustomAction();
            configure(action);
            return action;
        }
    }

    public static class ActionStartExtensions
    {
        public static IActionHandle Start(
            this IAction action,
            Action onCompleted = null)
        {
            return ResolveService().Start(action, onCompleted);
        }

        public static IActionHandle Start(
            this IAction action,
            Object owner,
            Action onCompleted = null)
        {
            return ResolveService().Start(action, owner, onCompleted);
        }

        public static IActionHandle Start(
            this IAction action,
            IActionService service,
            Action onCompleted = null)
        {
            if (service == null)
            {
                throw new ArgumentNullException(nameof(service));
            }

            return service.Start(action, onCompleted);
        }

        private static IActionService ResolveService()
        {
            var host = FrameworkHost.Current;
            var runtime = host?.Runtime;
            if (runtime == null ||
                !runtime.Services.TryResolve<IActionService>(out var service))
            {
                throw new InvalidOperationException(
                    "ActionKit requires a running ActionKitModule.");
            }

            return service;
        }
    }
}
