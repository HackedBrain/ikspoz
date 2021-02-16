using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Microsoft.Azure.Relay;

namespace Ikspoz.Cli
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            var parser = new CommandLineBuilder(BuildRootCommand())
                .UseDefaults()
                .Build();

            return await parser.Parse(args).InvokeAsync();
        }

        private static RootCommand BuildRootCommand()
        {
            var rootCommand = new RootCommand
            {
                Handler = CommandHandler.Create(async (string azureSubscriptionId, string azureResourceGroupName, string azureRelayNamespace, string? azureRelayConnectionName, Uri tunnelTargetBaseUrl) =>
                {
                    Console.WriteLine($"💥 Welcome to ikspoz!{Environment.NewLine}{Environment.NewLine}");

                    Console.WriteLine("🔐 Authenticating with Azure...");

                    var azureCredential = new DefaultAzureCredential(includeInteractiveCredentials: true);

                    var token = azureCredential.GetToken(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));

                    Console.WriteLine($"🔓 Authenticated!{Environment.NewLine}{Environment.NewLine}");

                    var relayManagementClient = new Microsoft.Azure.Management.Relay.RelayManagementClient(new Microsoft.Rest.TokenCredentials(token.Token))
                    {
                        SubscriptionId = azureSubscriptionId,
                    };

                    azureRelayConnectionName ??= Guid.NewGuid().ToString("N");

                    var hybridConnectionDetails = await EnsureAzureRelayConnectionExists();

                    if (hybridConnectionDetails is not null)
                    {
                        await ListenToConnection(hybridConnectionDetails);
                    }

                    async Task ListenToConnection(Microsoft.Azure.Management.Relay.Models.HybridConnection hybridConnectionDetails)
                    {
                        Console.WriteLine($"📡 Connecting...");

                        var tokenProvider = TokenProvider.CreateSharedAccessSignatureTokenProvider("manualPolicy", "");
                        var hybridConnectionEndpointUri = new Uri($"sb://{azureRelayNamespace}.servicebus.windows.net/{azureRelayConnectionName}/");

                        var tunnelingHttpClient = new HttpClient
                        {
                            BaseAddress = tunnelTargetBaseUrl
                        };

                        var hybridConnectionListener = new HybridConnectionListener(hybridConnectionEndpointUri, tokenProvider)
                        {
                            RequestHandler = TunnelRequest
                        };

                        await hybridConnectionListener.OpenAsync();

                        Console.WriteLine($"💫 Connected!{Environment.NewLine}{Environment.NewLine}");

                        var hybridConnectionEndpointPublicUrl = new UriBuilder(hybridConnectionEndpointUri) { Scheme = "https" }.Uri;

                        Console.WriteLine($"📥 You can begin sending requests to the following public endpoint:{Environment.NewLine}\t{hybridConnectionEndpointPublicUrl}{Environment.NewLine}{Environment.NewLine}");

                        var shutdownCompletedEvent = new ManualResetEvent(false);

                        Console.CancelKeyPress += async (_, a) =>
                        {
                            a.Cancel = true;

                            Console.WriteLine($"{Environment.NewLine}{Environment.NewLine}🔌 Shutting down...");

                            try
                            {
                                await hybridConnectionListener.CloseAsync();
                            }
                            finally
                            {
                                shutdownCompletedEvent.Set();
                            }
                        };

                        shutdownCompletedEvent.WaitOne();

                        Console.WriteLine($"👋 Bye!");

                        async void TunnelRequest(RelayedHttpListenerContext context)
                        {
                            var request = context.Request;

                            Console.WriteLine($"➡ Request received: {request.HttpMethod} - {request.Url.PathAndQuery}");

                            using var tunneledRequest = new HttpRequestMessage
                            {
                                Method = new HttpMethod(request.HttpMethod),
                                RequestUri = hybridConnectionEndpointUri.MakeRelativeUri(request.Url),
                                Content = new StreamContent(request.InputStream),
                            };

                            foreach (var requestHeaderKey in request.Headers.AllKeys)
                            {
                                tunneledRequest.Headers.Add(requestHeaderKey, request.Headers.GetValues(requestHeaderKey)!);
                            }

                            using var tunneledResponse = await tunnelingHttpClient.SendAsync(tunneledRequest);

                            var response = context.Response;

                            response.StatusCode = tunneledResponse.StatusCode;
                            response.StatusDescription = tunneledResponse.ReasonPhrase;

                            foreach (var tunneledResponseHeader in tunneledResponse.Headers)
                            {
                                foreach (var tunneledResponseHeaderValue in tunneledResponseHeader.Value)
                                {
                                    response.Headers.Add(tunneledResponseHeader.Key, tunneledResponseHeaderValue);
                                }
                            }

                            await tunneledResponse.Content.CopyToAsync(response.OutputStream);

                            await response.CloseAsync();

                            Console.WriteLine($"⬅ Response sent: {response.StatusCode} ({response.Headers["Content-Length"]} bytes)");
                        }
                    }

                    async Task<Microsoft.Azure.Management.Relay.Models.HybridConnection?> EnsureAzureRelayConnectionExists()
                    {
                        Console.WriteLine($"🔃 Preparing connection \"{azureRelayConnectionName}\"...");

                        try
                        {
                            var existingHybridConnectionResponse = await relayManagementClient.HybridConnections.GetWithHttpMessagesAsync(azureResourceGroupName, azureRelayNamespace, azureRelayConnectionName);

                            if (existingHybridConnectionResponse is not null)
                            {
                                Console.WriteLine("🌟 Connection already exists!");

                                return existingHybridConnectionResponse.Body;
                            }
                        }
                        catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException exception) when (exception.Response.StatusCode == HttpStatusCode.NotFound)
                        {
                            // Eat it
                        }

                        Console.WriteLine("🛠 Connection does not exist, creating...");

                        try
                        {
                            var createHybridConnectionResponse = await relayManagementClient.HybridConnections.CreateOrUpdateWithHttpMessagesAsync(azureResourceGroupName, azureRelayNamespace, azureRelayConnectionName, new Microsoft.Azure.Management.Relay.Models.HybridConnection { RequiresClientAuthorization = false });

                            var hybridConnectionDetails = createHybridConnectionResponse.Body;

                            return hybridConnectionDetails;
                        }
                        catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException exception)
                        {
                            Console.WriteLine($"💣 Failed to create connection: {exception.Response.ReasonPhrase}");
                            Console.WriteLine(exception.Response.Content);
                        }

                        return default;
                    }
                }),
            };

            rootCommand.AddArgument(
                new Argument<Uri>
                {
                    Name = "tunnel-target-base-url",
                    Description = "The URL where traffic should be tunneled to.",
                }
                .AddSuggestions("http://localhost", "https://localhost", "https://localhost:8181", "https://swapi.dev/api/"));

            rootCommand.AddOption(BuildAzureSubscriptionIdOption());
            rootCommand.AddOption(BuildAzureResourceGroupNameOption());
            rootCommand.AddOption(BuildAzureRelayNamespaceOption());
            rootCommand.AddOption(BuildAzureRelayConnectionNameOption());

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

            static Option BuildAzureRelayConnectionNameOption()
            {
                var option = new Option<string>("--azure-relay-connection-name")
                {
                    Description = "The Azure Relay Connection name to utilize when creating a connection.",
                    IsRequired = false,
                };

                option.AddAlias("--relay-connection-name");
                option.AddAlias("--connection-name");
                option.AddAlias("--rcn");

                option.AddValidator(optionResult =>
                {
                    var value = optionResult.GetValueOrDefault<string>();

                    if (value is { Length: > 260 })
                    {
                        optionResult.ErrorMessage = "Azure Relay Connection names cannot be greater than 260 characters in length.";
                    }

                    return value;
                });

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
