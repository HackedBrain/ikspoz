using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Threading;
using System.Threading.Tasks;

namespace Ikspoz.Cli
{
    internal interface IAzureAuthTokenProvider
    {
        Task<string> GetTokenAsync(CancellationToken cancellationToken = default);
    }

    internal static class AzureAuthTokenProviderCommandLineBuilderExtensions
    {
        public static CommandLineBuilder UseDefaultAzureAuthTokenProvider(this CommandLineBuilder commandLineBuilder) =>
            commandLineBuilder.UseMiddleware((context) =>
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
            });
    }
}
