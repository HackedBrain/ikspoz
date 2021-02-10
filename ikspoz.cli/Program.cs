using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Net.Http;
using System.Threading.Tasks;
using Azure.Identity;
using Microsoft.Azure.Relay;

namespace Ikspoz.Cli
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            var parser = new CommandLineBuilder(BuildRootCommand())
                .UseDefaults()
                .Build();

            return await parser.Parse(args).InvokeAsync();
        }

        private static RootCommand BuildRootCommand()
        {
            var rootCommand = new RootCommand
            {
                Handler = CommandHandler.Create(async (string azureSubscriptionId, string azureResourceGroupName, string azureRelayNamespace, Uri tunnelTargetBaseUrl) =>
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
                        Console.WriteLine($"Connecting...");

                        var tokenProvider = TokenProvider.CreateSharedAccessSignatureTokenProvider("manualPolicy", "");
                        var hybridConnectionEndpointUri = new Uri($"sb://{azureRelayNamespace}.servicebus.windows.net/testName/");

                        var tunnelingHttpClient = new HttpClient
                        {
                            BaseAddress = tunnelTargetBaseUrl
                        };

                        var hybridConnectionListener = new HybridConnectionListener(hybridConnectionEndpointUri, tokenProvider)
                        {
                            RequestHandler = TunnelRequest
                        };

                        await hybridConnectionListener.OpenAsync();

                        Console.WriteLine($"Connected!");

                        Console.WriteLine("Press any key to shut down...");
                        Console.ReadKey();

                        Console.WriteLine($"Shutting down...");

                        await hybridConnectionListener.CloseAsync();

                        Console.WriteLine($"Shut down.");

                        async void TunnelRequest(RelayedHttpListenerContext context)
                        {
                            var request = context.Request;

                            Console.WriteLine($"Request received: {request.HttpMethod} - {request.Url}");

                            using var tunneledRequest = new HttpRequestMessage
                            {
                                Method = new HttpMethod(request.HttpMethod),
                                RequestUri = hybridConnectionEndpointUri.MakeRelativeUri(request.Url),
                                Content = new StreamContent(request.InputStream),
                            };

                            // TODO: copy headers from hybrid request to tunneled request

                            using var tunneledResponse = await tunnelingHttpClient.SendAsync(tunneledRequest);

                            var response = context.Response;

                            response.StatusCode = tunneledResponse.StatusCode;
                            response.StatusDescription = tunneledResponse.ReasonPhrase;

                            // TODO: copy headers from tunneled response to hybrid response

                            await tunneledResponse.Content.CopyToAsync(response.OutputStream);

                            await response.CloseAsync();
                        }
                    }
                }),
            };

            rootCommand.AddArgument(
                new Argument<Uri>
                {
                    Name = "tunnel-target-base-url",
                    Description = "The URL where traffic should be tunneled to.",
                }
                .AddSuggestions("http://localhost", "https://localhost", "https://localhost:8080", "https://swapi.dev/api/"));

            rootCommand.AddGlobalOption(BuildAzureSubscriptionIdOption());
            rootCommand.AddGlobalOption(BuildAzureResourceGroupNameOption());
            rootCommand.AddGlobalOption(BuildAzureRelayNamespaceOption());

            return rootCommand;

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

            static Option BuildAzureRelayNamespaceOption()
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
