using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Ikspoz.Cli.Settings;
using Microsoft.Azure.Relay;

namespace Ikspoz.Cli
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            var homeDirectory = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.CurrentDirectory;

            return await new CommandLineBuilder(BuildRootCommand())
                .UseMiddleware((context) =>
                {
                    Console.WriteLine($"🎉 Welcome to ikspōz!{Environment.NewLine}");
                })
                .UseMiddleware((context) =>
                {
                    context.BindingContext.AddService<IUserSettingsManager>(sp => new UserSettingsFileSystemBasedManager(Path.Combine(homeDirectory, ".ikspoz"), new UserSettingsJsonSerializer()));
                })
                .UseDefaults()
                .Build()
                .InvokeAsync(args);
        }

        private static Command BuildAzureRelayCommand()
        {
            var azureRelayCommand = new Command("azure-relay")
            {
                Description = "Connect directly to an Azure Relay instance.",
            };

            azureRelayCommand.AddCommand(BuildAutoCommand());

            azureRelayCommand.AddArgument(BuildAzureRelayConnectionStringArgument());
            azureRelayCommand.AddArgument(BuildTunnelTargetBaseUrlArgument());

            azureRelayCommand.Handler = CommandHandler.Create(async (string azureRelayConnectionString, Uri tunnelTargetBaseUrl, CancellationToken cancellationToken) =>
            {
                await TunnelTrafficAsync(azureRelayConnectionString, tunnelTargetBaseUrl, cancellationToken);
            });

            return azureRelayCommand;

            static Command BuildAutoCommand()
            {
                var autoCommand = new Command("auto");

                autoCommand.AddCommand(BuildInitializeCommand());
                autoCommand.AddCommand(BuildCleanupCommand());

                autoCommand.AddArgument(BuildTunnelTargetBaseUrlArgument());

                autoCommand.Handler = CommandHandler.Create(async (InvocationContext invocationContext, IUserSettingsManager userSettingsManager, Uri tunnelTargetBaseUrl, CancellationToken cancellationToken) =>
                {
                    var userSettings = await userSettingsManager.GetUserSettingsAsync();

                    if (userSettings.AzureRelayAutoInstance is null)
                    {
                        Console.WriteLine($"❌ Sorry, \"auto\" mode does not appear to have been initialized yet. For more information run:{Environment.NewLine}{Environment.NewLine}\t{Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly()!.Location)} auto initialize --help");

                        return;
                    }

                    await TunnelTrafficAsync(userSettings.AzureRelayAutoInstance.ConnectionString, tunnelTargetBaseUrl, cancellationToken);

                    Console.WriteLine($"👋 Bye!");
                });

                return autoCommand;

                static Command BuildInitializeCommand()
                {
                    var initializeCommand = new Command("initialize")
                    {
                        Description = "Initializes your Azure Subscription with support for the ikspōz \"auto\" connection feature."
                    };

                    initializeCommand.AddAlias("init");
                    initializeCommand.AddAlias("i");

                    initializeCommand.AddOption(BuildAzureTenantIdOption());
                    initializeCommand.AddOption(BuildAzureSubscriptionIdOption());
                    initializeCommand.AddOption(BuildAzureResourceGroupNameOption());
                    initializeCommand.AddOption(BuildAzureRelayNamespaceOption());
                    initializeCommand.AddOption(BuildAzureRelayNamespaceLocationOption());

                    initializeCommand.Handler = CommandHandler.Create(async (string? tenantId, string subscriptionId, AzureRelayOptions azureRelayOptions, IUserSettingsManager userSettingsManager, CancellationToken cancellationToken) =>
                    {
                        var userSettings = await userSettingsManager.GetUserSettingsAsync();

                        if (userSettings.AzureRelayAutoInstance is not null)
                        {
                            Console.WriteLine("❓ An instance has already been initialized for auto mode, are you sure you want to re-initialize? (y/n)");

                            if (Console.ReadKey(true).Key != ConsoleKey.Y)
                            {
                                Console.WriteLine("🏳 Ok, auto initialization canceled.");

                                return;
                            }

                            Console.WriteLine($"👍 Ok, beginning re-initialization.{Environment.NewLine}");
                        }

                        var token = GetAzureAccessToken(tenantId, cancellationToken);

                        var relayManagementClient = new Microsoft.Azure.Management.Relay.RelayManagementClient(new Microsoft.Rest.TokenCredentials(token.Token))
                        {
                            SubscriptionId = subscriptionId,
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

                                    return;
                                }
                            }
                            catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException checkNamespaceErrorResponseException)
                            {
                                if (checkNamespaceErrorResponseException.Response.StatusCode != HttpStatusCode.NotFound)
                                {
                                    Console.WriteLine($"\t💥 Unexpected status received while checking for namespace: {checkNamespaceErrorResponseException.Response.StatusCode}{Environment.NewLine}{checkNamespaceErrorResponseException.Body.Code}{Environment.NewLine}{checkNamespaceErrorResponseException.Body.Message}");

                                    return;
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

                                return;
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

                            return;
                        }

                        var randomAuthorizationRuleName = $".ikspoz-{Guid.NewGuid():N}";

                        Console.WriteLine("🛠 Creating authorization rule to listen to Hybrid Connection...");

                        var authorizationRuleConnectionString = string.Empty;

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

                            return;
                        }

                        Console.WriteLine("👍 Auto mode Hybrid Connection authorization rule created!");

                        userSettings = userSettings with { AzureRelayAutoInstance = new UserSettingsConfiguredAzureRelayAutoInstance(tenantId, subscriptionId, azureRelayOptions.ResourceGroup, azureRelayOptions.RelayNamespace, azureRelayOptions.RelayConnectionName!, authorizationRuleConnectionString, namespaceWasAutoCreated) };

                        await userSettingsManager.SaveUserSettingsAsync(userSettings);
                    });

                    return initializeCommand;

                    static Option BuildAzureTenantIdOption()
                    {
                        var option = new Option<string>("--tenant-id")
                        {
                            Description = "The ID of the Azure Tenant that should be authenticated against.",
                        };

                        option.AddAlias("--tenant");
                        option.AddAlias("--tid");
                        option.AddAlias("-t");

                        return option;
                    }

                    static Option BuildAzureSubscriptionIdOption()
                    {
                        var option = new Option<string>("--subscription-id")
                        {
                            Description = "The ID of the Azure Subscription of the Azure Relay Instance that should be used.",
                            IsRequired = true
                        };

                        option.AddAlias("--subscription");
                        option.AddAlias("--sub");
                        option.AddAlias("-s");

                        return option;
                    }

                    static Option BuildAzureRelayNamespaceOption()
                    {
                        var option = new Option<string>("--relay-namespace")
                        {
                            Description = "The Azure Relay namespace to create. If left unspecified, ikspōz will generate a unique namespace for you. (NOTE: This value must be globally unique to all of Azure.)",
                        };

                        option.AddAlias("--namespace");
                        option.AddAlias("--ns");
                        option.AddAlias("-n");

                        return option;
                    }

                    static Option BuildAzureRelayNamespaceLocationOption()
                    {
                        var option = new Option<string>("--relay-namespace-location")
                        {
                            Description = "The Azure locatation where you want the namespace to be created. For a full list of locations you can use: az account list-locations. Refer to https://azure.microsoft.com/en-us/global-infrastructure/geographies/#geographies for more information.",
                            IsRequired = true,
                        };

                        option.AddAlias("--namespace-location");
                        option.AddAlias("--location");
                        option.AddAlias("-l");
                        option.AddSuggestions("eastus", "westus2", "brazilsoutheast", "japanwest");

                        return option;
                    }

                    static Option BuildAzureResourceGroupNameOption()
                    {
                        var option = new Option<string>("--resource-group")
                        {
                            Description = "The Azure Resource Group name of the Azure Relay Instance that should be used.",
                            IsRequired = true,
                        };

                        option.AddAlias("--group");
                        option.AddAlias("--rg");
                        option.AddAlias("-g");

                        return option;
                    }
                }

                static Command BuildCleanupCommand()
                {
                    var cleanupCommand = new Command("cleanup")
                    {
                        Description = "Cleans up auto-created Azure resources."
                    };

                    cleanupCommand.AddAlias("clean");
                    cleanupCommand.AddAlias("c");
                    cleanupCommand.Handler = CommandHandler.Create(async (string? tenantId, IUserSettingsManager userSettingsManager, CancellationToken cancellationToken) =>
                    {
                        var userSettings = await userSettingsManager.GetUserSettingsAsync();

                        if (userSettings.AzureRelayAutoInstance is null)
                        {
                            Console.WriteLine("No instance has been initialized for auto mode. Try running ikspoz azure-relay auto initialize --help");
                        }
                        else
                        {
                            Console.WriteLine("Are you sure you want to delete your Azure Relay instance? (y/n)");

                            if (Console.ReadKey(true).Key != ConsoleKey.Y)
                            {
                                Console.WriteLine("👍 Ok, we'll keep your resources in Azure.");

                                return;
                            }

                            Console.WriteLine("👍 Ok, beginning resource cleanup.");

                            var token = GetAzureAccessToken(tenantId, cancellationToken);

                            var relayManagementClient = new Microsoft.Azure.Management.Relay.RelayManagementClient(new Microsoft.Rest.TokenCredentials(token.Token))
                            {
                                SubscriptionId = userSettings.AzureRelayAutoInstance.SubscriptionId,
                            };

                            if (userSettings.AzureRelayAutoInstance.NamespaceWasAutoCreated)
                            {
                                Console.WriteLine("🗑 Deleting namespace...");

                                try
                                {
                                    await relayManagementClient.Namespaces.DeleteWithHttpMessagesAsync(
                                        userSettings.AzureRelayAutoInstance.ResourceGroup,
                                        userSettings.AzureRelayAutoInstance.RelayNamespace,
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
                                        userSettings.AzureRelayAutoInstance.ResourceGroup,
                                        userSettings.AzureRelayAutoInstance.RelayNamespace,
                                        userSettings.AzureRelayAutoInstance.ConnectionName,
                                        cancellationToken: cancellationToken);

                                    Console.WriteLine("👍 Hybrid Connection deleted!");
                                }
                                catch (Microsoft.Azure.Management.Relay.Models.ErrorResponseException deleteHybridConnectionErrorResponseException)
                                {
                                    Console.WriteLine($"💥 Unexpected status received while attempting to delete Hybrid Connection: {deleteHybridConnectionErrorResponseException.Response.StatusCode}{Environment.NewLine}{deleteHybridConnectionErrorResponseException.Body.Code}{Environment.NewLine}{deleteHybridConnectionErrorResponseException.Body.Message}");

                                    return;
                                }
                            }
                            userSettings = userSettings with { AzureRelayAutoInstance = null };
                            await userSettingsManager.SaveUserSettingsAsync(userSettings);
                        }
                    });

                    return cleanupCommand;
                }
            }
        }

        private static RootCommand BuildRootCommand()
        {
            var rootCommand = new RootCommand();

            rootCommand.AddCommand(BuildAzureRelayCommand());

            return rootCommand;
        }

        private static async Task TunnelTrafficAsync(string hybridConnectionString, Uri tunnelTargetBaseUrl, CancellationToken cancellationToken)
        {
            Console.WriteLine($"📡 Connecting...");

            var tunnelingHttpClient = new HttpClient()
            {
                BaseAddress = tunnelTargetBaseUrl
            };

            var hybridConnectionListener = new HybridConnectionListener(hybridConnectionString);
            var hybridConnectionEndpointPublicUrl = new UriBuilder(hybridConnectionListener.Address) { Scheme = "https" }.Uri;

            hybridConnectionListener.RequestHandler = TunnelIndividualHttpRequest;

            await hybridConnectionListener.OpenAsync(cancellationToken);

            Console.WriteLine($"💫 Connected!{Environment.NewLine}");


            Console.WriteLine($"📥 You can begin sending requests to the following public endpoint:{Environment.NewLine}\t{hybridConnectionEndpointPublicUrl}{Environment.NewLine}");

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

            async void TunnelIndividualHttpRequest(RelayedHttpListenerContext context)
            {
                var request = context.Request;
                var response = context.Response;

                try
                {
                    var tunnelRelativeRequestUrl = hybridConnectionListener.Address.MakeRelativeUri(request.Url);

                    Console.WriteLine($"→ Request received: {request.HttpMethod} - {tunnelRelativeRequestUrl}");

                    using var tunneledRequest = new HttpRequestMessage
                    {
                        Method = new HttpMethod(request.HttpMethod),
                        RequestUri = tunnelRelativeRequestUrl,
                        Content = new StreamContent(request.InputStream),
                    };

                    foreach (var requestHeaderKey in request.Headers.AllKeys)
                    {
                        tunneledRequest.Headers.Add(requestHeaderKey, request.Headers.GetValues(requestHeaderKey)!);
                    }

                    HttpResponseMessage tunneledResponse;

                    try
                    {
                        tunneledResponse = await tunnelingHttpClient.SendAsync(tunneledRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        Console.WriteLine("💥 Error tunneling request:");
                        Console.WriteLine(exception.ToString());

                        response.StatusCode = HttpStatusCode.InternalServerError;
                        response.StatusDescription = "ikspoz: Error tunneling request";

                        return;
                    }

                    using (tunneledResponse)
                    {
                        response.StatusCode = tunneledResponse.StatusCode;
                        response.StatusDescription = tunneledResponse.ReasonPhrase;

                        foreach (var tunneledResponseHeader in tunneledResponse.Headers)
                        {
                            foreach (var tunneledResponseHeaderValue in tunneledResponseHeader.Value)
                            {
                                response.Headers.Add(tunneledResponseHeader.Key, tunneledResponseHeaderValue);
                            }
                        }

                        try
                        {
                            await tunneledResponse.Content.CopyToAsync(response.OutputStream, cancellationToken);
                        }
                        catch (Exception exception)
                        {
                            Console.WriteLine("💥 Error tunneling response:");
                            Console.WriteLine(exception.ToString());

                            return;
                        }
                    }

                    Console.WriteLine($"← Response sent: {response.StatusCode} ({response.Headers["Content-Length"]} bytes)");
                }
                catch (Exception exception)
                {
                    Console.WriteLine("💥 Unexpected error tunneling request:");
                    Console.WriteLine(exception.ToString());
                }
                finally
                {
                    await response.CloseAsync();
                }
            }
        }

        private static Azure.Core.AccessToken GetAzureAccessToken(String? tenantId, CancellationToken cancellationToken)
        {
            Console.WriteLine("🔐 Authenticating with Azure...");

            var azureCredential = new DefaultAzureCredential(
                new DefaultAzureCredentialOptions
                {
                    SharedTokenCacheTenantId = tenantId,
                    InteractiveBrowserTenantId = tenantId,
                    ExcludeVisualStudioCredential = true,
                    ExcludeVisualStudioCodeCredential = true,
                });

            var token = azureCredential.GetToken(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }), cancellationToken);

            Console.WriteLine($"🔓 Authenticated!{Environment.NewLine}{Environment.NewLine}");
            return token;

        }

        private static Argument BuildTunnelTargetBaseUrlArgument() =>
            new Argument<Uri>
            {
                Name = "tunnel-target-base-url",
                Description = "The base URL where traffic should be tunneled to.",
            }
            .AddSuggestions("http://localhost", "https://localhost", "https://localhost:8181", "https://swapi.dev/api/");

        private static Argument<string> BuildAzureRelayConnectionStringArgument() =>
            new Argument<string>
            {
                Name = "relay-connection-string",
                Description = "A connection string for an Azure Relay namespace or specific Hybrid Connection.",
            }.AddSuggestions("Endpoint=sb://<namespace-name>.servicebus.windows.net/;SharedAccessKeyName=<key-name>;SharedAccessKey=<base64-encoded-key>;Entity=<hybrid-connection-name>");
    }
}
