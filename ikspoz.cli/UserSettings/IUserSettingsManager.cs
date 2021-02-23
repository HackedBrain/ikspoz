using System.Threading.Tasks;

namespace Ikspoz.Cli.Settings
{
    internal interface IUserSettingsManager
    {
        Task<UserSettings> GetUserSettingsAsync();
        Task SaveUserSettingsAsync(UserSettings userSettings);
    }
}
