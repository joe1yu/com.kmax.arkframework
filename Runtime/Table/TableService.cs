using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public sealed class TableService : ITableService, IDisposable
    {
        private readonly ITableTextSource _source;
        private readonly Dictionary<TableCacheKey, object> _tables =
            new Dictionary<TableCacheKey, object>();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private bool _disposed;

        public TableService(ITableTextSource source = null)
        {
            _source = source ?? new StreamingAssetsTableSource();
        }

        public async ValueTask<TableData<T>> LoadAsync<T>(
            string relativePath,
            bool forceReload = false,
            CancellationToken token = default)
        {
            EnsureNotDisposed();
            var normalized = TablePathUtility.Normalize(relativePath);
            var key = new TableCacheKey(typeof(T), normalized);
            await _gate.WaitAsync(token);
            try
            {
                EnsureNotDisposed();
                object cached;
                lock (_sync)
                {
                    if (!forceReload && _tables.TryGetValue(key, out cached))
                    {
                        return (TableData<T>)cached;
                    }
                }

                var text = await _source.ReadAsync(normalized, token);
                token.ThrowIfCancellationRequested();
                var document = CsvTableDocument.Parse(text, normalized);
                var table = TableRowMapper.Map<T>(document);
                lock (_sync)
                {
                    EnsureNotDisposed();
                    _tables[key] = table;
                }

                return table;
            }
            finally
            {
                _gate.Release();
            }
        }

        public bool TryGetLoaded<T>(
            string relativePath,
            out TableData<T> table)
        {
            EnsureNotDisposed();
            var key = new TableCacheKey(
                typeof(T),
                TablePathUtility.Normalize(relativePath));
            lock (_sync)
            {
                if (_tables.TryGetValue(key, out var cached))
                {
                    table = (TableData<T>)cached;
                    return true;
                }
            }

            table = null;
            return false;
        }

        public bool Unload<T>(string relativePath)
        {
            EnsureNotDisposed();
            lock (_sync)
            {
                return _tables.Remove(
                    new TableCacheKey(
                        typeof(T),
                        TablePathUtility.Normalize(relativePath)));
            }
        }

        public void Clear()
        {
            EnsureNotDisposed();
            lock (_sync)
            {
                _tables.Clear();
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (_sync)
            {
                _disposed = true;
                _tables.Clear();
            }

            // SemaphoreSlim 未访问 WaitHandle 时不持有非托管资源。这里不主动
            // Dispose，避免与正在完成并执行 Release 的异步加载产生竞态。
        }

        private void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TableService));
            }
        }

        private readonly struct TableCacheKey : IEquatable<TableCacheKey>
        {
            public TableCacheKey(Type type, string path)
            {
                Type = type;
                Path = path;
            }

            public Type Type { get; }

            public string Path { get; }

            public bool Equals(TableCacheKey other)
            {
                return Type == other.Type &&
                       string.Equals(Path, other.Path, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is TableCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((Type != null ? Type.GetHashCode() : 0) * 397) ^
                           (Path != null
                               ? StringComparer.Ordinal.GetHashCode(Path)
                               : 0);
                }
            }
        }
    }
}
