using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public interface IUIService : IAsyncDisposable
    {
        void Register<TWindow>(UIWindowDescriptor descriptor)
            where TWindow : UIWindow;

        ValueTask<IWindowHandle> OpenAsync<TWindow>(
            object parameter = null,
            CancellationToken token = default)
            where TWindow : UIWindow;

        ValueTask CloseAsync(
            IWindowHandle handle,
            CancellationToken token = default);

        ValueTask<bool> BackAsync(CancellationToken token = default);

        bool TryGetWindow(IWindowHandle handle, out UIWindow window);

        UIDiagnostics Diagnostics { get; }

        ValueTask StopAsync(CancellationToken token = default);
    }
}
