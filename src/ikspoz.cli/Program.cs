using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.CommandLine.Rendering;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ikspoz.Cli.Settings;

namespace Ikspoz.Cli
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            var homeDirectory = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.CurrentDirectory;

            return await new CommandLineBuilder(BuildRootCommand())
                .UseMiddleware(DisplayBanner)
                .UseMiddleware((context) =>
                {
                    context.BindingContext.AddService<IAzureAuthTokenProvider>(sp =>
                            context.ParseResult.HasOption("--azure-auth-token")
                                ?
                                new RawTokenAzureTokenProvider(context.ParseResult.ValueForOption<string>("--azure-auth-token")!)
                                :
                                (context.ParseResult.HasOption("--azure-tenant-id")
                                    ?
                                    new AzureIdentityAzureTokenProvider(context.ParseResult.ValueForOption<string>("--azure-tenant-id")!)
                                    :
                                    new AzureIdentityAzureTokenProvider()));
                })
                .UseMiddleware((context) =>
                {
                    context.BindingContext.AddService<IUserSettingsManager>(sp => new UserSettingsFileSystemBasedManager(Path.Combine(homeDirectory, ".ikspoz"), new UserSettingsJsonSerializer()));
                })
                .UseDefaults()
                .UseAnsiTerminalWhenAvailable()
                .Build()
                .InvokeAsync(args);
        }

        private static void DisplayBanner(InvocationContext context)
        {
            if (!context.ParseResult.HasOption("--no-banner"))
            {
                Console.WriteLine(@"
                            ██████╗
                            ╚═════╝
██╗██╗  ██╗███████╗██████╗  ██████╗ ███████╗
██║██║ ██╔╝██╔════╝██╔══██╗██╔═══██╗╚══███╔╝
██║█████╔╝ ███████╗██████╔╝██║   ██║  ███╔╝
██║██╔═██╗ ╚════██║██╔═══╝ ██║   ██║ ███╔╝
██║██║  ██╗███████║██║     ╚██████╔╝███████╗
╚═╝╚═╝  ╚═╝╚══════╝╚═╝      ╚═════╝ ╚══════╝");

                Console.WriteLine(@$"v{GetAppDisplayVersion()}{Environment.NewLine}");
            }
        }
        private static string GetAppDisplayVersion() => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        private static Command BuildAzureRelayCommand()
        {
            var azureRelayCommand = new Command("azure-relay")
            {
                Description = "Connect directly to an existing Azure Relay instance.",
            };

            azureRelayCommand.AddCommand(BuildAutoCommand());

            azureRelayCommand.AddArgument(BuildAzureRelayConnectionStringArgument());
            azureRelayCommand.AddArgument(BuildTunnelTargetBaseUrlArgument());

            azureRelayCommand.AddGlobalOption(BuildAzureAuthTokenOption());

            azureRelayCommand.Handler = CommandHandler.Create(async (string relayConnectionString, Uri tunnelTargetBaseUrl, CancellationToken cancellationToken) =>
            {
                await TunnelTrafficAsync(new AzureRelayTunneledConnection(relayConnectionString, tunnelTargetBaseUrl), cancellationToken);
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

                    await TunnelTrafficAsync(new AzureRelayTunneledConnection(userSettings.AzureRelayAutoInstance.ConnectionString, tunnelTargetBaseUrl), cancellationToken);
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

                    initializeCommand.Handler = CommandHandler.Create(async (string? tenantId, string subscriptionId, AzureRelayOptions azureRelayOptions, IAzureAuthTokenProvider azureAuthTokenProvider, IUserSettingsManager userSettingsManager, CancellationToken cancellationToken) =>
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

                        var token = await azureAuthTokenProvider.GetTokenAsync(cancellationToken);

                        var relayManagementClient = new Microsoft.Azure.Management.Relay.RelayManagementClient(new Microsoft.Rest.TokenCredentials(token))
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
                                    Console.WriteLine($"💥 Unexpected status received while checking for namespace: {checkNamespaceErrorResponseException.Response.StatusCode}{Environment.NewLine}{checkNamespaceErrorResponseException.Body.Code}{Environment.NewLine}{checkNamespaceErrorResponseException.Body.Message}");

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
                    cleanupCommand.Handler = CommandHandler.Create(async (string? tenantId, IAzureAuthTokenProvider azureAuthTokenProvider, IUserSettingsManager userSettingsManager, CancellationToken cancellationToken) =>
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

                            var token = await azureAuthTokenProvider.GetTokenAsync(cancellationToken);

                            var relayManagementClient = new Microsoft.Azure.Management.Relay.RelayManagementClient(new Microsoft.Rest.TokenCredentials(token))
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

            static Argument<string> BuildAzureRelayConnectionStringArgument()
            {
                return new Argument<string>()
                {
                    Name = "relay-connection-string",
                    Description = "A connection string for an existing Azure Relay Hybrid Connection.",
                }
                .AddSuggestions("Endpoint=sb://<namespace-name>.servicebus.windows.net/;Entity=<hybrid-connection-name>;SharedAccessKeyName=<key-name>;SharedAccessKey=<base64-encoded-key>");
            }

            static Option BuildAzureAuthTokenOption()
            {
                var azureAuthTokenOption = new Option<string>("--azure-auth-token")
                {
                    Description = "An Azure authentication token (JWT) that can be used for any Azure management operations. If not specified, we'll use the Azure Identity SDK to try to authenticate you.",
                };

                azureAuthTokenOption.AddAlias("--azure-auth");
                azureAuthTokenOption.AddAlias("--aat");

                return azureAuthTokenOption;
            }
        }

        private static RootCommand BuildRootCommand()
        {
            var rootCommand = new RootCommand();

            rootCommand.AddCommand(BuildAzureRelayCommand());

            var noBannerOption = new Option("--no-banner")
            {
                Description = "Prevents the application banner from being displayed.",
            };

            noBannerOption.AddAlias("--nb");

            rootCommand.AddGlobalOption(noBannerOption);

            return rootCommand;
        }

        private static async Task TunnelTrafficAsync(ITunneledConnection connection, CancellationToken cancellationToken)
        {
            connection.Events.OnConnectionOpening = () => Console.WriteLine($"📡 Connecting...");
            connection.Events.OnConnectionOpened = (hybridConnectionEndpointPublicUrl) =>
            {
                Console.WriteLine($"💫 Connected!{Environment.NewLine}");

                Console.WriteLine($"📥 You can begin sending requests to the following public endpoint:{Environment.NewLine}\t{hybridConnectionEndpointPublicUrl}{Environment.NewLine}");
            };

            connection.Events.OnRequestTunnelingException = (exception) =>
            {
                Console.WriteLine("💥 Error tunneling request:");
                Console.WriteLine(exception.ToString());
            };

            connection.Events.OnResponseTunnelingException = (exception) =>
            {
                Console.WriteLine("💥 Error tunneling response:");
                Console.WriteLine(exception.ToString());
            };


            connection.Events.OnTunnelingHttpRequest = (request) =>
            {
                Console.WriteLine($"→ Tunneling request: {request.Method} - {request.RequestUri}");
            };

            connection.Events.OnTunnelingHttpResponse = (response) =>
            {
                Console.WriteLine($"← Tunneling response: {response.StatusCode} ({response.Content.Headers.ContentLength ?? 0} bytes)");
            };

            connection.Events.OnConnectionClosing = () =>
            {
                Console.WriteLine($"{Environment.NewLine}{Environment.NewLine}🔌 Closing connection...");
            };

            connection.Events.OnConnectionClosed = () =>
            {
                Console.WriteLine($"{Environment.NewLine}{Environment.NewLine}close Connection closed!");
            };

            try
            {
                await connection.OpenAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"💥 An unexpected error occurred while attempting to open the connection:{Environment.NewLine}{Environment.NewLine}{exception.Message}");

                throw;
            }

            var shutdownCompletedEvent = new ManualResetEvent(false);

            Console.CancelKeyPress += async (_, a) =>
            {
                a.Cancel = true;

                try
                {
                    await connection.CloseAsync(cancellationToken);
                }
                catch (Exception)
                {
                    // TODO: eat and log full exception details
                }
                finally
                {
                    shutdownCompletedEvent.Set();
                }
            };

            shutdownCompletedEvent.WaitOne();

            Console.WriteLine($"👋 Bye!");
        }

        private static Argument BuildTunnelTargetBaseUrlArgument() =>
            new Argument<Uri>
            {
                Name = "tunnel-target-base-url",
                Description = "The base URL where traffic should be tunneled to.",
            }
            .AddSuggestions("http://localhost", "https://localhost", "https://localhost:8181", "https://swapi.dev/api/");
    }
}
