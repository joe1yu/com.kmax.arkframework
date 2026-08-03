using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public interface IConfigService
    {
        T Get<T>(string key);

        bool TryGet<T>(string key, out T value);

        void RegisterValidator<T>(IConfigValidator<T> validator);

        ValueTask ReloadAsync(CancellationToken token = default);

        ConfigDiagnostics Diagnostics { get; }
    }
}
