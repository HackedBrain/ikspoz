using System.Threading;
using System.Threading.Tasks;

namespace Ikspoz.Cli
{
    internal sealed class RawTokenAzureTokenProvider : IAzureAuthTokenProvider
    {
        private readonly Task<string> _tokenTask;


        public RawTokenAzureTokenProvider(string token)
        {
            _tokenTask = Task.FromResult(token);
        }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => _tokenTask;
    }
}
