using System.Threading;
using System.Threading.Tasks;

namespace Ikspoz.Cli
{
    internal interface IAzureAuthTokenProvider
    {
        Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
    }
}
