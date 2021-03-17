using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;

namespace Ikspoz.Cli
{
    internal sealed class AzureIdentityAzureTokenProvider : IAzureAuthTokenProvider
    {
        private readonly string? _tenantId;

        public AzureIdentityAzureTokenProvider()
        {
        }

        public AzureIdentityAzureTokenProvider(string tenantId)
        {
            _tenantId = tenantId;
        }

        public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            var azureCredential = new DefaultAzureCredential(
                new DefaultAzureCredentialOptions
                {
                    SharedTokenCacheTenantId = _tenantId,
                    InteractiveBrowserTenantId = _tenantId,
                    ExcludeVisualStudioCredential = true,
                    ExcludeVisualStudioCodeCredential = true,
                });

            var token = await azureCredential.GetTokenAsync(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }), cancellationToken);

            return token.Token;
        }
    }
}
