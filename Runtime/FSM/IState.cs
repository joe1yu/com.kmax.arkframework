using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public interface IState<TContext>
    {
        ValueTask EnterAsync(TContext context, CancellationToken token);
        void Update(TContext context, float deltaTime);
        ValueTask ExitAsync(TContext context, CancellationToken token);
    }
}
