using System;
using System.Collections.Generic;
using System.CommandLine.Builder;
using System.Net;
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
        public static readonly AzureRelayHybridConnectionCreationResult Failed = new AzureRelayHybridConnectionCreationResult(false);
    }

    internal sealed class AzureRelayHybridConnectionManager : IAzureRelayHybridConnectionManager
    {
        private readonly IAzureAuthTokenProvider _azureAuthTokenProvider;

        public AzureRelayHybridConnectionManager(IAzureAuthTokenProvider azureAuthTokenProvider)
        {
            _azureAuthTokenProvider = azureAuthTokenProvider;
        }

        public async Task<AzureRelayHybridConnectionCreationResult> CreateConnectionAsync(AzureRelayOptions azureRelayOptions, CancellationToken cancellationToken)
        {
            var token = await _azureAuthTokenProvider.GetTokenAsync(cancellationToken);

            var relayManagementClient = new Microsoft.Azure.Management.Relay.RelayManagementClient(new Microsoft.Rest.TokenCredentials(token))
            {
                SubscriptionId = azureRelayOptions.SubscriptionId,
            };

            var isRandomNamespace = false;

            if (azureRelayOptions.RelayNamespace is null or { Length: 0 })
            {
                azureRelayOptions = azureRelayOptions with { RelayNamespace = $"ikspoz-auto-{Guid.NewGuid():N}" };

                isRandomNamespace = true;
            }

            var namespaceAlreadyExists = false;
            var namespaceWasAutoCreated = false;


            if (!isRandomNamespace)
            {
                Console.WriteLine($"🔎 Checking if \"{azureRelayOptions.RelayNamespace}\" namespace already exists...");

                try
                {
                    await relayManagementClient.Namespaces.GetWithHttpMessagesAsync(
                        azureRelayOptions.ResourceGroup,
                        azureRelayOptions.RelayNamespace,
                        cancellationToken: cancellationToken);

                    Console.WriteLine("✔ Namespace already exists...");

                    namespaceAlreadyExists = true;
                }
                catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException checkNamespaceErrorResponseException) when (checkNamespaceErrorResponseException.Response.StatusCode == HttpStatusCode.NotFound)
                {
                    Console.WriteLine("❔ Namespace does not exist, create now? (y/n)");

                    if (Console.ReadKey(true).Key != ConsoleKey.Y)
                    {
                        Console.WriteLine("🏳 Ok, auto initialization canceled.");

                        return AzureRelayHybridConnectionCreationResult.Failed;
                    }
                }
                catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException checkNamespaceErrorResponseException)
                {
                    if (checkNamespaceErrorResponseException.Response.StatusCode != HttpStatusCode.NotFound)
                    {
                        Console.WriteLine($"💥 Unexpected status received while checking for namespace: {checkNamespaceErrorResponseException.Response.StatusCode}{Environment.NewLine}{checkNamespaceErrorResponseException.Body.Code}{Environment.NewLine}{checkNamespaceErrorResponseException.Body.Message}");

                        return AzureRelayHybridConnectionCreationResult.Failed;
                    }
                }
            }

            if (!namespaceAlreadyExists)
            {
                Console.WriteLine("🛠 Creating namespace...");

                try
                {
                    await relayManagementClient.Namespaces.CreateOrUpdateWithHttpMessagesAsync(
                        azureRelayOptions.ResourceGroup,
                        azureRelayOptions.RelayNamespace,
                        new Microsoft.Azure.Management.Relay.Models.RelayNamespace
                        {
                            Location = azureRelayOptions.RelayNamespaceLocation
                        },
                        cancellationToken: cancellationToken);

                    Console.WriteLine("👍 Namespace created!");

                    namespaceWasAutoCreated = true;
                }
                catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException createNamespaceErrorResponseException)
                {
                    Console.WriteLine($"💥 Unexpected status received while attempting to create namespace: {createNamespaceErrorResponseException.Response.StatusCode}{Environment.NewLine}{createNamespaceErrorResponseException.Body.Code}{Environment.NewLine}{createNamespaceErrorResponseException.Body.Message}");

                    return AzureRelayHybridConnectionCreationResult.Failed;
                }
            }

            Console.WriteLine("🛠 Creating Hybrid Connection...");

            if (azureRelayOptions.RelayConnectionName is null or { Length: 0 })
            {
                azureRelayOptions = azureRelayOptions with { RelayConnectionName = $"ikspoz-auto-connection-{Guid.NewGuid():N}" };
            }

            try
            {
                await relayManagementClient.HybridConnections.CreateOrUpdateWithHttpMessagesAsync(
                    azureRelayOptions.ResourceGroup,
                    azureRelayOptions.RelayNamespace,
                    azureRelayOptions.RelayConnectionName,
                    new Microsoft.Azure.Management.Relay.Models.HybridConnection
                    {
                        RequiresClientAuthorization = false,
                        UserMetadata = ".ikspoz-auto-generated-connection"
                    },
                    cancellationToken: cancellationToken);

                Console.WriteLine("👍 Auto mode Hybrid Connection created!");
            }
            catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException createConnectionErrorResponseException)
            {
                Console.WriteLine($"💥 Unexpected status received while creating connection: {createConnectionErrorResponseException.Response.StatusCode}{Environment.NewLine}{createConnectionErrorResponseException.Body.Code}{Environment.NewLine}{createConnectionErrorResponseException.Body.Message}");

                return AzureRelayHybridConnectionCreationResult.Failed;
            }

            var randomAuthorizationRuleName = $".ikspoz-{Guid.NewGuid():N}";

            Console.WriteLine("🛠 Creating authorization rule to listen to Hybrid Connection...");

            string authorizationRuleConnectionString;

            try
            {
                await relayManagementClient.HybridConnections.CreateOrUpdateAuthorizationRuleWithHttpMessagesAsync(
                    azureRelayOptions.ResourceGroup,
                    azureRelayOptions.RelayNamespace,
                    azureRelayOptions.RelayConnectionName,
                    randomAuthorizationRuleName,
                    new Microsoft.Azure.Management.Relay.Models.AuthorizationRule
                    {
                        Rights = new List<Microsoft.Azure.Management.Relay.Models.AccessRights?> { Microsoft.Azure.Management.Relay.Models.AccessRights.Listen }
                    },
                    cancellationToken: cancellationToken);

                var listAccessKeysResponse = await relayManagementClient.HybridConnections.ListKeysWithHttpMessagesAsync(azureRelayOptions.ResourceGroup, azureRelayOptions.RelayNamespace, azureRelayOptions.RelayConnectionName, randomAuthorizationRuleName, cancellationToken: cancellationToken);

                authorizationRuleConnectionString = listAccessKeysResponse.Body.PrimaryConnectionString;
            }
            catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException createAuthorizationRuleErrorResponseException)
            {
                Console.WriteLine($"💥 Unexpected status received while creating authorization rule: {createAuthorizationRuleErrorResponseException.Response.StatusCode}{Environment.NewLine}{createAuthorizationRuleErrorResponseException.Body.Code}{Environment.NewLine}{createAuthorizationRuleErrorResponseException.Body.Message}");

                return AzureRelayHybridConnectionCreationResult.Failed;
            }

            Console.WriteLine("👍 Auto mode Hybrid Connection authorization rule created!");

            return new(true, azureRelayOptions.RelayNamespace, azureRelayOptions.RelayConnectionName, authorizationRuleConnectionString, namespaceWasAutoCreated);
        }

        public async Task DeleteConnectionAsync(AzureRelayOptions azureRelayOptions, bool deleteEntireNamespace, CancellationToken cancellationToken)
        {
            var token = await _azureAuthTokenProvider.GetTokenAsync(cancellationToken);

            var relayManagementClient = new Microsoft.Azure.Management.Relay.RelayManagementClient(new Microsoft.Rest.TokenCredentials(token))
            {
                SubscriptionId = azureRelayOptions.SubscriptionId,
            };

            if (deleteEntireNamespace)
            {
                Console.WriteLine("🗑 Deleting namespace...");

                try
                {
                    await relayManagementClient.Namespaces.DeleteWithHttpMessagesAsync(
                        azureRelayOptions.ResourceGroup,
                        azureRelayOptions.RelayNamespace,
                        cancellationToken: cancellationToken);

                    Console.WriteLine("👍 Namespace deleted!");

                }
                catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException deleteNamespaceErrorResponseException)
                {
                    Console.WriteLine($"💥 Unexpected status received while attempting to delete namespace: {deleteNamespaceErrorResponseException.Response.StatusCode}{Environment.NewLine}{deleteNamespaceErrorResponseException.Body.Code}{Environment.NewLine}{deleteNamespaceErrorResponseException.Body.Message}");

                    return;
                }
            }
            else
            {
                Console.WriteLine("🗑 Deleting Hybrid Connection...");

                try
                {
                    await relayManagementClient.HybridConnections.DeleteWithHttpMessagesAsync(
                        azureRelayOptions.ResourceGroup,
                        azureRelayOptions.RelayNamespace,
                        azureRelayOptions.RelayConnectionName,
                        cancellationToken: cancellationToken);

                    Console.WriteLine("👍 Hybrid Connection deleted!");
                }
                catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException deleteHybridConnectionErrorResponseException)
                {
                    Console.WriteLine($"💥 Unexpected status received while attempting to delete Hybrid Connection: {deleteHybridConnectionErrorResponseException.Response.StatusCode}{Environment.NewLine}{deleteHybridConnectionErrorResponseException.Body.Code}{Environment.NewLine}{deleteHybridConnectionErrorResponseException.Body.Message}");

                    return;
                }
            }
        }
    }

    internal static class AzureRelayHybridConnectionManagerCommandLineBuilderExtensions
    {
        public static CommandLineBuilder UseAzureRelayHybridConnectionManager(this CommandLineBuilder commandLineBuilder) =>
            commandLineBuilder.UseMiddleware(context =>
                context.BindingContext.AddService<IAzureRelayHybridConnectionManager>(
                    sp => new AzureRelayHybridConnectionManager(sp.GetService(typeof(IAzureAuthTokenProvider)) as IAzureAuthTokenProvider ?? throw new InvalidOperationException($"No {nameof(IAzureAuthTokenProvider)} service was found."))));
    }
}
