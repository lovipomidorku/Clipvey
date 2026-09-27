using System.Windows;
using System.Windows.Threading;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Точка входа. Цикл сообщений — WPF (Application.Run): на нём живут панель, меню трея, NotifyIcon из WinForms
/// и скрытое окно ClipboardWatcher — диспетчер WPF разбирает сообщения всех окон потока.
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Первым делом: в режиме проверки (--test --data DIR) все пути, мьютекс и события — свои.
        AppPaths.Configure(args);
        // Режим установщика обновления: заменить exe и запустить его, без интерфейса и мьютекса.
        if (args.Contains(UpdaterOptions.FinishUpdateFlag))
        {
            Updater.FinishUpdate(args);
            return;
        }
        UpdaterOptions.Load(args);
        Theme.Load(args);
        using var mutex = new Mutex(initiallyOwned: true, AppPaths.InstanceName, out var isFirstInstance);
        // Запуск после обновления: старая версия ещё завершается и держит мьютекс.
        if (!isFirstInstance && UpdaterOptions.Current.AfterUpdate)
            isFirstInstance = WaitForPreviousInstance(mutex);
        if (!isFirstInstance)
        {
            // Режим проверки: --quit завершает работающую копию штатно (значок убирается, буфер очищается).
            if (AppPaths.IsolatedTest && args.Contains("--quit"))
            {
                TrayApplication.SignalQuit();
                return;
            }
            // Повторный запуск открывает панель уже работающей копии (например, если значок спрятан в трее).
            if (TrayApplication.SignalShowPanel())
                return;
            MessageBox.Show(L("Clipvey уже запущен — его значок в области уведомлений на панели задач.",
                    "Clipvey is already running. Look for its icon in the notification area of the taskbar."), "Clipvey",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        FileLog.Start();
        if (AppPaths.IsolatedTest)
            Core.Log.Write($"Режим проверки: данные в {AppPaths.DataDirectory}");
        Updater.RemoveLeftovers();
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Core.Log.Write($"Необработанная ошибка: {e.ExceptionObject}");

        // Из WinForms — NotifyIcon и скрытое окно ClipboardWatcher: их собственный контекст синхронизации не ставим,
        // контекст потока — диспетчер WPF.
        System.Windows.Forms.WindowsFormsSynchronizationContext.AutoInstall = false;
        // Подложку окон (Mica) тема WPF ставит сама — у панели своя, Acrylic, её задаёт PanelWindow.
        AppContext.SetSwitch("Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop", true);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.ThemeMode = Theme.Forced switch
        {
            true => ThemeMode.Dark,
            false => ThemeMode.Light,
            null => ThemeMode.System,
        };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Clipvey;component/Styles.xaml"),
        });
        app.DispatcherUnhandledException += (_, e) =>
        {
            Core.Log.Write($"Ошибка интерфейса: {e.Exception}");
            e.Handled = true;
        };

        TrayApplication? tray = null;
        app.Startup += (_, _) =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            // Виртуальные файлы кладутся в буфер через OleSetClipboard — с этого (STA) потока, с его циклом сообщений.
            OleClipboard.Initialize();
            try
            {
                tray = new TrayApplication();
            }
            catch (Exception e)
            {
                Core.Log.Write($"Не удалось запуститься: {e}");
                MessageBox.Show(L($"Не удалось запустить Clipvey:\n{e.Message}", $"Clipvey couldn’t start:\n{e.Message}"), "Clipvey",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                app.Shutdown();
            }
        };
        app.Run();
        GC.KeepAlive(tray);
    }

    private static bool WaitForPreviousInstance(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(30));
        }
        catch (AbandonedMutexException)
        {
            // Старая версия завершилась, не освободив мьютекс: он теперь наш.
            return true;
        }
    }
}
