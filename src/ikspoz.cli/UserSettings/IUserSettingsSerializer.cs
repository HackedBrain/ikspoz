using System.IO;
using System.Threading.Tasks;

namespace Ikspoz.Cli.Settings
{
    internal interface IUserSettingsSerializer
    {
        Task<UserSettings> DeserializeAsync(Stream settingsDataStream);
        Task SerializeAsync(UserSettings userSettings, Stream settingsDataStream);
    }
}
