using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public interface ITableTextSource
    {
        ValueTask<string> ReadAsync(
            string relativePath,
            CancellationToken token = default);
    }

    public interface ITableService
    {
        ValueTask<TableData<T>> LoadAsync<T>(
            string relativePath,
            bool forceReload = false,
            CancellationToken token = default);

        bool TryGetLoaded<T>(string relativePath, out TableData<T> table);

        bool Unload<T>(string relativePath);

        void Clear();
    }
}
