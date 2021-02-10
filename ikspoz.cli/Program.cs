using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Threading.Tasks;
using Azure.Identity;
using Microsoft.Azure.Relay;

namespace Ikspoz.Cli
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var (rootCommand, _, _) = BuildRootCommand();

            var parser = new CommandLineBuilder(rootCommand)
                .UseDefaults()
                .Build();

            return await parser.Parse(args).InvokeAsync();
        }

        private static (RootCommand rootCommand, (Option azureSubscriptionIdOption, Option azureRelayNamespaceOption, Option azureResourceGroupNameOption) azureOptions, Option confirmOption) BuildRootCommand()
        {
            var auzreSubscriptionIdOption = BuildAzureSubscriptionIdOption();
            var azureResourceGroupNameOption = BuildAzureResourceGroupNameOption();
            var azureRelayNamespaceOption = BuildRelayNamespaceOption();
            var confirmOption = new Option<bool>("--confirm");

            var rootCommand = new RootCommand
            {
                Handler = CommandHandler.Create(async (string azureSubscriptionId, string azureResourceGroupName, string azureRelayNamespace) =>
                {
                    Console.WriteLine("Welcome to ikspoz!");

                    var azureCredential = new DefaultAzureCredential(includeInteractiveCredentials: true);


                    var token = azureCredential.GetToken(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));

                    var relayManagementClient = new Microsoft.Azure.Management.Relay.RelayManagementClient(new Microsoft.Rest.TokenCredentials(token.Token))
                    {
                        SubscriptionId = azureSubscriptionId,
                    };

                    Console.WriteLine("Preparing connection...");

                    try
                    {
                        var createHybridConnectionResponse = await relayManagementClient.HybridConnections.CreateOrUpdateWithHttpMessagesAsync(azureResourceGroupName, azureRelayNamespace, "testName", new Microsoft.Azure.Management.Relay.Models.HybridConnection { RequiresClientAuthorization = false });

                        var hybridConnectionDetails = createHybridConnectionResponse.Body;

                        Console.WriteLine($"Connection prepared! {hybridConnectionDetails.Id}");


                        await ListenToConnection(hybridConnectionDetails);

                        Console.WriteLine("Done!");
                    }
                    catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException exception)
                    {
                        Console.WriteLine($"Failed to prepare connection: {exception.Response.ReasonPhrase}");
                        Console.WriteLine(exception.Response.Content);
                    }

                    async Task ListenToConnection(Microsoft.Azure.Management.Relay.Models.HybridConnection hybridConnectionDetails)
                    {
                        var tokenProvider = TokenProvider.CreateSharedAccessSignatureTokenProvider("manualPolicy", "");
                        var hybridConnectionListener = new HybridConnectionListener(new Uri($"sb://{azureRelayNamespace}.servicebus.windows.net/testName"), tokenProvider)
                        {
                            RequestHandler = (context) =>
                            {
                                Console.WriteLine($"Request received: {context.Request.HttpMethod} - {context.Request.Url}");

                                context.Response.StatusCode = System.Net.HttpStatusCode.InternalServerError;
                                context.Response.StatusDescription = "Not Implemented Yet";
                                context.Response.Close();
                            }
                        };

                        Console.WriteLine($"Connecting...");

                        await hybridConnectionListener.OpenAsync();

                        Console.WriteLine($"Connected!");


                        Console.WriteLine("Press any key to shut down...");
                        Console.ReadKey();

                        Console.WriteLine($"Shutting down...");

                        await hybridConnectionListener.CloseAsync();

                        Console.WriteLine($"Shut down.");
                    }
                }),
            };

            rootCommand.AddGlobalOption(auzreSubscriptionIdOption);
            rootCommand.AddGlobalOption(azureResourceGroupNameOption);
            rootCommand.AddGlobalOption(azureRelayNamespaceOption);
            rootCommand.AddGlobalOption(confirmOption);

            return (rootCommand, (auzreSubscriptionIdOption, azureRelayNamespaceOption, azureResourceGroupNameOption), confirmOption);

            static Option BuildAzureSubscriptionIdOption()
            {
                var option = new Option<string>("--azure-subscription-id")
                {
                    Description = "The ID of the Azure Subscription of the Azure Relay Instance that should be used.",
                    IsRequired = true,
                };

                option.AddAlias("--subscription-id");
                option.AddAlias("--subscription");
                option.AddAlias("--sub");

                return option;
            }

            static Option BuildRelayNamespaceOption()
            {
                var option = new Option<string>("--azure-relay-namespace")
                {
                    Description = "The Azure Relay namespace to utilize when creating a connection.",
                    IsRequired = true,
                };

                option.AddAlias("--relay-namespace");
                option.AddAlias("--relay-ns");
                option.AddAlias("--rns");

                return option;
            }

            static Option BuildAzureResourceGroupNameOption()
            {
                var option = new Option<string>("--azure-resource-group-name")
                {
                    Description = "The Azure Resource Group name of the Azure Relay Instance that should be used.",
                    IsRequired = true,
                };

                option.AddAlias("--resource-group-name");
                option.AddAlias("--resource-group");
                option.AddAlias("--rg");

                return option;
            }
        }
    }
}
