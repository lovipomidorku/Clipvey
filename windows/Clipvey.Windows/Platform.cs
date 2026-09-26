using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Win32;
using Clipvey.Core;

namespace Clipvey.Windows;

internal static class AppPaths
{
    /// %APPDATA%\Clipvey: ключ устройства и список связанных устройств.
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clipvey");

    /// %LOCALAPPDATA%\Clipvey\clipvey.log: журнал для диагностики.
    public static string LogFile { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipvey", "clipvey.log");
}

/// Ключ устройства, зашифрованный DPAPI: прочитать его может только эта учётная запись на этом компьютере.
internal sealed class DpapiSecretStore(string directory) : ISecretStore
{
    public byte[]? Load(string name)
    {
        var path = Path.Combine(directory, name + ".dpapi");
        return File.Exists(path)
            ? ProtectedData.Unprotect(File.ReadAllBytes(path), optionalEntropy: null, DataProtectionScope.CurrentUser)
            : null;
    }

    public void Save(string name, byte[] data)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(
            Path.Combine(directory, name + ".dpapi"),
            ProtectedData.Protect(data, optionalEntropy: null, DataProtectionScope.CurrentUser));
    }
}

/// Журнал в файл. При размере больше 2 МБ старый журнал переименовывается в clipvey.old.log.
internal static class FileLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static void Start()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LogFile)!);
        Log.Sink = line =>
        {
            lock (Gate)
            {
                try
                {
                    var info = new FileInfo(AppPaths.LogFile);
                    if (info.Exists && info.Length > MaxBytes)
                        File.Move(AppPaths.LogFile, Path.ChangeExtension(AppPaths.LogFile, ".old.log"), overwrite: true);
                    File.AppendAllText(AppPaths.LogFile, line + Environment.NewLine);
                }
                catch (IOException)
                {
                    // Журнал не должен ломать работу программы.
                }
            }
        };
        Log.Write($"Clipvey {Application.ProductVersion} запущен, Windows {Environment.OSVersion.Version}");
    }

    public static void Open()
    {
        if (File.Exists(AppPaths.LogFile))
            Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
    }
}

/// Автозапуск при входе в Windows: запись в HKCU\...\Run.
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Clipvey";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Write($"Автозапуск {(enabled ? "включён" : "выключен")}");
    }
}

internal static class AppIcon
{
    public static Icon Load(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Clipvey.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, size);
    }
}
