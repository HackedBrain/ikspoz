namespace Ikspoz.Cli.Settings
{
    internal record UserSettings(UserSettingsConfiguredAzureRelayAutoInstance? AzureRelayAutoInstance)
    {
        public UserSettings() : this(default(UserSettingsConfiguredAzureRelayAutoInstance))
        {
        }
    }

    internal record UserSettingsConfiguredAzureRelayAutoInstance(string SubscriptionId, string ResourceGroup, string RelayNamespace, string ConnectionName, string ConnectionString, bool NamespaceWasAutoCreated)
    {
    }
}
