using System.Threading;
using System.Threading.Tasks;

namespace Shared
{
    public interface IGameModule
    {
        string Name { get; }
        Task RunAsync(CancellationToken ct);
    }
}