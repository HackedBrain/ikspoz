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
                .UseDefaults()
                .UseMiddleware(DisplayBanner)
                .UseUserSettingsFileSystemBasedManager(homeDirectory)
                .UseDefaultAzureAuthTokenProvider()
                .UseAzureRelayHybridConnectionManager()
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

                autoCommand.Handler = CommandHandler.Create(async (IUserSettingsManager userSettingsManager, Uri tunnelTargetBaseUrl, CancellationToken cancellationToken) =>
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

                    initializeCommand.Handler = CommandHandler.Create(async (AzureRelayOptions azureRelayOptions, IAzureRelayHybridConnectionManager azureRelayConnectionManager, IUserSettingsManager userSettingsManager, CancellationToken cancellationToken) =>
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

                        var azureRelayConnectionCreationResult = await azureRelayConnectionManager.CreateConnectionAsync(azureRelayOptions, cancellationToken);

                        if (azureRelayConnectionCreationResult.Succeeded)
                        {
                            userSettings = userSettings with { AzureRelayAutoInstance = new UserSettingsConfiguredAzureRelayAutoInstance(azureRelayOptions.SubscriptionId, azureRelayOptions.ResourceGroup, azureRelayConnectionCreationResult.NamespaceName, azureRelayConnectionCreationResult.ConnectionName, azureRelayConnectionCreationResult.ConnectionString, azureRelayConnectionCreationResult.NamespaceWasAutoCreated) };

                            await userSettingsManager.SaveUserSettingsAsync(userSettings);
                        }
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
                    cleanupCommand.Handler = CommandHandler.Create(async (IAzureRelayHybridConnectionManager azureRelayHybridConnectionManager, IUserSettingsManager userSettingsManager, CancellationToken cancellationToken) =>
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

                            var azureRelayConnectionToDelete = new AzureRelayOptions(
                                userSettings.AzureRelayAutoInstance.SubscriptionId,
                                userSettings.AzureRelayAutoInstance.ResourceGroup,
                                userSettings.AzureRelayAutoInstance.RelayNamespace,
                                string.Empty,
                                userSettings.AzureRelayAutoInstance.ConnectionName);

                            await azureRelayHybridConnectionManager.DeleteConnectionAsync(azureRelayConnectionToDelete, userSettings.AzureRelayAutoInstance.NamespaceWasAutoCreated, cancellationToken);

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
