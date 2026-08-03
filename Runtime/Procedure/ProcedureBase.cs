using System.Threading;
using System.Threading.Tasks;

namespace ArkFramework
{
    public abstract class ProcedureBase : IState<ProcedureContext>
    {
        public abstract string Id { get; }

        public abstract ValueTask EnterAsync(
            ProcedureContext context,
            CancellationToken token);

        public virtual void Update(
            ProcedureContext context,
            float deltaTime)
        {
        }

        public abstract ValueTask ExitAsync(
            ProcedureContext context,
            CancellationToken token);
    }
}
