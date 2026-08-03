using System;
using System.Collections.Generic;
using UnityEngine;

namespace ArkFramework
{
    public static class FrameworkStaticReset
    {
        private static readonly object CallbackSync = new object();
        private static readonly List<CallbackRegistration> ResetCallbacks =
            new List<CallbackRegistration>();
        private static readonly Dictionary<Action, CallbackRegistration>
            RegisteredCallbacks =
                new Dictionary<Action, CallbackRegistration>();

        public static IDisposable Register(Action resetCallback)
        {
            if (resetCallback == null)
            {
                throw new ArgumentNullException(nameof(resetCallback));
            }

            if (resetCallback.Target != null)
            {
                throw new ArgumentException(
                    "Static reset callbacks must not capture or target instances.",
                    nameof(resetCallback));
            }

            lock (CallbackSync)
            {
                if (!RegisteredCallbacks.TryGetValue(
                        resetCallback,
                        out var registration))
                {
                    registration = new CallbackRegistration(resetCallback);
                    RegisteredCallbacks.Add(resetCallback, registration);
                    ResetCallbacks.Add(registration);
                }

                registration.ReferenceCount++;
                return new RegistrationToken(registration);
            }
        }

        public static void Reset()
        {
            FrameworkHost.ResetCurrent();

            Action[] callbacks;
            lock (CallbackSync)
            {
                callbacks = new Action[ResetCallbacks.Count];
                for (var index = 0; index < ResetCallbacks.Count; index++)
                {
                    callbacks[index] = ResetCallbacks[index].Callback;
                }
            }

            List<Exception> failures = null;
            for (var index = 0; index < callbacks.Length; index++)
            {
                try
                {
                    callbacks[index]();
                }
                catch (Exception exception)
                {
                    if (failures == null)
                    {
                        failures = new List<Exception>();
                    }

                    failures.Add(exception);
                }
            }

            if (failures != null)
            {
                throw new AggregateException(
                    "One or more framework static reset callbacks failed.",
                    failures);
            }
        }

        [RuntimeInitializeOnLoadMethod(
            RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetAtSubsystemRegistration()
        {
            try
            {
                Reset();
            }
            catch (AggregateException aggregate)
            {
                var failures = aggregate.Flatten().InnerExceptions;
                for (var index = 0; index < failures.Count; index++)
                {
                    TryLogException(failures[index]);
                }
            }
            catch (Exception exception)
            {
                TryLogException(exception);
            }
        }

        private static void TryLogException(Exception exception)
        {
            try
            {
                Debug.LogException(exception);
            }
            catch
            {
                // Unity 的自动重置入口不得因日志实现异常而中断子系统注册。
            }
        }

        private static void Unregister(CallbackRegistration registration)
        {
            lock (CallbackSync)
            {
                if (registration.ReferenceCount == 0)
                {
                    return;
                }

                registration.ReferenceCount--;
                if (registration.ReferenceCount != 0)
                {
                    return;
                }

                RegisteredCallbacks.Remove(registration.Callback);
                ResetCallbacks.Remove(registration);
            }
        }

        private sealed class CallbackRegistration
        {
            public CallbackRegistration(Action callback)
            {
                Callback = callback;
            }

            public Action Callback { get; }

            public int ReferenceCount { get; set; }
        }

        private sealed class RegistrationToken : IDisposable
        {
            private CallbackRegistration _registration;

            public RegistrationToken(CallbackRegistration registration)
            {
                _registration = registration;
            }

            public void Dispose()
            {
                var registration = System.Threading.Interlocked.Exchange(
                    ref _registration,
                    null);
                if (registration == null)
                {
                    return;
                }

                Unregister(registration);
            }
        }
    }
}
