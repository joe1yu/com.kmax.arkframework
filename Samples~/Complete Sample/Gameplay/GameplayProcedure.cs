using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework.Samples
{
    public sealed class GameplayProcedure : ProcedureBase
    {
        private readonly SampleFlowController _flow;
        private IWindowHandle _window;
        private IAudioHandle _music;

        public GameplayProcedure(SampleFlowController flow)
        {
            _flow = flow ?? throw new ArgumentNullException(nameof(flow));
        }

        public override string Id => SampleContent.GameplayProcedureId;

        public override async ValueTask EnterAsync(
            ProcedureContext context,
            CancellationToken token)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var ui = context.Resolve<IUIService>();
            var sampleUI = context.Resolve<ISampleUIService>();
            var scenes = context.Resolve<ISceneService>();
            var audio = context.Resolve<IAudioService>();
            var procedures = context.Resolve<IProcedureService>();
            var actions = context.Resolve<IActionService>();
            IWindowHandle loading = null;
            IWindowHandle window = null;
            IAudioHandle music = null;
            try
            {
                loading = await sampleUI.OpenAsync(
                    SampleContent.LoadingWindowId,
                    token: token);
                await scenes.LoadByIdAsync(
                    SampleContent.GameplaySceneId,
                    token);
                await ui.CloseAsync(loading, token);
                loading = null;
                window = await sampleUI.OpenAsync(
                    SampleContent.GameplayHudWindowId,
                    new SampleNavigationCommand(
                        procedures,
                        actions,
                        SampleContent.MainMenuProcedureId),
                    token);
                music = await audio.PlayAsync(
                    new ResourceKey(
                        SampleContent.GameplayMusicAddress),
                    new AudioPlayOptions(
                        AudioChannel.Music,
                        loop: true),
                    token);
                if (!ui.TryGetWindow(window, out var activeWindow) ||
                    !(activeWindow is GameplayHudWindow))
                {
                    throw new InvalidOperationException(
                        "The sample Gameplay HUD did not become active.");
                }

                _window = window;
                _music = music;
                _flow.Publish(Id, activeWindow);
            }
            catch (Exception primary)
            {
                var failure = primary;
                if (loading != null)
                {
                    try
                    {
                        await ui.CloseAsync(
                            loading,
                            CancellationToken.None);
                    }
                    catch (Exception cleanup)
                    {
                        failure = First(failure, cleanup);
                    }
                }

                if (window != null)
                {
                    try
                    {
                        await ui.CloseAsync(
                            window,
                            CancellationToken.None);
                    }
                    catch (Exception cleanup)
                    {
                        failure = First(failure, cleanup);
                    }
                }

                if (music != null)
                {
                    try
                    {
                        await audio.StopAsync(
                            music,
                            token: CancellationToken.None);
                    }
                    catch (Exception cleanup)
                    {
                        failure = First(failure, cleanup);
                    }
                }

                _flow.Clear(Id);
                ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }
        }

        public override async ValueTask ExitAsync(
            ProcedureContext context,
            CancellationToken token)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            _flow.Clear(Id);
            var ui = context.Resolve<IUIService>();
            var audio = context.Resolve<IAudioService>();
            Exception failure = null;
            var window = _window;
            _window = null;
            if (window != null)
            {
                try
                {
                    await ui.CloseAsync(window, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }

            var music = _music;
            _music = null;
            if (music != null)
            {
                try
                {
                    await audio.StopAsync(
                        music,
                        token: CancellationToken.None);
                }
                catch (Exception exception)
                {
                    failure = First(failure, exception);
                }
            }

            if (failure != null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        private static Exception First(
            Exception current,
            Exception candidate)
        {
            return current ?? candidate;
        }
    }
}
