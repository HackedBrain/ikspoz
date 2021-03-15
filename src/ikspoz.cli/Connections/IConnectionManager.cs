using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ikspoz.Cli
{
    internal interface IConnectionManager
    {
        Task CreateConnectionAsync(string connectionName, Dictionary<string, object> connectionProperties);
        Task DeleteConnectionAsync(string connectionName, Dictionary<string, object> parameters);
    }

    internal sealed class AzureRelayHybridConnectionManager : IConnectionManager
    {
        private readonly IAzureAuthTokenProvider _azureAuthTokenProvider;

        public AzureRelayHybridConnectionManager(IAzureAuthTokenProvider azureAuthTokenProvider)
        {
            _azureAuthTokenProvider = azureAuthTokenProvider;
        }

        public Task CreateConnectionAsync(string connectionName, Dictionary<string, object> connectionProperties) => throw new System.NotImplementedException();
        public Task DeleteConnectionAsync(string connectionName, Dictionary<string, object> parameters) => throw new System.NotImplementedException();    
    }
}
