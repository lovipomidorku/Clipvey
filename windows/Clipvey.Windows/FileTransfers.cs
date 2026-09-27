using Clipvey.Core;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Файлы и папки в буфере (docs/protocol.md, «Поведение сторон → Файлы в буфере»):
/// - скопированное здесь (CF_HDROP) уходит описанием (OfferFilesAsync); ошибка — в окошке;
/// - полученное всего не больше порога — тихо скачивается в Incoming и кладётся в буфер настоящими файлами
///   (CF_HDROP), без окошка; окошко — только при ошибке. Новое содержимое (своё или с другого устройства)
///   отменяет незаконченное скачивание;
/// - больше порога — окошко «… скопировал …» с «Загрузить»: файлы скачиваются в «Загрузки»\Clipvey с прогрессом
///   в окошке и кладутся в буфер, если он с нажатия не менялся.
///
/// Окошки — стопка сообщений, видно верхнее (как на Mac). Новое ложится сверху, поэтому идущая загрузка
/// не теряется: она продолжается под ним и снова видна, когда то закрыто. Окошко «Загрузить» одно: новое
/// содержимое с другого устройства его убирает (как следующий текст заменяет описание в буфере). Сообщение
/// об ошибке тоже одно: новое заменяет прежнее.
/// Всё — на потоке интерфейса: события узла сюда переносит TrayApplication.
internal sealed class FileTransfers
{
    /// Всего до этого размера (включительно) файлы скачиваются сразу, тихо, в фоне.
    public const long BackgroundLimit = 50L * 1024 * 1024;

    /// Сколько показывать «Готово», если на окошко не навели мышь.
    private static readonly TimeSpan DoneLifetime = TimeSpan.FromSeconds(6);

    private readonly ClipveyNode _node;
    private readonly ClipboardWatcher _watcher;
    private readonly Func<ToastWindow> _toast;
    private readonly Action<LastSync> _noteSync;

    /// Идущее фоновое скачивание (не больше одного: новое содержимое отменяет прежнее).
    private Download? _download;

    private sealed record Download(FileOfferReceived Received, CancellationTokenSource Cancel);

    /// Стопка окошек; видно последнее.
    private readonly List<Card> _cards = [];

    /// Выход из программы: окошки больше не показываются.
    private bool _closed;

    public FileTransfers(ClipveyNode node, ClipboardWatcher watcher, Func<ToastWindow> toast, Action<LastSync> noteSync)
    {
        _node = node;
        _watcher = watcher;
        _toast = toast;
        _noteSync = noteSync;
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

    /// Скопированы файлы и папки: отправить описание устройствам с «file». Ошибки — в окошке.
    /// Повтор того же копирования (второе изменение буфера) сюда не доходит: его отсекает ClipboardWatcher.
    public async void Send(IReadOnlyList<string> paths)
    {
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
                ShowNotice(L("Файлы не отправлены", "Files weren’t sent"), UiText.OfferFailure(failure));
            return;
        }
        if (result.Recipients > 0)
            _noteSync(new LastSync(DateTime.Now, From: null, SyncKind.Files));
    }

    // MARK: - Получение

    /// Пришло описание файлов — новое содержимое буфера.
    public void Receive(FileOfferReceived received)
    {
        RemoteContentArrived("пришли новые файлы");
        if (received.Offer.Total <= BackgroundLimit)
            DownloadInBackground(received);
        else
            OfferDownload(received);
    }

    /// Пришли текст, картинка или другое описание: незаконченное тихое скачивание больше не нужно, окошко
    /// «Загрузить» тоже устарело. Загрузки, начатые кнопкой, продолжаются.
    public void RemoteContentArrived(string reason)
    {
        CancelDownload(reason);
        DropOffers(reason);
    }

    /// Выключено «Передавать файлы»: тихое скачивание и окошко «Загрузить» убираются; начатые кнопкой загрузки
    /// продолжаются (пользователь сам попросил).
    public void FilesDisabled()
    {
        CancelDownload("передача файлов выключена");
        DropOffers("передача файлов выключена");
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
            await Task.Run(() => Prepare(target, offer.Total));
            // Смену сеанса и короткий обрыв посреди скачивания переживает узел (продолжает по новому сеансу).
            var paths = await Task.Run(() => _node.DownloadFilesAsync(offer, received.DeviceId, target, null, download.Cancel.Token));
            if (!ReferenceEquals(_download, download))
            {
                Log.Write($"Файлы {offer.Id} скачаны, но в буфере уже новее — не записаны");
                return;
            }
            _download = null;
            if (await _watcher.WriteRemoteFilesAsync(paths, sequence))
                _noteSync(new LastSync(DateTime.Now, received.DeviceName, SyncKind.Files));
        }
        catch (Exception e)
        {
            if (ReferenceEquals(_download, download))
                _download = null;
            var problem = Problem.Of(e, received.DeviceName, AppPaths.IncomingDirectory, inDownloads: false);
            if (problem.Failure == FileTransferFailure.Cancelled)
                return;
            Log.Write($"Файлы {offer.Id} не получены: {e.Message}");
            ShowNotice(problem.Title ?? L($"Файлы от «{received.DeviceName}» не получены", $"Files from “{received.DeviceName}” weren’t received"),
                problem.Message);
        }
        finally
        {
            download.Cancel.Dispose();
        }
    }

    // MARK: - «Загрузить»

    /// Больше порога: окошко «… скопировал …» с «Загрузить». Ничего не скачивается, пока не нажмут.
    private void OfferDownload(FileOfferReceived received)
    {
        Log.Write($"Файлы {received.Offer.Id} от «{received.DeviceName}»: {received.Offer.Items.Count} элементов, "
            + $"{received.Offer.Total} байт — больше порога, окошко «Загрузить»");
        Push(new Card(CardKind.Offer, received));
    }

    /// «Загрузить»: скачать в «Загрузки»\Clipvey с прогрессом в том же окошке, затем — в буфер, если он с нажатия
    /// не менялся (иначе файлы только в папке: «Показать в Проводнике» их откроет).
    private async void StartDownload(Card card)
    {
        if (card.Kind != CardKind.Offer || card.Received is not { } received)
            return;
        var offer = received.Offer;
        var cancel = new CancellationTokenSource();
        card.Cancel = cancel;
        card.Kind = CardKind.Downloading;
        Present();
        var sequence = ClipboardWatcher.Sequence;
        var directory = AppPaths.DownloadsDirectory;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Log.Write($"Загрузка {offer.Id} в {directory}: {offer.Items.Count} элементов, {offer.Total} байт");
        try
        {
            await Task.Run(() => Prepare(directory, offer.Total));
            var progress = new Counter(card);
            var paths = await Task.Run(() => _node.DownloadFilesAsync(offer, received.DeviceId, directory, progress, cancel.Token));
            var inClipboard = !_closed && await _watcher.WriteRemoteFilesAsync(paths, sequence);
            Log.Write($"Загрузка {offer.Id}: готово за {stopwatch.ElapsedMilliseconds} мс, {offer.Total} байт — "
                + (inClipboard ? "в буфере" : "только в папке (буфер с нажатия изменился)"));
            if (inClipboard)
                _noteSync(new LastSync(DateTime.Now, received.DeviceName, SyncKind.Files));
            card.Paths = paths;
            card.InClipboard = inClipboard;
            card.Kind = CardKind.Done;
        }
        catch (Exception e)
        {
            var problem = Problem.Of(e, received.DeviceName, directory, inDownloads: true);
            if (problem.Failure == FileTransferFailure.Cancelled)
            {
                Log.Write($"Загрузка {offer.Id}: отменена через {stopwatch.ElapsedMilliseconds} мс");
                _cards.Remove(card);
            }
            else
            {
                Log.Write($"Загрузка {offer.Id}: ошибка через {stopwatch.ElapsedMilliseconds} мс — {e.Message}");
                card.Problem = problem;
                card.Kind = CardKind.Failed;
            }
        }
        finally
        {
            card.Cancel = null;
            cancel.Dispose();
        }
        Present();
    }

    /// Папка есть, запись в неё разрешена (проверяется файлом: папка могла остаться с прошлого раза, а доступ
    /// с тех пор закрыт — например, контролируемым доступом к папкам) и места хватает. До запросов к источнику.
    private static void Prepare(string directory, long needed)
    {
        try
        {
            Directory.CreateDirectory(directory);
            using (File.Create(Path.Combine(directory, $".clipvey-probe-{Blob.NewId()[..8]}"), 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (UnauthorizedAccessException e)
        {
            throw new SaveException(SaveProblem.NoAccess, e.Message);
        }
        catch (IOException e)
        {
            throw new FileTransferException(FileTransferFailure.WriteFailed, e.Message);
        }
        var root = directory.EndsWith('\\') ? directory : directory + "\\"; // UNC — только с «\» в конце
        if (GetDiskFreeSpaceEx(root, out var available, out _, out _) && available < (ulong)needed)
            throw new SaveException(SaveProblem.NoSpace, $"нужно {needed} байт, свободно {available}") { Needed = needed, Available = (long)available };
    }

    // MARK: - Окошки

    private enum CardKind
    {
        /// Больше порога: «Загрузить» или закрыть.
        Offer,
        Downloading,
        /// Загружено; InClipboard — файлы положены в буфер.
        Done,
        /// Загрузка не удалась.
        Failed,
        /// Сообщение: файлы не отправлены или тихое скачивание не удалось.
        Notice,
    }

    /// Одно окошко стопки.
    private sealed class Card(CardKind kind, FileOfferReceived? received)
    {
        public CardKind Kind { get; set; } = kind;
        public FileOfferReceived? Received { get; } = received;
        public CancellationTokenSource? Cancel { get; set; }
        public IReadOnlyList<string>? Paths { get; set; }
        public bool InClipboard { get; set; }
        public Problem? Problem { get; set; }
        public string NoticeTitle { get; init; } = "";
        public string NoticeText { get; init; } = "";

        /// Получено байт (пишет поток сеанса, читает окошко).
        private long _received;
        public long ReceivedBytes => Interlocked.Read(ref _received);
        public void Report(long bytes) => Interlocked.Exchange(ref _received, bytes);
    }

    /// Прогресс скачивания — прямо в Card, без переноса на поток интерфейса на каждый кусок: окошко опрашивает сам.
    private sealed class Counter(Card card) : IProgress<long>
    {
        public void Report(long value) => card.Report(value);
    }

    private void Push(Card card)
    {
        _cards.Add(card);
        Present();
    }

    /// Сообщение об ошибке — одно: новое заменяет прежнее, а не копится под ним.
    private void ShowNotice(string title, string text)
    {
        _cards.RemoveAll(card => card.Kind == CardKind.Notice);
        Push(new Card(CardKind.Notice, null) { NoticeTitle = title, NoticeText = text });
    }

    private void DropOffers(string reason)
    {
        foreach (var card in _cards.Where(card => card.Kind == CardKind.Offer))
            Log.Write($"Окошко «Загрузить» для {card.Received?.Offer.Id} убрано: {reason}");
        if (_cards.RemoveAll(card => card.Kind == CardKind.Offer) > 0)
            Present();
    }

    /// Закрыть окошко («×», «Закрыть», само по времени). Идущая загрузка при этом отменяется, как «Отмена».
    private void Dismiss(Card card)
    {
        if (card.Kind == CardKind.Downloading)
        {
            card.Cancel?.Cancel();
            return;
        }
        if (card.Kind == CardKind.Offer)
            Log.Write($"Окошко «Загрузить» для {card.Received?.Offer.Id} закрыто");
        _cards.Remove(card);
        Present();
    }

    private void ShowInExplorer(Card card)
    {
        if (card.Paths is not { Count: > 0 } paths)
            return;
        Log.Write($"Показать в Проводнике: {paths[0]}");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{paths[0]}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"Не удалось открыть Проводник: {e.Message}");
        }
        Dismiss(card);
    }

    /// Показать верхнее окошко стопки (или спрятать, если стопка пуста).
    private void Present()
    {
        if (_closed)
            return;
        if (_cards.Count == 0)
        {
            _toast().HideToast();
            return;
        }
        var card = _cards[^1];
        _toast().Show(card, ViewOf(card));
    }

    private ToastView ViewOf(Card card)
    {
        var close = () => Dismiss(card);
        if (card.Kind == CardKind.Notice || card.Received is not { } received)
        {
            return new ToastView(ToastIcon.Warning, card.NoticeTitle)
            {
                TitleStrong = true,
                Detail = card.NoticeText,
                ActionText = L("Закрыть", "Close"),
                Action = close,
            };
        }
        var offer = received.Offer;
        var names = Names(offer);
        return card.Kind switch
        {
            // «MacBook скопировал «Отчёт» (+2 ещё), 1,2 ГБ» — имя устройства жирным.
            CardKind.Offer => new ToastView(offer.TopLevel.FirstOrDefault()?.IsDirectory == true ? ToastIcon.Folder : ToastIcon.File,
                L($" скопировал {names}, {UiText.Size(offer.Total)}", $" copied {names}, {UiText.Size(offer.Total)}"))
            {
                Bold = received.DeviceName,
                ActionText = L("Загрузить", "Download"),
                Action = () => StartDownload(card),
                ActionAccent = true,
                Close = close,
            },
            CardKind.Downloading => new ToastView(ToastIcon.Download, L($"Загрузка {names}", $"Downloading {names}"))
            {
                TitleStrong = true,
                Progress = () => new ToastProgress(Math.Min(card.ReceivedBytes, offer.Total), offer.Total),
                ActionText = L("Отмена", "Cancel"),
                Action = () => card.Cancel?.Cancel(),
            },
            CardKind.Done => new ToastView(ToastIcon.Done, L($"Готово: {names}", $"Done: {names}"))
            {
                TitleStrong = true,
                Detail = card.InClipboard
                    ? L("Скопировано — можно вставлять", "Copied — ready to paste")
                    : L("Сохранено в «Загрузки» → Clipvey", "Saved to Downloads → Clipvey"),
                ActionText = L("Показать в Проводнике", "Show in File Explorer"),
                Action = () => ShowInExplorer(card),
                Close = close,
                AutoHide = DoneLifetime,
                Expired = close,
            },
            _ => new ToastView(ToastIcon.Warning, card.Problem?.Title ?? L($"Не удалось загрузить {names}", $"Couldn’t download {names}"))
            {
                TitleStrong = true,
                Detail = card.Problem?.Message,
                ActionText = L("Закрыть", "Close"),
                Action = close,
            },
        };
    }

    /// «Отчёт.pdf» (+2 ещё) — первый элемент верхнего уровня (то, что скопировали) и сколько ещё.
    private static string Names(FileOffer offer)
    {
        var top = offer.TopLevel.ToList();
        var name = Shortened(top.FirstOrDefault()?.Path ?? "");
        var quoted = L($"«{name}»", $"“{name}”");
        return top.Count > 1 ? L($"{quoted} (+{top.Count - 1} ещё)", $"{quoted} (+{top.Count - 1} more)") : quoted;
    }

    /// Длинное имя — с многоточием в середине, расширение остаётся видно.
    private static string Shortened(string name, int limit = 44)
    {
        if (name.Length <= limit)
            return name;
        var tail = Math.Min(12, limit / 3);
        return $"{name[..(limit - tail - 1)]}…{name[^tail..]}";
    }

    // MARK: - Ошибки

    private enum SaveProblem
    {
        NoAccess,
        NoSpace,
    }

    /// Ошибка подготовки папки перед скачиванием (до запросов к другому устройству).
    private sealed class SaveException(SaveProblem problem, string message) : Exception(message)
    {
        public SaveProblem Problem { get; } = problem;
        public long Needed { get; init; }
        public long Available { get; init; }
    }

    /// Что не получилось, на языке интерфейса. Title — null: заголовок по месту («Не удалось загрузить …»).
    private sealed record Problem(FileTransferFailure Failure, string? Title, string Message)
    {
        public static Problem Of(Exception e, string device, string directory, bool inDownloads) => e switch
        {
            SaveException { Problem: SaveProblem.NoSpace } space => new(FileTransferFailure.WriteFailed, L("Недостаточно места", "Not enough space"),
                L($"Нужно {UiText.Size(space.Needed)}, свободно {UiText.Size(space.Available)}",
                    $"{UiText.Size(space.Needed)} needed, {UiText.Size(space.Available)} available")),
            SaveException => new(FileTransferFailure.WriteFailed, L("Нет доступа к папке", "No access to the folder"), inDownloads
                ? L("Clipvey не может записывать в «Загрузки» → Clipvey", "Clipvey can’t save to Downloads → Clipvey")
                : L($"Clipvey не может записывать в {directory}", $"Clipvey can’t save to {directory}")),
            _ => new(FileTransferFailures.Of(e), null, UiText.TransferFailure(FileTransferFailures.Of(e), device)),
        };
    }

    // MARK: - Выход

    /// Выход из программы: отменить скачивания (и тихое, и по «Загрузить») и убрать недокачанное —
    /// скрытые папки .clipvey-<id>-….part: узел удаляет их сам, но программа завершится раньше.
    public void Shutdown()
    {
        _closed = true;
        var unfinished = new List<(string Id, string Directory)>();
        if (_download is { } download)
            unfinished.Add((download.Received.Offer.Id, IncomingCache.DirectoryFor(AppPaths.IncomingDirectory, download.Received.Offer.Id)));
        CancelDownload("выход из программы");
        foreach (var card in _cards.Where(card => card.Kind == CardKind.Downloading))
        {
            unfinished.Add((card.Received!.Offer.Id, AppPaths.DownloadsDirectory));
            card.Cancel?.Cancel();
        }
        // Узел закрывает файлы не сразу после отмены — несколько попыток, всего не дольше полутора секунд.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var (id, directory) in unfinished)
        {
            while (Directory.Exists(directory))
            {
                try
                {
                    foreach (var part in Directory.EnumerateDirectories(directory, $".clipvey-{id}-*.part"))
                    {
                        Directory.Delete(part, recursive: true);
                        Log.Write($"Недокачанное удалено: {part}");
                    }
                    break;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (stopwatch.ElapsedMilliseconds > 1500)
                    {
                        Log.Write($"Недокачанное не удалено ({directory}): {e.Message}");
                        break;
                    }
                    Thread.Sleep(100);
                }
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true,
        EntryPoint = "GetDiskFreeSpaceExW")]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);
}
