using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\Clipvey", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(L("Clipvey уже запущен — его значок в области уведомлений на панели задач.",
                    "Clipvey is already running. Look for its icon in the notification area of the taskbar."), "Clipvey",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        FileLog.Start();
        Application.ThreadException += (_, e) => Core.Log.Write($"Ошибка интерфейса: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Core.Log.Write($"Необработанная ошибка: {e.ExceptionObject}");

        TrayApplication app;
        try
        {
            app = new TrayApplication();
        }
        catch (Exception e)
        {
            Core.Log.Write($"Не удалось запуститься: {e}");
            MessageBox.Show(L($"Не удалось запустить Clipvey:\n{e.Message}", $"Clipvey couldn’t start:\n{e.Message}"), "Clipvey", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(app);
    }
}
