namespace Ikspoz.Cli
{
    internal record AzureRelayOptions(string ResourceGroup, string RelayNamespace, string RelayNamespaceLocation, string? RelayConnectionName)
    {
    }
}
