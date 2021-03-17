using System;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.Reflection;

namespace Ikspoz.Cli.UI
{
    public static class AppBanner
    {
        public static CommandLineBuilder UseAppBanner(this CommandLineBuilder commandLineBuilder) =>
            commandLineBuilder.UseMiddleware(context =>
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
            });

        private static string GetAppDisplayVersion() => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    }
}
