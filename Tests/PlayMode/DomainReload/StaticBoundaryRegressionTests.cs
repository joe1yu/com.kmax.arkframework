using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ArkFramework.Tests
{
    public sealed class StaticBoundaryRegressionTests
    {
        private const double TimeoutSeconds = 10d;
        private readonly List<TaskCompletionSource<bool>> _completions =
            new List<TaskCompletionSource<bool>>();
        private readonly List<ManualResetEventSlim> _signals =
            new List<ManualResetEventSlim>();
        private readonly List<Task> _trackedTasks = new List<Task>();
        private readonly HashSet<Task> _expectedFaultTasks =
            new HashSet<Task>();
        private readonly List<IAsyncDisposable> _stateMachines =
            new List<IAsyncDisposable>();
        private readonly List<FsmService> _fsmServices =
            new List<FsmService>();
        private readonly List<ProcedureService> _procedureServices =
            new List<ProcedureService>();
        private UIRoot _createdRoot;

        [SetUp]
        public void SetUp()
        {
            FrameworkStaticReset.Reset();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            var failures = new List<Exception>();
            for (var index = 0; index < _completions.Count; index++)
            {
                try
                {
                    _completions[index].TrySetResult(true);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            for (var index = 0; index < _signals.Count; index++)
            {
                try
                {
                    _signals[index].Set();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            yield return null;
            for (var index = 0; index < _trackedTasks.Count; index++)
            {
                yield return WaitForCleanupTask(
                    _trackedTasks[index],
                    "Tracked boundary task cleanup timed out.",
                    failures,
                    swallowTaskFailure:
                    _expectedFaultTasks.Contains(_trackedTasks[index]));
            }

            for (var index = 0; index < _procedureServices.Count; index++)
            {
                Task stop = null;
                try
                {
                    stop = _procedureServices[index].StopAsync().AsTask();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }

                if (stop != null)
                {
                    yield return WaitForCleanupTask(
                        stop,
                        "Procedure cleanup timed out.",
                        failures);
                }
            }

            for (var index = 0; index < _stateMachines.Count; index++)
            {
                Task dispose = null;
                try
                {
                    dispose =
                        _stateMachines[index].DisposeAsync().AsTask();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }

                if (dispose != null)
                {
                    yield return WaitForCleanupTask(
                        dispose,
                        "State machine cleanup timed out.",
                        failures);
                }
            }

            for (var index = 0; index < _fsmServices.Count; index++)
            {
                Task dispose = null;
                try
                {
                    dispose = _fsmServices[index].DisposeAsync().AsTask();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }

                if (dispose != null)
                {
                    yield return WaitForCleanupTask(
                        dispose,
                        "FSM service cleanup timed out.",
                        failures);
                }
            }

            if (_createdRoot != null)
            {
                Task dispose = null;
                try
                {
                    dispose = _createdRoot.DisposeAsync().AsTask();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }

                if (dispose != null)
                {
                    yield return WaitForCleanupTask(
                        dispose,
                        "UIRoot failure-path cleanup timed out.",
                        failures);
                }

                _createdRoot = null;
                yield return null;
            }

            for (var index = 0; index < _signals.Count; index++)
            {
                try
                {
                    _signals[index].Dispose();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            try
            {
                FrameworkStaticReset.Reset();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            _completions.Clear();
            _signals.Clear();
            _trackedTasks.Clear();
            _expectedFaultTasks.Clear();
            _stateMachines.Clear();
            _fsmServices.Clear();
            _procedureServices.Clear();
            if (failures.Count != 0)
            {
                throw new AggregateException(
                    "Static boundary test cleanup failed.",
                    failures);
            }
        }

        [Test]
        public void Register_DisposedTokenStopsFutureInvocation()
        {
            RegistrationCallbacks.Count = 0;
            var registration =
                FrameworkStaticReset.Register(
                    RegistrationCallbacks.Increment);
            try
            {
                FrameworkStaticReset.Reset();
                Assert.That(RegistrationCallbacks.Count, Is.EqualTo(1));
            }
            finally
            {
                registration.Dispose();
            }

            FrameworkStaticReset.Reset();
            Assert.That(RegistrationCallbacks.Count, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator ObjectPool_ConcurrentFactoriesClaimBeforeOnCreate()
        {
            var shared = new PooledReference();
            var firstEntered = Track(new ManualResetEventSlim());
            var releaseFirst = Track(new ManualResetEventSlim());
            var callbackCount = 0;
            Action<PooledReference> onCreate = _ =>
            {
                if (Interlocked.Increment(ref callbackCount) == 1)
                {
                    firstEntered.Set();
                    releaseFirst.Wait(TimeSpan.FromSeconds(TimeoutSeconds));
                }
            };
            var firstPool = new ObjectPool<PooledReference>(
                () => shared,
                maxIdleCapacity: 0,
                onCreate: onCreate);
            var secondPool = new ObjectPool<PooledReference>(
                () => shared,
                maxIdleCapacity: 0,
                onCreate: onCreate);

            var firstRent = Track(
                Task.Run(() => firstPool.Rent()),
                expectedFault: true);
            Assert.That(_trackedTasks, Does.Contain(firstRent));
            yield return WaitFor(
                () => firstEntered.IsSet,
                "The first pool did not enter onCreate.");
            var secondRent = Track(
                Task.Run(() => secondPool.Rent()),
                expectedFault: true);
            yield return WaitFor(
                () => secondRent.IsCompleted ||
                      Volatile.Read(ref callbackCount) > 1,
                "The competing pool did not reach a terminal claim state.");
            releaseFirst.Set();
            yield return WaitForTask(
                Task.WhenAll(
                    IgnoreFailure(firstRent),
                    IgnoreFailure(secondRent)),
                "Concurrent pool claims did not settle.");

            Assert.That(callbackCount, Is.EqualTo(1));
            Assert.That(
                new[] { firstRent.IsCompletedSuccessfully,
                    secondRent.IsCompletedSuccessfully },
                Has.Exactly(1).True);

            if (firstRent.IsCompletedSuccessfully)
            {
                firstPool.Return(firstRent.Result);
            }
            else
            {
                secondPool.Return(secondRent.Result);
            }
        }

        [UnityTest]
        public IEnumerator ObjectPool_ResetDuringOnCreateRejectsOldGeneration()
        {
            var shared = new PooledReference();
            var entered = Track(new ManualResetEventSlim());
            var release = Track(new ManualResetEventSlim());
            var destroyCount = 0;
            var oldPool = new ObjectPool<PooledReference>(
                () => shared,
                maxIdleCapacity: 0,
                onCreate: _ =>
                {
                    entered.Set();
                    release.Wait(TimeSpan.FromSeconds(TimeoutSeconds));
                },
                onDestroy: _ => Interlocked.Increment(ref destroyCount));

            var oldRent = Track(
                Task.Run(() => oldPool.Rent()),
                expectedFault: true);
            yield return WaitFor(
                () => entered.IsSet,
                "The old generation did not enter onCreate.");

            FrameworkStaticReset.Reset();
            var freshCreateCount = 0;
            var freshPool = new ObjectPool<PooledReference>(
                () => shared,
                maxIdleCapacity: 1,
                onCreate: _ => freshCreateCount++);
            Assert.Throws<InvalidOperationException>(() => freshPool.Rent());
            Assert.That(freshCreateCount, Is.Zero);

            var different = new PooledReference();
            var differentPool = new ObjectPool<PooledReference>(
                () => different,
                maxIdleCapacity: 0);
            Assert.That(differentPool.Rent(), Is.SameAs(different));
            differentPool.Return(different);

            release.Set();
            yield return WaitForTask(
                IgnoreFailure(oldRent),
                "The old generation rent did not settle.");

            Assert.That(oldRent.IsFaulted, Is.True);
            Assert.That(
                oldRent.Exception?.GetBaseException(),
                Is.TypeOf<InvalidOperationException>());
            Assert.That(destroyCount, Is.EqualTo(1));

            Assert.That(freshPool.Rent(), Is.SameAs(shared));
            Assert.That(freshCreateCount, Is.EqualTo(1));
            freshPool.Return(shared);
            Assert.That(freshPool.Rent(), Is.SameAs(shared));
            freshPool.Return(shared);
        }

        [UnityTest]
        public IEnumerator UIService_ResetInvalidatesSuspendedAsyncLocalFlow()
        {
            var entered = NewCompletion();
            var release = NewCompletion();
            var observation = Track(
                BeginSuspendedAsyncLocalObservation(
                    typeof(UIService),
                    "CurrentCallback",
                    entered,
                    release));
            Assert.That(_trackedTasks, Does.Contain(observation));
            yield return WaitForTask(
                entered.Task,
                "The UI callback holder was not captured.");

            FrameworkStaticReset.Reset();
            release.TrySetResult(true);
            yield return WaitForTask(
                observation,
                "The UI callback holder observation did not resume.");

            Assert.That(observation.Result, Is.False);
        }

        [UnityTest]
        public IEnumerator StateMachine_ResetInvalidatesEveryGenericHolder()
        {
            var objectEntered = NewCompletion();
            var objectRelease = NewCompletion();
            var stringEntered = NewCompletion();
            var stringRelease = NewCompletion();
            bool? objectObserved = null;
            bool? stringObserved = null;

            var objectMachine = Track(
                new StateMachine<object>(
                    "object-machine",
                    new object()));
            objectMachine.RegisterState(
                "waiting",
                new SuspendingState<object>(
                    objectEntered,
                    objectRelease,
                    () => objectObserved =
                        ReadIsExecutingCallback(objectMachine)));
            var stringMachine = Track(
                new StateMachine<string>(
                    "string-machine",
                    "context"));
            stringMachine.RegisterState(
                "waiting",
                new SuspendingState<string>(
                    stringEntered,
                    stringRelease,
                    () => stringObserved =
                        ReadIsExecutingCallback(stringMachine)));

            var objectStart = Track(
                objectMachine.StartAsync("waiting").AsTask());
            var stringStart = Track(
                stringMachine.StartAsync("waiting").AsTask());
            Assert.That(_stateMachines, Does.Contain(objectMachine));
            Assert.That(_trackedTasks, Does.Contain(objectStart));
            yield return WaitForTask(
                Task.WhenAll(objectEntered.Task, stringEntered.Task),
                "The generic FSM callbacks did not suspend.");

            FrameworkStaticReset.Reset();
            objectRelease.TrySetResult(true);
            stringRelease.TrySetResult(true);
            yield return WaitForTask(
                Task.WhenAll(objectStart, stringStart),
                "The generic FSM callbacks did not resume.");

            Assert.That(objectObserved, Is.False);
            Assert.That(stringObserved, Is.False);

            var dispose = Task.WhenAll(
                objectMachine.DisposeAsync().AsTask(),
                stringMachine.DisposeAsync().AsTask());
            yield return WaitForTask(dispose, "FSM cleanup did not complete.");
            Observe(dispose);
        }

        [UnityTest]
        public IEnumerator Procedure_ResetInvalidatesSuspendedCallbackOwner()
        {
            var entered = NewCompletion();
            var release = NewCompletion();
            bool? observed = null;
            var fsm = Track(new FsmService());
            var service = Track(
                new ProcedureService(
                    fsm,
                    new ServiceContainer()));
            service.Register(
                new SuspendingProcedure(
                    entered,
                    release,
                    () => observed =
                        ReadStaticAsyncLocalValue(
                            typeof(ProcedureService),
                            "CallbackOwner") != null));

            var start = Track(service.StartAsync("waiting").AsTask());
            Assert.That(_procedureServices, Does.Contain(service));
            Assert.That(_fsmServices, Does.Contain(fsm));
            Assert.That(_trackedTasks, Does.Contain(start));
            yield return WaitForTask(
                entered.Task,
                "The Procedure callback did not suspend.");

            FrameworkStaticReset.Reset();
            release.TrySetResult(true);
            yield return WaitForTask(
                start,
                "The Procedure callback did not resume.");
            Observe(start);

            Assert.That(observed, Is.False);

            var stop = service.StopAsync().AsTask();
            yield return WaitForTask(stop, "Procedure cleanup did not complete.");
            Observe(stop);
            var disposeFsm = fsm.DisposeAsync().AsTask();
            yield return WaitForTask(
                disposeFsm,
                "FSM service cleanup did not complete.");
            Observe(disposeFsm);
        }

        [UnityTest]
        public IEnumerator UIRoot_ResetClearsSingletonAndThreadMarker()
        {
            var root = UIRoot.Create(dontDestroyOnLoad: false);
            _createdRoot = root;
            Assert.That(
                ReadStaticField(typeof(UIRoot), "_instance"),
                Is.SameAs(root));
            Assert.That(
                (int)ReadStaticField(typeof(UIRoot), "_unityThreadId"),
                Is.Not.Zero);

            FrameworkStaticReset.Reset();

            Assert.That(
                ReadStaticField(typeof(UIRoot), "_instance"),
                Is.Null);
            Assert.That(
                (int)ReadStaticField(typeof(UIRoot), "_unityThreadId"),
                Is.Zero);

            var dispose = root.DisposeAsync().AsTask();
            yield return WaitForTask(
                dispose,
                "UIRoot cleanup did not complete.");
            Observe(dispose);
            _createdRoot = null;
            yield return null;
        }

        private static async Task<bool> BeginSuspendedAsyncLocalObservation(
            Type ownerType,
            string fieldName,
            TaskCompletionSource<bool> entered,
            TaskCompletionSource<bool> release)
        {
            var field = GetStaticField(ownerType, fieldName);
            var holder = field.GetValue(null);
            var valueProperty = holder.GetType().GetProperty("Value");
            Assert.That(valueProperty, Is.Not.Null);
            var frameType = holder.GetType().GetGenericArguments()[0];
#pragma warning disable SYSLIB0050
            var frame = FormatterServices.GetUninitializedObject(frameType);
#pragma warning restore SYSLIB0050
            valueProperty.SetValue(holder, frame);

            entered.TrySetResult(true);
            await release.Task;
            return ReadStaticAsyncLocalValue(ownerType, fieldName) != null;
        }

        private static object ReadStaticAsyncLocalValue(
            Type ownerType,
            string fieldName)
        {
            var holder = GetStaticField(ownerType, fieldName).GetValue(null);
            var valueProperty = holder.GetType().GetProperty("Value");
            Assert.That(valueProperty, Is.Not.Null);
            return valueProperty.GetValue(holder);
        }

        private static bool ReadIsExecutingCallback<TContext>(
            StateMachine<TContext> machine)
        {
            var property = machine.GetType().GetProperty(
                "IsExecutingCallback",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            return (bool)property.GetValue(machine);
        }

        private static object ReadStaticField(Type ownerType, string fieldName)
        {
            return GetStaticField(ownerType, fieldName).GetValue(null);
        }

        private static FieldInfo GetStaticField(
            Type ownerType,
            string fieldName)
        {
            var field = ownerType.GetField(
                fieldName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return field;
        }

        private TaskCompletionSource<bool> NewCompletion()
        {
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _completions.Add(completion);
            return completion;
        }

        private ManualResetEventSlim Track(ManualResetEventSlim signal)
        {
            _signals.Add(signal);
            return signal;
        }

        private Task Track(Task task, bool expectedFault = false)
        {
            _trackedTasks.Add(task);
            if (expectedFault)
            {
                _expectedFaultTasks.Add(task);
            }

            return task;
        }

        private Task<T> Track<T>(
            Task<T> task,
            bool expectedFault = false)
        {
            _trackedTasks.Add(task);
            if (expectedFault)
            {
                _expectedFaultTasks.Add(task);
            }

            return task;
        }

        private StateMachine<TContext> Track<TContext>(
            StateMachine<TContext> machine)
        {
            _stateMachines.Add(machine);
            return machine;
        }

        private FsmService Track(FsmService service)
        {
            _fsmServices.Add(service);
            return service;
        }

        private ProcedureService Track(ProcedureService service)
        {
            _procedureServices.Add(service);
            return service;
        }

        private static async Task IgnoreFailure(Task task)
        {
            try
            {
                await task;
            }
            catch
            {
                // The assertion examines the original task.
            }
        }

        private static IEnumerator WaitFor(
            Func<bool> condition,
            string timeoutMessage)
        {
            var elapsed = Stopwatch.StartNew();
            while (!condition() &&
                   elapsed.Elapsed.TotalSeconds < TimeoutSeconds)
            {
                yield return null;
            }

            elapsed.Stop();
            Assert.That(condition(), Is.True, timeoutMessage);
        }

        private static IEnumerator WaitForTask(Task task, string timeoutMessage)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted &&
                   elapsed.Elapsed.TotalSeconds < TimeoutSeconds)
            {
                yield return null;
            }

            elapsed.Stop();
            Assert.That(task.IsCompleted, Is.True, timeoutMessage);
        }

        private static IEnumerator WaitForCleanupTask(
            Task task,
            string timeoutMessage,
            ICollection<Exception> failures,
            bool swallowTaskFailure = false)
        {
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted &&
                   elapsed.Elapsed.TotalSeconds < TimeoutSeconds)
            {
                yield return null;
            }

            elapsed.Stop();
            if (!task.IsCompleted)
            {
                failures.Add(
                    new TimeoutException(
                        timeoutMessage + " Elapsed real seconds: " +
                        elapsed.Elapsed.TotalSeconds.ToString("F3") + "."));
                yield break;
            }

            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                if (!swallowTaskFailure)
                {
                    failures.Add(exception);
                }
            }
        }

        private static void Observe(Task task)
        {
            task.GetAwaiter().GetResult();
        }

        private sealed class PooledReference
        {
        }

        private sealed class SuspendingState<TContext> : IState<TContext>
        {
            private readonly TaskCompletionSource<bool> _entered;
            private readonly TaskCompletionSource<bool> _release;
            private readonly Action _observe;

            public SuspendingState(
                TaskCompletionSource<bool> entered,
                TaskCompletionSource<bool> release,
                Action observe)
            {
                _entered = entered;
                _release = release;
                _observe = observe;
            }

            public async ValueTask EnterAsync(
                TContext context,
                CancellationToken token)
            {
                _entered.TrySetResult(true);
                await _release.Task;
                _observe();
            }

            public void Update(TContext context, float deltaTime)
            {
            }

            public ValueTask ExitAsync(
                TContext context,
                CancellationToken token)
            {
                return default;
            }
        }

        private sealed class SuspendingProcedure : ProcedureBase
        {
            private readonly TaskCompletionSource<bool> _entered;
            private readonly TaskCompletionSource<bool> _release;
            private readonly Action _observe;

            public SuspendingProcedure(
                TaskCompletionSource<bool> entered,
                TaskCompletionSource<bool> release,
                Action observe)
            {
                _entered = entered;
                _release = release;
                _observe = observe;
            }

            public override string Id => "waiting";

            public override async ValueTask EnterAsync(
                ProcedureContext context,
                CancellationToken token)
            {
                _entered.TrySetResult(true);
                await _release.Task;
                _observe();
            }

            public override ValueTask ExitAsync(
                ProcedureContext context,
                CancellationToken token)
            {
                return default;
            }
        }

        private static class RegistrationCallbacks
        {
            public static int Count;

            public static void Increment()
            {
                Count++;
            }
        }
    }
}
