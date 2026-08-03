using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public static class ActionChainExtensions
    {
        public static T Append<T>(this T container, IAction action)
            where T : IActionContainer
        {
            if (container == null)
            {
                throw new ArgumentNullException(nameof(container));
            }

            container.Add(action);
            return container;
        }

        public static T Callback<T>(this T container, Action callback)
            where T : IActionContainer
        {
            return container.Append(new CallbackAction(callback));
        }

        public static T Delay<T>(
            this T container,
            float duration,
            Action callback = null)
            where T : IActionContainer
        {
            if (callback == null)
            {
                return container.Append(new DelayAction(duration));
            }

            // 回调必须与 Delay 组成一个原子子序列；否则放入 Parallel 时
            // Callback 会被当作另一个并行分支并立即执行。
            return container.Append(
                new SequenceAction()
                    .Append(new DelayAction(duration))
                    .Append(new CallbackAction(callback)));
        }

        public static T DelayFrame<T>(
            this T container,
            int frameCount,
            Action callback = null)
            where T : IActionContainer
        {
            if (callback == null)
            {
                return container.Append(new FrameDelayAction(frameCount));
            }

            return container.Append(
                new SequenceAction()
                    .Append(new FrameDelayAction(frameCount))
                    .Append(new CallbackAction(callback)));
        }

        public static T NextFrame<T>(this T container, Action callback = null)
            where T : IActionContainer
        {
            return container.DelayFrame(1, callback);
        }

        public static T Condition<T>(this T container, Func<bool> predicate)
            where T : IActionContainer
        {
            return container.Append(new ConditionAction(predicate));
        }

        public static T Async<T>(
            this T container,
            Func<CancellationToken, Task> operation)
            where T : IActionContainer
        {
            return container.Append(new AsyncAction(operation));
        }

        public static T Custom<T>(
            this T container,
            Action<CustomAction> configure)
            where T : IActionContainer
        {
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            var action = new CustomAction();
            configure(action);
            return container.Append(action);
        }

        public static T Sequence<T>(
            this T container,
            Action<SequenceAction> configure)
            where T : IActionContainer
        {
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            var sequence = new SequenceAction();
            configure(sequence);
            return container.Append(sequence);
        }

        public static T Parallel<T>(
            this T container,
            Action<ParallelAction> configure)
            where T : IActionContainer
        {
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            var parallel = new ParallelAction();
            configure(parallel);
            return container.Append(parallel);
        }

        public static T Repeat<T>(
            this T container,
            int repeatCount,
            Action<RepeatAction> configure)
            where T : IActionContainer
        {
            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            var repeat = new RepeatAction(repeatCount);
            configure(repeat);
            return container.Append(repeat);
        }
    }
}
