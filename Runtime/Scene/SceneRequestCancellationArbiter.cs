using System;
using System.Threading;

namespace ArkFramework
{
    internal sealed class SceneRequestCancellationArbiter
    {
        private readonly object _sync = new object();
        private readonly Action _beforeBoundaryCommit;
        private bool _started;
        private bool _callerCancellationRequested;
        private bool _lifetimeCancellationRequested;
        private bool _irreversible;

        public SceneRequestCancellationArbiter(
            Action beforeBoundaryCommit = null)
        {
            _beforeBoundaryCommit = beforeBoundaryCommit;
        }

        public bool HasCrossedBoundary
        {
            get
            {
                lock (_sync)
                {
                    return _irreversible;
                }
            }
        }

        public CancellationTokenRegistration RegisterLifetimeCancellation(
            CancellationToken token)
        {
            return token.CanBeCanceled
                ? token.Register(RecordLifetimeCancellation)
                : default;
        }

        public bool TryStart(bool completionIsAlreadyTerminal)
        {
            lock (_sync)
            {
                if (completionIsAlreadyTerminal ||
                    _callerCancellationRequested ||
                    _lifetimeCancellationRequested)
                {
                    return false;
                }

                _started = true;
                return true;
            }
        }

        public bool TryCrossIrreversibleBoundary()
        {
            lock (_sync)
            {
                if (_callerCancellationRequested ||
                    _lifetimeCancellationRequested)
                {
                    return false;
                }

                _beforeBoundaryCommit?.Invoke();
                _irreversible = true;
                return true;
            }
        }

        public bool RecordCallerCancellation()
        {
            lock (_sync)
            {
                _callerCancellationRequested = true;
                return !_started || _irreversible;
            }
        }

        public void RecordLifetimeCancellation()
        {
            lock (_sync)
            {
                _lifetimeCancellationRequested = true;
            }
        }

        public bool TryCancelQueuedForStop()
        {
            lock (_sync)
            {
                _lifetimeCancellationRequested = true;
                return !_started;
            }
        }
    }
}
