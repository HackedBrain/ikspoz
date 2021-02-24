namespace Ikspoz.Cli.Settings
{
    internal record UserSettings(UserSettingsConfiguredAzureRelayAutoInstance? AzureRelayAutoInstance)
    {
        public UserSettings() : this(default(UserSettingsConfiguredAzureRelayAutoInstance))
        {
        }
    }

    internal record UserSettingsConfiguredAzureRelayAutoInstance(string? TenantId, string SubscriptionId, string RelayNamespace, string ConnectionName, string ConnectionString)
    {
    }
}
