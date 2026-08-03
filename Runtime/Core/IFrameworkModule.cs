using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public interface IFrameworkModule
    {
        string Id { get; }
        IReadOnlyCollection<string> Dependencies { get; }
        ValueTask InitializeAsync(ModuleContext context, CancellationToken token);
        ValueTask StartAsync(CancellationToken token);
        ValueTask StopAsync(CancellationToken token);
        ValueTask DisposeAsync();
    }
}
