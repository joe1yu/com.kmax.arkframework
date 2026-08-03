using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace ArkFramework.Samples
{
    public sealed class GameplayHudWindow : UIWindow
    {
        [SerializeField]
        private Button _backButton;

        private SampleNavigationCommand _navigation;

        public Button BackButton => _backButton;

        protected override ValueTask OnOpenAsync(
            object parameter,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (_backButton == null)
            {
                throw new InvalidOperationException(
                    "The Gameplay HUD Back button is not configured.");
            }

            _navigation = parameter as SampleNavigationCommand;
            if (_navigation == null)
            {
                throw new ArgumentException(
                    "GameplayHudWindow requires a sample navigation command.",
                    nameof(parameter));
            }

            _backButton.onClick.AddListener(HandleBack);
            return default;
        }

        protected override ValueTask OnCloseAsync(
            CancellationToken token)
        {
            if (_backButton != null)
            {
                _backButton.onClick.RemoveListener(HandleBack);
            }

            _navigation = null;
            return default;
        }

        private void HandleBack()
        {
            _navigation?.Execute(LifetimeToken);
        }
    }
}
