using System;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework.Samples
{
    public interface ISampleUIService
    {
        SampleUIRow Get(string tableId);

        ValueTask<IWindowHandle> OpenAsync(
            string tableId,
            object parameter = null,
            CancellationToken token = default);
    }

    /// <summary>
    /// 将 UI 配表 ID 转换为 ArkFramework UI 的强类型注册与打开调用。
    /// </summary>
    public sealed class SampleUIService : ISampleUIService
    {
        private readonly IUIService _ui;
        private readonly TableData<SampleUIRow> _table;

        public SampleUIService(
            IUIService ui,
            TableData<SampleUIRow> table)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _table = table ?? throw new ArgumentNullException(nameof(table));
            if (!_table.HasKey)
            {
                throw new ArgumentException(
                    "Sample UI table must declare #key,Id.",
                    nameof(table));
            }

            RegisterAll();
        }

        public SampleUIRow Get(string tableId)
        {
            return _table.Get(ValidateTableId(tableId));
        }

        public ValueTask<IWindowHandle> OpenAsync(
            string tableId,
            object parameter = null,
            CancellationToken token = default)
        {
            var row = Get(tableId);
            switch (row.WindowType)
            {
                case nameof(MainMenuWindow):
                    return _ui.OpenAsync<MainMenuWindow>(parameter, token);
                case nameof(GameplayHudWindow):
                    return _ui.OpenAsync<GameplayHudWindow>(parameter, token);
                case nameof(LoadingWindow):
                    return _ui.OpenAsync<LoadingWindow>(parameter, token);
                default:
                    throw UnsupportedWindowType(row);
            }
        }

        private void RegisterAll()
        {
            foreach (var row in _table.Rows)
            {
                var descriptor = CreateDescriptor(row);
                switch (row.WindowType)
                {
                    case nameof(MainMenuWindow):
                        _ui.Register<MainMenuWindow>(descriptor);
                        break;
                    case nameof(GameplayHudWindow):
                        _ui.Register<GameplayHudWindow>(descriptor);
                        break;
                    case nameof(LoadingWindow):
                        _ui.Register<LoadingWindow>(descriptor);
                        break;
                    default:
                        throw UnsupportedWindowType(row);
                }
            }
        }

        private static UIWindowDescriptor CreateDescriptor(SampleUIRow row)
        {
            if (row == null)
            {
                throw new ArgumentNullException(nameof(row));
            }

            return new UIWindowDescriptor(
                row.Id,
                new ResourceKey(row.Address),
                row.Layer,
                row.Mode,
                row.CacheOnClose,
                row.RequiresMask,
                row.CloseOnMaskClick,
                row.BlocksInput,
                row.AllowBack,
                row.RootId);
        }

        private static string ValidateTableId(string tableId)
        {
            if (string.IsNullOrWhiteSpace(tableId))
            {
                throw new ArgumentException(
                    "A Sample UI table ID is required.",
                    nameof(tableId));
            }

            return tableId;
        }

        private static InvalidOperationException UnsupportedWindowType(
            SampleUIRow row)
        {
            return new InvalidOperationException(
                $"UI table row '{row?.Id}' uses unsupported window type " +
                $"'{row?.WindowType}'.");
        }
    }
}
