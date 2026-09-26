using System.Text.Json;
using System.Text.Json.Nodes;
using Clipvey.Core;

namespace Clipvey.Windows;

/// Язык интерфейса: как в системе, русский или английский.
internal enum AppLanguage
{
    System,
    Russian,
    English,
}

/// Настройки приложения: %APPDATA%\Clipvey\settings.json.
/// Файл может отсутствовать или быть испорчен — тогда берутся значения по умолчанию.
/// Автозапуск хранится не здесь, а в реестре (Autostart).
internal static class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppPaths.DataDirectory, "settings.json");
    private static JsonObject? _data;

    public static AppLanguage Language
    {
        get => Read("language") switch
        {
            "ru" => AppLanguage.Russian,
            "en" => AppLanguage.English,
            _ => AppLanguage.System,
        };
        set => Write("language", value switch
        {
            AppLanguage.Russian => "ru",
            AppLanguage.English => "en",
            _ => "system",
        });
    }

    /// «Проверять обновления автоматически» (по умолчанию включено).
    public static bool CheckUpdatesAutomatically
    {
        get => Read("checkUpdatesAutomatically") != "false";
        set => Write("checkUpdatesAutomatically", value ? "true" : "false");
    }

    /// Время последней проверки обновлений (ISO 8601).
    public static DateTimeOffset? LastUpdateCheck
    {
        get => DateTimeOffset.TryParse(Read("lastUpdateCheck"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var time) ? time : null;
        set => Write("lastUpdateCheck", value?.ToString("o", System.Globalization.CultureInfo.InvariantCulture) ?? "");
    }

    private static string? Read(string key)
    {
        try
        {
            return Data[key]?.GetValue<string>();
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static void Write(string key, string value)
    {
        Data[key] = value;
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Не удалось сохранить настройки: {e.Message}");
        }
    }

    private static JsonObject Data
    {
        get
        {
            if (_data is not null)
                return _data;
            try
            {
                if (File.Exists(FilePath))
                    _data = JsonNode.Parse(File.ReadAllText(FilePath)) as JsonObject;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Write($"Не удалось прочитать настройки, беру значения по умолчанию: {e.Message}");
            }
            return _data ??= new JsonObject();
        }
    }
}
