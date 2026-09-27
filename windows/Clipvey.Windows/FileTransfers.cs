using Clipvey.Core;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Файлы и папки в буфере (docs/protocol.md, «Поведение сторон → Файлы в буфере»):
/// - скопированное здесь (CF_HDROP) уходит описанием (OfferFilesAsync); ошибка — в окошке;
/// - полученное всего до 50 МиБ — тихо скачивается в Incoming и кладётся в буфер настоящими файлами (CF_HDROP),
///   без окошка; окошко — только при ошибке. Новое содержимое (своё или с другого устройства) отменяет
///   незаконченное скачивание;
/// - больше 50 МиБ — в буфер кладутся виртуальные файлы (VirtualFiles.cs): скачиваются, когда Проводник
///   вставляет, с прогрессом в окошке.
/// Всё — на потоке интерфейса: события узла сюда переносит TrayApplication.
internal sealed class FileTransfers
{
    /// Всего до этого размера (включительно) файлы скачиваются сразу, тихо, в фоне.
    public const long BackgroundLimit = 50L * 1024 * 1024;

    private readonly ClipveyNode _node;
    private readonly ClipboardWatcher _watcher;
    private readonly Func<ToastWindow> _toast;
    private readonly Action<LastSync> _noteSync;
    private readonly SynchronizationContext _ui;

    /// Идущее фоновое скачивание (не больше одного: новое содержимое отменяет прежнее).
    private Download? _download;

    private sealed record Download(FileOfferReceived Received, CancellationTokenSource Cancel);

    public FileTransfers(ClipveyNode node, ClipboardWatcher watcher, Func<ToastWindow> toast, Action<LastSync> noteSync, SynchronizationContext ui)
    {
        _node = node;
        _watcher = watcher;
        _toast = toast;
        _noteSync = noteSync;
        _ui = ui;
    }

    /// Убрать из Incoming то, что старше суток (при запуске, в фоне).
    public static void CleanCache() => Task.Run(() =>
    {
        var removed = IncomingCache.Clean(AppPaths.IncomingDirectory);
        if (removed > 0)
            Log.Write($"Кэш полученных файлов: удалено старых папок — {removed}");
    });

    /// Режим проверки (--probe): обойти путь, как при отправке, и записать итог — без отправки и без чтения
    /// содержимого. Заодно — сколько там «точек повторной обработки» (ссылки, файлы облака по запросу).
    public static void Probe(string path) => Task.Run(() =>
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int links = 0, reparse = 0, cloud = 0, entries = 0;
        try
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, RecurseSubdirectories = true, IgnoreInaccessible = true };
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
            {
                entries++;
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    reparse++;
                // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS, RECALL_ON_OPEN, OFFLINE: содержимое в облаке.
                if (((int)entry.Attributes & (0x400000 | 0x40000 | 0x1000)) != 0)
                    cloud++;
                if (entry.LinkTarget is not null)
                    links++;
            }
        }
        catch (Exception e)
        {
            Log.Write($"Проба: обход не удался: {e.GetType().Name}: {e.Message}");
        }
        Log.Write($"Проба: записей {entries}, точек повторной обработки {reparse}, в облаке {cloud}, ссылок {links} ({stopwatch.ElapsedMilliseconds} мс)");
        stopwatch.Restart();
        try
        {
            var tree = FileTree.Build([path]);
            Log.Write($"Проба: в описание попало бы {tree.Items.Count} элементов, {tree.Total} байт ({stopwatch.ElapsedMilliseconds} мс)");
        }
        catch (FileOfferException e)
        {
            Log.Write($"Проба: описание не составлено ({e.Failure}): {e.Message}");
        }
    });

    // MARK: - Отправка

    /// Последние отправленные пути и когда: одно копирование бывает двумя изменениями буфера подряд
    /// (программа кладёт данные и сразу «закрепляет» их, OleFlushClipboard) — второе не отправляется.
    private (string[] Paths, long Time)? _lastSent;

    /// Скопированы файлы и папки: отправить описание устройствам с «file». Ошибки — в окошке.
    public async void Send(IReadOnlyList<string> paths)
    {
        var now = Environment.TickCount64;
        if (_lastSent is { } last && now - last.Time < 2000 && last.Paths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
        {
            Log.Write("Файлы из буфера: те же, что только что, — повтор не отправляется");
            return;
        }
        _lastSent = ([.. paths], now);
        Log.Write($"Файлы из буфера: {paths.Count} элементов верхнего уровня");
        FileOfferResult result;
        try
        {
            result = await _node.OfferFilesAsync(paths);
        }
        catch (Exception e)
        {
            Log.Write($"Файлы не отправлены: {e.Message}");
            return;
        }
        if (result.Failure is { } failure)
        {
            if (failure != FileOfferFailure.Disabled)
                _toast().ShowError(null, L("Файлы не отправлены", "Files weren’t sent"), UiText.OfferFailure(failure));
            return;
        }
        if (result.Recipients > 0)
            _noteSync(new LastSync(DateTime.Now, From: null, SyncKind.Files));
    }

    // MARK: - Получение

    /// Пришло описание файлов — новое содержимое буфера.
    public void Receive(FileOfferReceived received)
    {
        CancelDownload("пришли новые файлы");
        if (received.Offer.Total <= BackgroundLimit)
            DownloadInBackground(received);
        else
            PlaceVirtual(received);
    }

    /// Незаконченное фоновое скачивание больше не нужно: в буфере уже что-то новее.
    public void CancelDownload(string reason)
    {
        if (_download is not { } download)
            return;
        _download = null;
        Log.Write($"Скачивание файлов {download.Received.Offer.Id} отменено: {reason}");
        download.Cancel.Cancel();
    }

    private async void DownloadInBackground(FileOfferReceived received)
    {
        var offer = received.Offer;
        var download = new Download(received, new CancellationTokenSource());
        _download = download;
        // Буфер на момент прихода описания: если к концу скачивания он изменится, файлы не записываются.
        var sequence = ClipboardWatcher.Sequence;
        var target = IncomingCache.DirectoryFor(AppPaths.IncomingDirectory, offer.Id);
        Log.Write($"Файлы {offer.Id} от «{received.DeviceName}»: {offer.Items.Count} элементов, {offer.Total} байт — скачиваются в фоне");
        try
        {
            var paths = await Task.Run(() => DownloadAsync(received, target, download.Cancel.Token));
            if (!ReferenceEquals(_download, download))
            {
                Log.Write($"Файлы {offer.Id} скачаны, но в буфере уже новее — не записаны");
                return;
            }
            _download = null;
            if (_watcher.WriteRemoteFiles(paths, sequence))
                _noteSync(new LastSync(DateTime.Now, received.DeviceName, SyncKind.Files));
        }
        catch (Exception e)
        {
            if (ReferenceEquals(_download, download))
                _download = null;
            var failure = FileTransferFailures.Of(e);
            if (failure == FileTransferFailure.Cancelled)
                return;
            _toast().ShowError(null, L($"Файлы от «{received.DeviceName}» не получены", $"Files from “{received.DeviceName}” weren’t received"),
                UiText.TransferFailure(failure, received.DeviceName));
        }
        finally
        {
            download.Cancel.Dispose();
        }
    }

    /// Скачать; если сеанс сменился (оба устройства подключились одновременно) или коротко оборвался — заново,
    /// по новому сеансу (не больше двух повторов).
    private async Task<IReadOnlyList<string>> DownloadAsync(FileOfferReceived received, string target, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await _node.DownloadFilesAsync(received.Offer, received.DeviceId, target, null, ct);
            }
            catch (FileTransferException e) when (e.Failure == FileTransferFailure.DeviceUnavailable && attempt < 3
                && Sessions.WaitFor(_node, received.DeviceId, ct))
            {
                Log.Write($"Файлы {received.Offer.Id}: сеанс сменился — скачиваю заново");
            }
        }
    }

    private void PlaceVirtual(FileOfferReceived received)
    {
        var offer = received.Offer;
        var set = new VirtualFileSet(received);
        if (set.Skipped > 0)
            Log.Write($"Файлы {offer.Id}: {set.Skipped} элементов не предлагаются — путь длиннее {VirtualFileSet.MaxPath} символов");
        if (set.Entries.Count == 0)
        {
            _toast().ShowError(null, L($"Файлы от «{received.DeviceName}» не получены", $"Files from “{received.DeviceName}” weren’t received"),
                L($"Слишком глубокая вложенность: путь длиннее {VirtualFileSet.MaxPath} символов",
                    $"Folders are nested too deeply: paths are longer than {VirtualFileSet.MaxPath} characters"));
            return;
        }
        var data = new VirtualFileDataObject(set, _node,
            paste => _ui.Post(_ => OnPasteStarted(paste), null),
            paste => _ui.Post(_ => OnPasteEnded(paste), null));
        if (!_watcher.PlaceVirtualFiles(data))
        {
            _toast().ShowError(null, L($"Файлы от «{received.DeviceName}» не получены", $"Files from “{received.DeviceName}” weren’t received"),
                L("Буфер обмена занят другой программой", "The clipboard is busy in another app"));
            return;
        }
        Log.Write($"Файлы {offer.Id} от «{received.DeviceName}» в буфере виртуальными: {set.Entries.Count} элементов, {set.TotalBytes} байт — "
            + "скачаются при вставке");
        _noteSync(new LastSync(DateTime.Now, received.DeviceName, SyncKind.Files));
    }

    /// Проводник начал читать файлы: окошко с прогрессом.
    private void OnPasteStarted(VirtualPaste paste)
    {
        if (paste.Outcome != PasteOutcome.Running)
            return;
        var from = paste.Set.Source.DeviceName;
        _toast().ShowProgress(paste, L($"Загрузка с «{from}»", $"Downloading from “{from}”"),
            () => new ToastProgress(paste.Received, paste.Set.TotalBytes, paste.CurrentName), paste.Cancel);
    }

    /// Вставка закончилась: «Готово» (само исчезнет), ошибка или ничего (отменено).
    private void OnPasteEnded(VirtualPaste paste)
    {
        if (!paste.WasStarted)
            return;
        var toast = _toast();
        // Окошко уже занято другим сообщением — не перебивать его.
        if (toast.IsVisible && !toast.Shows(paste))
            return;
        var from = paste.Set.Source.DeviceName;
        switch (paste.Outcome)
        {
            case PasteOutcome.Done:
                var skipped = paste.Set.Skipped;
                toast.ShowDone(paste, L("Готово", "Done"), skipped > 0
                    ? L($"Не вставлено: {skipped} — слишком длинный путь", $"Not pasted: {skipped} — path too long")
                    : L($"Файлы от «{from}» вставлены", $"Files from “{from}” pasted"));
                break;
            case PasteOutcome.Failed:
                toast.ShowError(paste, L("Вставка прервана", "Paste interrupted"), UiText.TransferFailure(paste.Failure, from));
                break;
            default:
                toast.HideToast();
                break;
        }
    }

    /// Выход из программы: отменить скачивание, убрать свои виртуальные файлы из буфера.
    public void Shutdown()
    {
        CancelDownload("выход из программы");
        _watcher.ReleaseVirtualFiles();
    }
}
