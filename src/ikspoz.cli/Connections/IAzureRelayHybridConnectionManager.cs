using System.Threading;
using System.Threading.Tasks;

namespace Ikspoz.Cli
{
    internal interface IAzureRelayHybridConnectionManager
    {
        Task<AzureRelayHybridConnectionCreationResult> CreateConnectionAsync(AzureRelayOptions azureRelayOptions, CancellationToken cancellationToken);
        Task DeleteConnectionAsync(AzureRelayOptions azureRelayOptions, bool deleteEntireNamespace, CancellationToken cancellationToken);
    }

    internal record AzureRelayHybridConnectionCreationResult(bool Succeeded, string NamespaceName = "", string ConnectionName = "", string ConnectionString = "", bool NamespaceWasAutoCreated = false)
    {
        public static readonly AzureRelayHybridConnectionCreationResult Failed = new(false);
    }
}
