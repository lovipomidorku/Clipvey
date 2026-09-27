using System.Diagnostics;
using System.Drawing;
using System.Reflection;
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
        Log.Write($"Clipvey {Version} запущен, Windows {Environment.OSVersion.Version}");
    }

    /// Версия программы (InformationalVersion, из файла VERSION).
    private static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

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

    /// IsEnabled без исключений: реестр может быть недоступен (политики) — тогда «выключен».
    public static bool SafeIsEnabled()
    {
        try
        {
            return IsEnabled;
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось прочитать автозапуск: {e.Message}");
            return false;
        }
    }

    /// Set без исключений. Возвращает, включён ли автозапуск после попытки.
    public static bool SafeSet(bool enabled)
    {
        try
        {
            Set(enabled);
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось изменить автозапуск: {e.Message}");
        }
        return SafeIsEnabled();
    }
}

internal static class AppIcon
{
    public static Icon Load(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Clipvey.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, size);
    }

    /// Картинка значка не меньше side пикселей (если есть). Внутри Clipvey.ico — PNG 16…256 px:
    /// берём PNG напрямую, чтобы не зависеть от того, как Icon.ToBitmap обходится с PNG-кадрами.
    /// PNG-кадр значка не меньше side пикселей (null — в Clipvey.ico нет PNG-кадров).
    public static byte[]? Png(int side)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Clipvey.ico");
        if (stream is null)
            return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return BestPng(memory.ToArray(), side);
    }

    public static Bitmap LoadBitmap(int side)
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Clipvey.ico");
            if (stream is not null)
            {
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                if (BestPng(memory.ToArray(), side) is { } png)
                {
                    // Копия, чтобы картинка не зависела от потока (GDI+ читает поток лениво).
                    using var pngStream = new MemoryStream(png);
                    using var decoded = new Bitmap(pngStream);
                    return new Bitmap(decoded);
                }
            }
        }
        catch (Exception e)
        {
            Log.Write($"Не удалось прочитать Clipvey.ico: {e.Message}");
        }
        using var icon = Load(new Size(side, side));
        return icon.ToBitmap();
    }

    private static byte[]? BestPng(byte[] ico, int side)
    {
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1)
            return null;
        var count = BitConverter.ToUInt16(ico, 4);
        (int Side, int Offset, int Length)? best = null;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            if (entry + 16 > ico.Length)
                break;
            var entrySide = ico[entry] == 0 ? 256 : ico[entry];
            var length = BitConverter.ToInt32(ico, entry + 8);
            var offset = BitConverter.ToInt32(ico, entry + 12);
            if (offset < 0 || length < 8 || offset + length > ico.Length || ico[offset] != 0x89 || ico[offset + 1] != (byte)'P')
                continue;
            // Наименьший кадр не меньше нужного; если такого нет — наибольший.
            var better = best is not { } current
                || (entrySide >= side ? current.Side < side || entrySide < current.Side : entrySide > current.Side && current.Side < side);
            if (better)
                best = (entrySide, offset, length);
        }
        return best is { } found ? ico.AsSpan(found.Offset, found.Length).ToArray() : null;
    }
}
