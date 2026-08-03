using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace ArkFramework
{
    public sealed class StreamingAssetsTableSource : ITableTextSource
    {
        public ValueTask<string> ReadAsync(
            string relativePath,
            CancellationToken token = default)
        {
            var normalized = TablePathUtility.Normalize(relativePath);
            return new ValueTask<string>(ReadCoreAsync(normalized, token));
        }

        private static async Task<string> ReadCoreAsync(
            string normalizedPath,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var root = Application.streamingAssetsPath;
            if (root.Contains("://"))
            {
                // Android/WebGL 的 StreamingAssets 不一定是本地文件系统路径。
                var encodedPath = string.Join(
                    "/",
                    normalizedPath.Split('/')
                        .Select(Uri.EscapeDataString));
                var uri = root.TrimEnd('/') + "/" + encodedPath;
                using (var request = UnityWebRequest.Get(uri))
                {
                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        if (token.IsCancellationRequested)
                        {
                            request.Abort();
                            token.ThrowIfCancellationRequested();
                        }

                        await Task.Yield();
                    }

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        throw new IOException(
                            $"Failed to read StreamingAssets table " +
                            $"'{normalizedPath}': {request.error}");
                    }

                    return request.downloadHandler.text;
                }
            }

            var path = Path.Combine(
                root,
                normalizedPath.Replace('/', Path.DirectorySeparatorChar));
            return await Task.Run(
                () => File.ReadAllText(path, Encoding.UTF8),
                token);
        }
    }

    internal static class TablePathUtility
    {
        public static string Normalize(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException(
                    "A StreamingAssets-relative table path is required.",
                    nameof(relativePath));
            }

            var normalized = relativePath.Replace('\\', '/').Trim();
            if (Path.IsPathRooted(normalized) ||
                Uri.TryCreate(normalized, UriKind.Absolute, out _))
            {
                throw new ArgumentException(
                    "Table path must be relative to StreamingAssets.",
                    nameof(relativePath));
            }

            var segments = normalized.Split('/');
            if (segments.Any(
                    segment =>
                        string.IsNullOrEmpty(segment) ||
                        string.Equals(segment, ".", StringComparison.Ordinal) ||
                        string.Equals(segment, "..", StringComparison.Ordinal)))
            {
                throw new ArgumentException(
                    "Table path cannot contain empty, '.' or '..' segments.",
                    nameof(relativePath));
            }

            return string.Join("/", segments);
        }
    }
}
