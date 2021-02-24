using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Ikspoz.Cli.Settings
{
    internal sealed class UserSettingsJsonSerializer : IUserSettingsSerializer
    {
        private static readonly JsonSerializerOptions DefaultJsonSerializerOptions = new()
        {
            AllowTrailingCommas = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public async Task<UserSettings> DeserializeAsync(Stream settingsDataStream) =>
            await JsonSerializer.DeserializeAsync<UserSettings>(settingsDataStream, DefaultJsonSerializerOptions) ?? new UserSettings();

        public async Task SerializeAsync(UserSettings userSettings, Stream settingsDataStream) =>
            await JsonSerializer.SerializeAsync(settingsDataStream, userSettings, DefaultJsonSerializerOptions);
    }
}
