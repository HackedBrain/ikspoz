namespace Ikspoz.Cli
{
    internal record AzureRelayOptions(string SubscriptionId, string ResourceGroup, string RelayNamespace, string RelayNamespaceLocation, string? RelayConnectionName)
    {
    }
}
