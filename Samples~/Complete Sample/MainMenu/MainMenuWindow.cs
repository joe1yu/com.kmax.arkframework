using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace ArkFramework.Samples
{
    public sealed class MainMenuWindow : UIWindow
    {
        [SerializeField]
        private Button _playButton;

        private SampleNavigationCommand _navigation;

        public Button PlayButton => _playButton;

        protected override ValueTask OnOpenAsync(
            object parameter,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_playButton == null)
            {
                throw new InvalidOperationException(
                    "The Main Menu Play button is not configured.");
            }

            _navigation = parameter as SampleNavigationCommand;
            if (_navigation == null)
            {
                throw new ArgumentException(
                    "MainMenuWindow requires a sample navigation command.",
                    nameof(parameter));
            }

            _playButton.onClick.AddListener(HandlePlay);
            return default;
        }

        protected override ValueTask OnCloseAsync(
            CancellationToken token)
        {
            if (_playButton != null)
            {
                _playButton.onClick.RemoveListener(HandlePlay);
            }

            _navigation = null;
            return default;
        }

        private void HandlePlay()
        {
            _navigation?.Execute(LifetimeToken);
        }
    }
}
