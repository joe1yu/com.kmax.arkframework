using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ArkFramework
{
    [DefaultExecutionOrder(-10000)]
    public sealed class FrameworkHost : MonoBehaviour
    {
        private enum HostLifecycleState
        {
            Configurable,
            RuntimeCreated,
            ShutdownRequested
        }

        private const string LifecycleCategory = "Lifecycle";
        private const string HostModuleId = "FrameworkHost";

        [SerializeField]
        private FrameworkProfile _profile;

        private readonly IFrameworkLogger _logger = new UnityFrameworkLogger();
        private readonly object _stateSync = new object();
        private FrameworkRuntime _runtime;
        private Task _startTask;
        private Task _shutdownTask;
        private bool _duplicate;
        private HostLifecycleState _state;

        public static FrameworkHost Current { get; private set; }

        public FrameworkRuntime Runtime
        {
            get
            {
                lock (_stateSync)
                {
                    return _runtime;
                }
            }
        }

        internal static void ResetCurrent()
        {
            Current = null;
        }

        private void Awake()
        {
            if (Current != null && Current != this)
            {
                _duplicate = true;
                _logger.Warning(
                    HostModuleId,
                    LifecycleCategory,
                    "A FrameworkHost already exists. Destroying the duplicate host.");
                Destroy(gameObject);
                return;
            }

            Current = this;
            if (Application.isPlaying)
            {
                DontDestroyOnLoad(gameObject);
            }
        }

        private void Start()
        {
            _ = ObserveStartupAsync();
        }

        private void Update()
        {
            var runtime = Runtime;
            runtime?.Update(Time.deltaTime);
        }

        private void LateUpdate()
        {
            var runtime = Runtime;
            runtime?.LateUpdate(Time.deltaTime);
        }

        private void FixedUpdate()
        {
            var runtime = Runtime;
            runtime?.FixedUpdate(Time.fixedDeltaTime);
        }

        private void OnApplicationQuit()
        {
            BeginShutdown(CancellationToken.None);
        }

        private void OnDestroy()
        {
            if (Current == this)
            {
                Current = null;
            }

            BeginShutdown(CancellationToken.None);
        }

        public void Configure(FrameworkProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            lock (_stateSync)
            {
                if (_duplicate)
                {
                    throw new InvalidOperationException(
                        "A duplicate FrameworkHost cannot be configured.");
                }

                if (_state == HostLifecycleState.RuntimeCreated)
                {
                    throw new InvalidOperationException(
                        "FrameworkHost cannot be configured after runtime startup begins.");
                }

                if (_state == HostLifecycleState.ShutdownRequested)
                {
                    throw new InvalidOperationException(
                        "FrameworkHost cannot be configured after shutdown begins.");
                }

                _profile = profile;
            }
        }

        public ValueTask StartRuntimeAsync(CancellationToken token = default)
        {
            FrameworkProfile profile;
            FrameworkRuntime runtime;
            TaskCompletionSource<bool> completion;
            Task publishedTask;
            lock (_stateSync)
            {
                if (_duplicate)
                {
                    throw new InvalidOperationException(
                        "A duplicate FrameworkHost cannot start a runtime.");
                }

                if (_startTask != null)
                {
                    return new ValueTask(_startTask);
                }

                if (_state == HostLifecycleState.ShutdownRequested)
                {
                    throw new InvalidOperationException(
                        "FrameworkHost cannot start after shutdown begins.");
                }

                if (_profile == null)
                {
                    throw new InvalidOperationException(
                        "FrameworkHost requires a FrameworkProfile before startup.");
                }

                profile = _profile;
                runtime = new FrameworkRuntime(_logger);
                completion = CreateCompletionSource();
                publishedTask = completion.Task;
                _runtime = runtime;
                _startTask = publishedTask;
                _state = HostLifecycleState.RuntimeCreated;
            }

            _ = CompleteStartOperationAsync(
                runtime,
                profile,
                token,
                completion);
            return new ValueTask(publishedTask);
        }

        public ValueTask StopRuntimeAsync(CancellationToken token = default)
        {
            return new ValueTask(BeginShutdown(token));
        }

        private static async Task CompleteStartOperationAsync(
            FrameworkRuntime runtime,
            FrameworkProfile profile,
            CancellationToken token,
            TaskCompletionSource<bool> completion)
        {
            try
            {
                await runtime.StartAsync(profile.CreateDescriptors(), token);
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private Task BeginShutdown(CancellationToken token)
        {
            FrameworkRuntime runtime;
            Task startTask;
            TaskCompletionSource<bool> completion;
            Task publishedTask;
            lock (_stateSync)
            {
                if (_shutdownTask != null)
                {
                    return _shutdownTask;
                }

                runtime = _runtime;
                startTask = _startTask;
                completion = CreateCompletionSource();
                publishedTask = completion.Task;
                _shutdownTask = publishedTask;
                _state = HostLifecycleState.ShutdownRequested;
            }

            _ = CompleteShutdownOperationAsync(
                runtime,
                startTask,
                token,
                completion);
            _ = ObserveShutdownAsync(publishedTask);
            return publishedTask;
        }

        private static async Task CompleteShutdownOperationAsync(
            FrameworkRuntime runtime,
            Task startTask,
            CancellationToken token,
            TaskCompletionSource<bool> completion)
        {
            try
            {
                await ShutdownRuntimeCoreAsync(runtime, startTask, token);
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        private static async Task ShutdownRuntimeCoreAsync(
            FrameworkRuntime runtime,
            Task startTask,
            CancellationToken token)
        {
            if (runtime == null)
            {
                return;
            }

            if (startTask != null)
            {
                try
                {
                    await startTask;
                }
                catch
                {
                    // Startup owns its failure; shutdown still disposes the runtime.
                }
            }

            try
            {
                await runtime.StopAsync(token);
            }
            finally
            {
                await runtime.DisposeAsync();
            }
        }

        private static TaskCompletionSource<bool> CreateCompletionSource()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private async Task ObserveStartupAsync()
        {
            try
            {
                await StartRuntimeAsync();
            }
            catch (Exception exception)
            {
                SafeLogError("Framework startup failed.", exception);
            }
        }

        private async Task ObserveShutdownAsync(Task shutdownTask)
        {
            try
            {
                await shutdownTask;
            }
            catch (Exception exception)
            {
                SafeLogError("Framework shutdown failed.", exception);
            }
        }

        private void SafeLogError(string message, Exception exception)
        {
            try
            {
                _logger.Error(
                    HostModuleId,
                    LifecycleCategory,
                    message,
                    exception);
            }
            catch
            {
                // Logging must not turn a lifecycle failure into an unobserved task.
            }
        }
    }
}
