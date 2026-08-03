using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace ArkFramework.Samples
{
    public sealed class SampleNavigationCommand
    {
        private readonly IProcedureService _procedures;
        private readonly IActionService _actions;
        private readonly string _targetId;
        private int _transitioning;

        public SampleNavigationCommand(
            IProcedureService procedures,
            IActionService actions,
            string targetId)
        {
            _procedures = procedures ??
                throw new ArgumentNullException(nameof(procedures));
            _actions = actions ??
                throw new ArgumentNullException(nameof(actions));
            if (string.IsNullOrWhiteSpace(targetId))
            {
                throw new ArgumentException(
                    "A target Procedure ID is required.",
                    nameof(targetId));
            }

            _targetId = targetId;
        }

        public bool IsTransitioning =>
            Volatile.Read(ref _transitioning) != 0;

        public void Execute(CancellationToken lifetimeToken)
        {
            if (lifetimeToken.IsCancellationRequested ||
                Interlocked.CompareExchange(
                    ref _transitioning,
                    1,
                    0) != 0)
            {
                return;
            }

            try
            {
                // 下一帧再切换，避免在当前 UI 点击回调栈内关闭窗口。
                ActionKit.Sequence()
                    .NextFrame()
                    .Async(ChangeProcedureAsync)
                    .Start(_actions);
            }
            catch
            {
                Interlocked.Exchange(ref _transitioning, 0);
                throw;
            }
        }

        private async Task ChangeProcedureAsync(CancellationToken token)
        {
            try
            {
                await _procedures.ChangeAsync(
                    _targetId,
                    token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // ActionKit 或 Runtime 停机取消属于正常生命周期。
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
            finally
            {
                Interlocked.Exchange(ref _transitioning, 0);
            }
        }
    }
}
