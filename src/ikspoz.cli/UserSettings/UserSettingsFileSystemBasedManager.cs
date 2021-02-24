using System.IO;
using System.Threading.Tasks;

namespace Ikspoz.Cli.Settings
{
    internal sealed class UserSettingsFileSystemBasedManager : IUserSettingsManager
    {
        private readonly string _settingsFilePath;
        private readonly IUserSettingsSerializer _userSettingsSerializer;

        public UserSettingsFileSystemBasedManager(string settingsFilePath, IUserSettingsSerializer userSettingsSerializer)
        {
            _settingsFilePath = settingsFilePath;
            _userSettingsSerializer = userSettingsSerializer;
        }

        public async Task<UserSettings> GetUserSettingsAsync()
        {
            if (!File.Exists(_settingsFilePath))
            {
                return new UserSettings();
            }

            using var settingsDataStream = File.OpenRead(_settingsFilePath);

            return await _userSettingsSerializer.DeserializeAsync(settingsDataStream);
        }

        public async Task SaveUserSettingsAsync(UserSettings userSettings)
        {
            using var settingsDataStream = File.OpenWrite(_settingsFilePath);
            settingsDataStream.SetLength(0);

            await _userSettingsSerializer.SerializeAsync(userSettings, settingsDataStream);
        }
    }
}
