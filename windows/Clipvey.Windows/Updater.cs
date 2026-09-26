using System.Diagnostics;
using System.Reflection;
using Clipvey.Core;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Флаги командной строки для обновлений. Флаги режима проверки работают только вместе с --test:
///   --update-url URL         адрес releases/latest вместо GitHub (допускается http://127.0.0.1)
///   --update-public-key B64  открытый ключ подписи вместо зашитого (одноразовый ключ проверок)
///   --update-now             сразу проверить и, если есть новая версия, установить без вопроса
///   --update-check-delay N   первая автоматическая проверка через N секунд вместо 60
/// --after-update ставит сама программа, запуская новую версию: подождать выхода старой.
internal sealed record UpdaterOptions(
    bool Test, Uri? Url, string? PublicKey, bool CheckNow, TimeSpan? FirstDelay, bool AfterUpdate, string[] Arguments)
{
    public const string AfterUpdateFlag = "--after-update";

    public static UpdaterOptions Current { get; private set; } = Parse([]);

    public static void Load(string[] args) => Current = Parse(args);

    public static UpdaterOptions Parse(string[] args)
    {
        string? Value(string flag)
        {
            var index = Array.IndexOf(args, flag);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        var test = args.Contains("--test");
        var url = test && Uri.TryCreate(Value("--update-url"), UriKind.Absolute, out var parsed) ? parsed : null;
        var delay = test && double.TryParse(Value("--update-check-delay"), System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : (TimeSpan?)null;
        return new UpdaterOptions(test, url, test ? Value("--update-public-key") : null, test && args.Contains("--update-now"),
            delay, args.Contains(AfterUpdateFlag), args);
    }

    /// Флаги для новой версии: в режиме проверки — те же (кроме --update-now), и --after-update.
    public IEnumerable<string> RelaunchArguments =>
        (Test ? Arguments.Where(arg => arg is not "--update-now" and not AfterUpdateFlag) : []).Append(AfterUpdateFlag);
}

internal enum UpdatePhase
{
    Idle,
    Checking,
    Downloading,
    Installing,
}

/// Итог действия пользователя, который нужно показать словами.
internal enum UpdateNotice
{
    UpToDate,
    CheckFailed,
    NotWritable,
    DownloadFailed,
    VerificationFailed,
    InstallFailed,
}

/// Проверка и установка обновлений по docs/releases.md.
/// Проверка — через минуту после запуска и затем раз в сутки (время последней проверки хранится в настройках),
/// если включено «Проверять обновления автоматически». Установка: скачать и проверить Clipvey.exe во временной
/// папке, переименовать работающий exe в Clipvey.exe.old, поставить новый на его место, запустить, завершиться.
/// Все методы и события — на потоке интерфейса.
internal sealed class Updater : IDisposable
{
    private const string ExeAsset = "Clipvey.exe";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly Action _quit;
    private readonly UpdaterOptions _options = UpdaterOptions.Current;
    private readonly HttpClient _http;
    private readonly ReleaseClient _client;
    private readonly CancellationTokenSource _stop = new();
    /// В режиме проверки время проверки не сохраняется: каждый запуск проверяет заново.
    private DateTimeOffset? _testLastCheck;

    /// Что-то изменилось: панель перестраивается.
    public event Action? Changed;

    /// Автоматическая проверка нашла новую версию: показать уведомление у значка.
    public event Action<ReleaseVersion>? UpdateFound;

    public ReleaseVersion? CurrentVersion { get; } = ReleaseVersion.Parse(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public UpdatePhase Phase { get; private set; }
    public ReleaseInfo? Available { get; private set; }
    /// «Позже»: полоса скрыта до следующей проверки.
    public bool Dismissed { get; private set; }
    public UpdateNotice? Notice { get; private set; }
    private string _noticeFolder = "";

    public bool ShowsBanner => Available is not null && !Dismissed;

    public bool ChecksAutomatically
    {
        get => AppSettings.CheckUpdatesAutomatically;
        set => AppSettings.CheckUpdatesAutomatically = value;
    }

    private Uri Url => _options.Url ?? new Uri(ReleaseClient.DefaultUrl);
    private string PublicKey => _options.PublicKey ?? ReleaseVerifier.PublicKeyBase64;

    public Updater(Action quit)
    {
        _quit = quit;
        _http = ReleaseClient.CreateHttpClient(CurrentVersion?.ToString() ?? "0.0.0");
        _client = new ReleaseClient(_http, allowLoopbackHttp: _options.Test);
    }

    public string NoticeText => Notice switch
    {
        UpdateNotice.UpToDate => L("Установлена последняя версия", "You’re up to date"),
        UpdateNotice.CheckFailed => L("Не удалось проверить обновления", "Couldn’t check for updates"),
        UpdateNotice.NotWritable => L($"Нет прав на запись в папку «{_noticeFolder}». Скачайте новую версию вручную.",
            $"No permission to write to “{_noticeFolder}”. Download the new version manually."),
        UpdateNotice.DownloadFailed => L("Не удалось скачать обновление", "Couldn’t download the update"),
        UpdateNotice.VerificationFailed => L("Обновление не прошло проверку подписи и отменено", "The update failed the signature check and was cancelled"),
        UpdateNotice.InstallFailed => L("Не удалось установить обновление", "Couldn’t install the update"),
        _ => "",
    };

    // MARK: - Расписание

    public void Start()
    {
        if (CurrentVersion is null)
        {
            Log.Write("Обновления: версия программы не определена — проверка отключена");
            return;
        }
        Log.Write($"Обновления: текущая версия {CurrentVersion}, адрес {Url}{(_options.Test ? " (режим проверки)" : "")}");
        _ = RunAsync(_stop.Token);
    }

    private async Task RunAsync(CancellationToken stop)
    {
        try
        {
            if (_options.CheckNow)
            {
                await CheckAsync(manual: true);
                if (Available is not null)
                    await InstallAsync();
            }
            await Task.Delay(_options.FirstDelay ?? TimeSpan.FromMinutes(1), stop);
            while (true)
            {
                if (ChecksAutomatically && IsDue)
                    await CheckAsync(manual: false);
                // Раз в час смотрим, не пора ли: так расписание переживает сон компьютера и смену настройки.
                await Task.Delay(TimeSpan.FromHours(1), stop);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log.Write($"Обновления: ошибка расписания: {e}");
        }
    }

    private DateTimeOffset? LastCheck
    {
        get => _options.Test ? _testLastCheck : AppSettings.LastUpdateCheck;
        set
        {
            if (_options.Test)
                _testLastCheck = value;
            else
                AppSettings.LastUpdateCheck = value;
        }
    }

    private bool IsDue
    {
        get
        {
            if (LastCheck is not { } last)
                return true;
            var elapsed = DateTimeOffset.UtcNow - last;
            // Часы переведены назад — считаем, что пора.
            return elapsed >= CheckInterval || elapsed < TimeSpan.Zero;
        }
    }

    // MARK: - Проверка

    /// manual — кнопка «Проверить сейчас»: итог показывается словами. Автоматическая проверка молчит при ошибках.
    public async Task CheckAsync(bool manual)
    {
        if (CurrentVersion is not { } current || Phase != UpdatePhase.Idle)
            return;
        Phase = UpdatePhase.Checking;
        if (manual)
            Notice = null;
        Changed?.Invoke();
        LastCheck = DateTimeOffset.UtcNow;
        var result = await _client.CheckAsync(Url, current, _stop.Token);
        Phase = UpdatePhase.Idle;
        Log.Write($"Обновления ({(manual ? "ручная" : "автоматическая")} проверка): {result.Detail}");
        switch (result.Status)
        {
            case UpdateCheckStatus.Newer:
                Available = result.Release;
                // Каждая проверка (то есть раз в сутки) снова напоминает, даже если нажимали «Позже».
                Dismissed = false;
                if (!manual)
                    UpdateFound?.Invoke(result.Release!.Version);
                break;
            case UpdateCheckStatus.UpToDate:
                Available = null;
                if (manual)
                    Notice = UpdateNotice.UpToDate;
                break;
            default:
                if (manual)
                    Notice = UpdateNotice.CheckFailed;
                break;
        }
        Changed?.Invoke();
    }

    /// «Позже».
    public void Dismiss()
    {
        Dismissed = true;
        Changed?.Invoke();
    }

    // MARK: - Установка

    public async Task InstallAsync()
    {
        if (Available is not { } release || Phase != UpdatePhase.Idle)
            return;
        Notice = null;

        var exe = Environment.ProcessPath;
        if (exe is null || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Fail(UpdateNotice.InstallFailed, $"путь программы не exe: {exe}");
            return;
        }
        var folder = Path.GetDirectoryName(exe)!;
        if (!CanWrite(folder))
        {
            _noticeFolder = folder;
            Fail(UpdateNotice.NotWritable, $"нет прав на запись в {folder}");
            return;
        }

        Phase = UpdatePhase.Downloading;
        Changed?.Invoke();
        var work = Path.Combine(Path.GetTempPath(), "ClipveyUpdate-" + Guid.NewGuid().ToString("N"));
        try
        {
            string downloaded;
            try
            {
                downloaded = await _client.DownloadVerifiedAsync(release, ExeAsset, work, PublicKey, _stop.Token);
            }
            catch (ReleaseException e)
            {
                Phase = UpdatePhase.Idle;
                Fail(e.Failure == ReleaseFailure.Verification ? UpdateNotice.VerificationFailed : UpdateNotice.DownloadFailed, e.Message);
                return;
            }
            Log.Write($"Обновления: подпись и SHA-256 {ExeAsset} {release.Version} верны");

            Phase = UpdatePhase.Installing;
            Changed?.Invoke();
            try
            {
                Replace(exe, downloaded);
                StartNew(exe);
            }
            catch (Exception e)
            {
                Phase = UpdatePhase.Idle;
                Fail(UpdateNotice.InstallFailed, $"замена exe: {e.Message}");
                return;
            }
        }
        finally
        {
            TryDeleteDirectory(work);
        }
        Log.Write($"Обновления: установлена {release.Version}, новая версия запущена — завершаюсь");
        _quit();
    }

    private void Fail(UpdateNotice notice, string detail)
    {
        Log.Write($"Обновления: {detail}");
        Notice = notice;
        Changed?.Invoke();
    }

    /// Работающий exe переименовывается (Windows это разрешает), новый встаёт на его место.
    private static void Replace(string exe, string downloaded)
    {
        var staged = exe + ".new";
        var old = exe + ".old";
        // Сначала в папку exe: тогда последние переименования — на одном диске.
        File.Move(downloaded, staged, overwrite: true);
        if (File.Exists(old))
            File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(staged, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    /// Запустить новую версию. Не запустилась — вернуть старую на место.
    private void StartNew(string exe)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var argument in _options.RelaunchArguments)
            start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("процесс не создан");
        }
        catch
        {
            var old = exe + ".old";
            File.Move(exe, exe + ".new", overwrite: true);
            File.Move(old, exe);
            throw;
        }
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            using var probe = File.Create(Path.Combine(folder, $".clipvey-write-{Guid.NewGuid():N}"), 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Обновления: не удалось удалить {path}: {e.Message}");
        }
    }

    /// При запуске: удалить Clipvey.exe.old (и недоустановленный .new) от прошлого обновления.
    /// Старая версия может ещё завершаться и держать файл — несколько попыток в фоне.
    public static void RemoveLeftovers()
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        var files = new[] { exe + ".old", exe + ".new" }.Where(File.Exists).ToList();
        if (files.Count == 0)
            return;
        _ = Task.Run(async () =>
        {
            foreach (var file in files)
            {
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Delete(file);
                        Log.Write($"Обновления: удалён {Path.GetFileName(file)}");
                        break;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        if (attempt >= 20)
                        {
                            Log.Write($"Обновления: не удалось удалить {Path.GetFileName(file)}: {e.Message}");
                            break;
                        }
                        await Task.Delay(500);
                    }
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
    }
}
