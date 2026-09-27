using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Clipvey.Core;

/// Итог OfferFilesAsync: описание и скольким устройствам оно отправлено, или почему не отправлено.
public sealed record FileOfferResult(FileOffer? Offer, int Recipients, FileOfferFailure? Failure)
{
    public bool Succeeded => Failure is null;
}

/// Пришло описание файлов: от какого устройства (DeviceId — для DownloadFilesAsync и OpenFileStream) и его имя
/// (псевдоним, если задан). Описание — новое содержимое буфера, как текст.
public sealed record FileOfferReceived(FileOffer Offer, string DeviceId, string DeviceName);

/// Файлы и папки (docs/protocol.md, «Файлы»): описание уходит при копировании, содержимое получатель забирает
/// у источника кусками по запросу. Файлы не пересылаются по цепочке.
public sealed partial class ClipveyNode
{
    /// Сколько file_get держать отправленными наперёд при скачивании (источник всё равно обслуживает их по очереди).
    private const int MaxOutstandingRequests = 8;

    /// Старые описания (не последнее) обслуживаются не дольше часа после того, как их сменило новое.
    private static readonly TimeSpan OldOfferLifetime = TimeSpan.FromHours(1);

    /// Сколько старых описаний помнить.
    private const int MaxOldOffers = 16;

    private volatile bool _filesEnabled;

    /// Свои описания: id → описание и локальные пути. Под _lock.
    private readonly Dictionary<string, OfferRecord> _offers = new(StringComparer.Ordinal);
    private string? _latestOfferId;

    private sealed class OfferRecord(FileOffer offer, IReadOnlyList<string> localPaths)
    {
        public FileOffer Offer { get; } = offer;
        public IReadOnlyList<string> LocalPaths { get; } = localPaths;
        /// Когда описание перестало быть последним.
        public DateTime? SupersededAt { get; set; }
    }

    /// «Передавать файлы». Меняется через SetFilesEnabled.
    public bool FilesEnabled => _filesEnabled;

    /// Пришло описание файлов. Вызывается из потока сеанса: долгую работу (скачивание) — в другой задаче.
    public event Action<FileOfferReceived>? FileOffered;

    /// Включить или выключить «Передавать файлы»: новый caps в info всем сеансам. Выключено — описания не уходят
    /// и не принимаются, на запросы содержимого — not_found. Начатые скачивания не прерываются.
    public void SetFilesEnabled(bool enabled)
    {
        if (_filesEnabled == enabled)
            return;
        _filesEnabled = enabled;
        Log.Write($"Передача файлов {(enabled ? "включена" : "выключена")}");
        if (!enabled)
        {
            lock (_lock)
            {
                _offers.Clear();
                _latestOfferId = null;
            }
        }
        SendInfoToAll();
        Changed?.Invoke();
    }

    // MARK: - Отправитель

    /// Отправить описание выбранных файлов и папок напрямую подключённым включённым устройствам с file в caps
    /// (не по цепочке). Обход папок идёт в фоне. Содержимое потом отдаётся по запросам, пока описание последнее
    /// (и ещё час после этого).
    public async Task<FileOfferResult> OfferFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (!_filesEnabled)
        {
            Log.Write("Передача файлов выключена — файлы не отправлены");
            return new FileOfferResult(null, 0, FileOfferFailure.Disabled);
        }
        FileTree tree;
        try
        {
            tree = await Task.Run(() => FileTree.Build(paths), ct);
        }
        catch (FileOfferException e)
        {
            Log.Write($"Файлы не отправлены ({e.Failure}): {e.Message}");
            return new FileOfferResult(null, 0, e.Failure);
        }
        var offer = new FileOffer(Blob.NewId(), tree.Items, tree.Total);
        List<ActiveSession> targets;
        lock (_lock)
        {
            RegisterOffer(new OfferRecord(offer, tree.LocalPaths));
            targets = _sessions.Values
                .Where(session => !_disabled.Contains(session.PeerId) && session.Caps.Contains(Protocol.FileCapability))
                .ToList();
        }
        var message = offer.ToMessage();
        var recipients = 0;
        foreach (var session in targets)
        {
            try
            {
                await session.Info.Channel.SendAsync((JsonObject)message.DeepClone(), _stop.Token);
                recipients++;
            }
            catch (Exception e)
            {
                Log.Write($"Не удалось отправить описание файлов «{session.PeerName}»: {e.Message}");
                session.Close();
            }
        }
        Log.Write($"Описание файлов {offer.Id}: {offer.Items.Count} элементов, {offer.Total} байт — отправлено {recipients} устройствам");
        return new FileOfferResult(offer, recipients, null);
    }

    /// Под _lock: новое описание становится последним, старые живут час и не больше MaxOldOffers.
    private void RegisterOffer(OfferRecord record)
    {
        var now = DateTime.UtcNow;
        if (_latestOfferId is { } latest && _offers.TryGetValue(latest, out var previous))
            previous.SupersededAt = now;
        _offers[record.Offer.Id] = record;
        _latestOfferId = record.Offer.Id;
        PruneOffers(now);
    }

    private void PruneOffers(DateTime now)
    {
        var old = _offers.Values.Where(record => record.SupersededAt is not null).OrderBy(record => record.SupersededAt).ToList();
        for (var i = 0; i < old.Count; i++)
        {
            if (now - old[i].SupersededAt > OldOfferLifetime || old.Count - i > MaxOldOffers)
                _offers.Remove(old[i].Offer.Id);
        }
    }

    private void HandleFileMessage(JsonObject message, ActiveSession session)
    {
        try
        {
            switch (Messages.Type(message))
            {
                case "file_offer":
                    HandleFileOffer(FileOffer.Parse(message), session);
                    break;
                case "file_get":
                    HandleFileGet(message, session);
                    break;
                case "file_end":
                    session.Files.OnEnd(FileMessages.Req(message), FileMessages.ParseEndSize(message));
                    break;
                case "file_error":
                    session.Files.OnError(FileMessages.Req(message), FileMessages.ParseErrorReason(message));
                    break;
                case "file_cancel":
                    session.Files.Server.Cancel(FileMessages.Req(message));
                    break;
            }
        }
        catch (FileMessageException e)
        {
            Log.Write($"{Messages.Type(message)} от «{session.PeerName}» пропущено: {e.Message}");
        }
    }

    /// file_get: ответ ставится в очередь сеанса (в том числе not_found — ответы идут в порядке запросов).
    private void HandleFileGet(JsonObject message, ActiveSession session)
    {
        var req = FileMessages.Req(message);
        var (id, index, offset) = FileMessages.ParseGet(message);
        ServeJob job;
        lock (_lock)
        {
            PruneOffers(DateTime.UtcNow);
            if (_filesEnabled && _offers.TryGetValue(id, out var record) && index >= 0 && index < record.Offer.Items.Count
                && record.Offer.Items[index].Size is { } size && offset >= 0 && offset <= size)
            {
                job = new ServeJob(req, record.LocalPaths[index], size, offset, null);
            }
            else
            {
                job = new ServeJob(req, null, 0, 0, "not_found");
            }
        }
        session.Files.Server.Enqueue(job);
    }

    /// Запрос в очереди источника: файл с позиции Offset до конца (Size — размер на момент описания) или сразу ошибка.
    private sealed record ServeJob(uint Req, string? LocalPath, long Size, long Offset, string? Failure);

    // MARK: - Получатель

    private void HandleFileOffer(FileOffer offer, ActiveSession session)
    {
        if (!_filesEnabled)
        {
            Log.Write($"Описание файлов от «{session.PeerName}» пропущено: передача файлов выключена");
            return;
        }
        Log.Write($"Описание файлов от «{session.PeerName}»: {offer.Items.Count} элементов, {offer.Total} байт");
        FileOffered?.Invoke(new FileOfferReceived(offer, session.PeerId, DisplayName(session)));
    }

    // MARK: - Сеанс для запросов

    /// Сколько получатель ждёт сеанс с источником, если его нет: сеанс, по которому шёл запрос, закрылся
    /// (сменился при одновременном подключении или оборвался) или ещё не восстановился. Больший deviceId
    /// подключается сам только через 5 с после обрыва, плюс период обслуживания (5 с) и поиск — 15 с с запасом.
    /// Меняется только в проверках.
    internal TimeSpan SessionWaitTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// Живой сеанс с устройством, кроме lost (закрывшегося); null — сейчас нет.
    private ActiveSession? LiveSession(string deviceId, ActiveSession? lost)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(deviceId, out var session) && session != lost && !_disabled.Contains(deviceId)
                && !session.Cancellation.IsCancellationRequested && !session.Files.IsStopped
                ? session
                : null;
        }
    }

    /// Есть смысл ждать сеанс: узел работает и синхронизация с устройством не выключена.
    private bool CanWaitForSession(string deviceId)
    {
        if (_stop.IsCancellationRequested)
            return false;
        lock (_lock)
            return !_disabled.Contains(deviceId);
    }

    /// Файлы запрашиваются только у связанного устройства с включённой синхронизацией: иначе ждать нечего.
    private void RequireSessionPossible(string deviceId)
    {
        if (!CanWaitForSession(deviceId) || _store.Load().All(device => device.DeviceId != deviceId))
            throw new FileTransferException(FileTransferFailure.DeviceUnavailable, "устройство не связано или синхронизация с ним выключена");
    }

    /// Дождаться живого сеанса с устройством (кроме lost) до deadline. Нет — DeviceUnavailable.
    private async Task<ActiveSession> WaitForSessionAsync(string deviceId, ActiveSession? lost, DateTime deadline, CancellationToken ct)
    {
        while (true)
        {
            if (LiveSession(deviceId, lost) is { } session)
                return session;
            if (!CanWaitForSession(deviceId) || DateTime.UtcNow >= deadline)
            {
                throw new FileTransferException(FileTransferFailure.DeviceUnavailable,
                    lost is null ? "нет сеанса с устройством" : "сеанс с устройством оборвался, новый не появился");
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    /// Сеанс, по которому шли запросы файлов, закрылся (сменился при одновременном подключении или оборвался).
    /// Дождаться, пока его приём остановится (тогда известно, сколько по нему получено, и кусков больше не будет),
    /// и нового сеанса с тем же устройством — всего не дольше SessionWaitTimeout.
    private async Task<ActiveSession> ResumeSessionAsync(ActiveSession lost, CancellationToken ct)
    {
        lost.Close();
        var deadline = DateTime.UtcNow + SessionWaitTimeout;
        try
        {
            await lost.Files.Stopped.WaitAsync(SessionWaitTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new FileTransferException(FileTransferFailure.DeviceUnavailable, "сеанс с устройством не завершился");
        }
        return await WaitForSessionAsync(lost.PeerId, lost, deadline, ct).ConfigureAwait(false);
    }

    // MARK: - Скачивание

    /// Скачать всё описание в targetDirectory с сохранением структуры. Возвращает пути элементов верхнего уровня.
    /// - Пишет во временную скрытую папку в targetDirectory и переносит на место только после успеха; при ошибке
    ///   и отмене недокачанное удаляется. Совпадающие имена в targetDirectory получают номер: «отчёт (2).pdf».
    /// - Имена, недопустимые на этой системе, заменяются (FileNames.LocalPaths).
    /// - progress — сколько байт получено всего (из потока сеанса, после каждого куска).
    /// - Сеанса нет или он закрылся посреди скачивания (сменился, оборвался) — ждёт новый до SessionWaitTimeout
    ///   и продолжает по нему: незаконченные файлы запрашиваются с того места, где остановились, и дописываются.
    /// - Ошибки — FileTransferException (Failure — код для интерфейса); отмена — OperationCanceledException
    ///   (источнику уходит file_cancel).
    public async Task<IReadOnlyList<string>> DownloadFilesAsync(
        FileOffer offer, string deviceId, string targetDirectory, IProgress<long>? progress, CancellationToken ct)
    {
        RequireSessionPossible(deviceId);
        var session = LiveSession(deviceId, null)
            ?? await WaitForSessionAsync(deviceId, null, DateTime.UtcNow + SessionWaitTimeout, ct).ConfigureAwait(false);
        var local = FileNames.LocalPaths(offer.Items, OperatingSystem.IsWindows());
        string staging;
        try
        {
            Directory.CreateDirectory(targetDirectory);
            staging = Path.Combine(targetDirectory, $".clipvey-{offer.Id}-{Blob.NewId()[..8]}.part");
            var info = Directory.CreateDirectory(staging);
            if (OperatingSystem.IsWindows())
                info.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new FileTransferException(FileTransferFailure.WriteFailed, e.Message);
        }

        var stopwatch = Stopwatch.StartNew();
        long received = 0;
        var resumes = 0;
        // Запросы по порядку файлов; первый — самый ранний незаконченный. Источник отвечает в порядке запросов.
        var pending = new List<DownloadRequest>();
        try
        {
            for (var index = 0; index < offer.Items.Count; index++)
            {
                if (offer.Items[index].IsDirectory)
                    CreateDirectory(Path.Combine(staging, local[index]));
            }
            var next = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var lost = false;
                // После смены сеанса — незаконченные запросы заново, в прежнем порядке, с того места, где остановились.
                foreach (var request in pending)
                {
                    if (request.NeedsRequest && !await RequestAsync(session, offer.Id, request, ct).ConfigureAwait(false))
                    {
                        lost = true;
                        break;
                    }
                }
                // Новые запросы, пока наперёд отправлено меньше MaxOutstandingRequests.
                while (!lost && pending.Count < MaxOutstandingRequests && next < offer.Items.Count)
                {
                    var index = next++;
                    if (offer.Items[index].Size is not { } size)
                        continue;
                    var request = new DownloadRequest(index, Path.Combine(staging, local[index]), size, count =>
                    {
                        var total = Interlocked.Add(ref received, count);
                        progress?.Report(total);
                    });
                    pending.Add(request);
                    lost = !await RequestAsync(session, offer.Id, request, ct).ConfigureAwait(false);
                }
                if (!lost)
                {
                    if (pending.Count == 0)
                        break;
                    if (await WaitRequestAsync(pending[0], session, ct).ConfigureAwait(false))
                    {
                        pending.RemoveAt(0);
                        continue;
                    }
                }
                Log.Write($"Файлы {offer.Id}: сеанс с «{session.PeerName}» закрылся посреди скачивания — жду новый");
                session = await ResumeSessionAsync(session, ct).ConfigureAwait(false);
                resumes++;
                Log.Write($"Файлы {offer.Id}: продолжаю по новому сеансу с «{session.PeerName}», получено {Interlocked.Read(ref received)} байт");
            }

            var results = new List<string>();
            for (var index = 0; index < offer.Items.Count; index++)
            {
                if (!offer.Items[index].Path.Contains('/'))
                    results.Add(MoveToUnique(Path.Combine(staging, local[index]), targetDirectory, local[index], offer.Items[index].IsDirectory));
            }
            Log.Write($"Файлы {offer.Id} от «{session.PeerName}» скачаны: {offer.FileCount} файлов, {received} байт за {stopwatch.ElapsedMilliseconds} мс"
                + (resumes > 0 ? $", сеанс менялся {resumes} раз" : ""));
            return results;
        }
        catch (Exception e)
        {
            foreach (var request in pending)
                request.Cancel();
            Log.Write($"Файлы {offer.Id} от «{session.PeerName}» не скачаны ({FileTransferFailures.Of(e)}): {e.Message}");
            if (e is OperationCanceledException or FileTransferException)
                throw;
            // Запись, перенос и ожидание сеанса дают FileTransferException; остальное — неожиданная ошибка.
            throw new FileTransferException(FileTransferFailure.DeviceUnavailable, e.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Write($"Не удалось убрать недокачанное {staging}: {e.Message}");
            }
        }

        static void CreateDirectory(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new FileTransferException(FileTransferFailure.WriteFailed, e.Message);
            }
        }
    }

    /// Запросить у источника остаток файла (с того места, где остановились) по сеансу session.
    /// false — сеанс уже завершён или не удалось отправить (сеанс закрывается): запрос повторится по новому.
    private static async Task<bool> RequestAsync(ActiveSession session, string offerId, DownloadRequest request, CancellationToken ct)
    {
        (uint Req, long Offset) attached;
        try
        {
            if (request.Attach(session.Files) is not { } value)
                return true; // Уже закончен.
            attached = value;
        }
        catch (FileTransferException e) when (e.Failure == FileTransferFailure.DeviceUnavailable)
        {
            return false;
        }
        try
        {
            await session.Info.Channel.SendAsync(FileMessages.Get(offerId, attached.Req, request.Index, attached.Offset), ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            session.Close();
            return false;
        }
    }

    /// Дождаться ответа на запрос: true — файл получен; false — сеанс завершился раньше (запрос надо повторить
    /// по новому); ошибки источника и записи — исключением. Продолжение не выполняется синхронно в потоке, который
    /// завершил ожидание или отменил ct: иначе отмена из progress (он вызывается в потоке сеанса) продолжила бы
    /// скачивание — и код вызывающего — прямо в потоке сеанса, и сеанс перестал бы читать из сети.
    private static async Task<bool> WaitRequestAsync(DownloadRequest request, ActiveSession session, CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => cancelled.TrySetCanceled(ct)))
            await Task.WhenAny(request.Completion, session.Files.Stopped, cancelled.Task).ConfigureAwait(false);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        if (!request.Completion.IsCompleted)
            return false;
        await request.Completion.ConfigureAwait(false);
        return true;
    }

    /// Перенести готовый элемент верхнего уровня в directory; занятое имя — с номером.
    private static string MoveToUnique(string source, string directory, string name, bool isDirectory)
    {
        for (var number = 1; ; number++)
        {
            var destination = Path.Combine(directory, number == 1 ? name : FileNames.Numbered(name, number, isDirectory));
            if (File.Exists(destination) || Directory.Exists(destination))
                continue;
            try
            {
                if (isDirectory)
                    Directory.Move(source, destination);
                else
                    File.Move(source, destination);
                return destination;
            }
            catch (IOException) when ((File.Exists(destination) || Directory.Exists(destination)) && number < 1000)
            {
                // Имя заняли между проверкой и переносом — следующий номер.
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new FileTransferException(FileTransferFailure.WriteFailed, e.Message);
            }
        }
    }

    /// Поток «на чтение по запросу» для файла index описания (для виртуальных файлов Windows): данные запрашиваются
    /// у источника при первом чтении и выдаются по мере прихода.
    /// - Read блокирует, пока нет данных; ReadTimeout (по умолчанию 60 с) — сколько ждать, если за это время
    ///   по сеансу не пришло ни одного куска; ct и Dispose прерывают ожидание.
    /// - Seek и Position работают: запрос перезапускается с нового места (offset).
    /// - Если читатель отстаёт, запрос отменяется и потом возобновляется с нужного места — сеанс не ждёт читателя.
    /// - Сеанса нет или он закрылся (сменился, оборвался) — Read ждёт новый до SessionWaitTimeout и продолжает
    ///   по нему с того же места.
    /// - Ошибки — FileTransferException (это IOException) с кодом; устройство не связано или выключено — сразу.
    /// Потокобезопасен; несколько потоков разных файлов (и одного сеанса) можно читать одновременно.
    public Stream OpenFileStream(FileOffer offer, string deviceId, int index, CancellationToken ct = default)
    {
        if (index < 0 || index >= offer.Items.Count || offer.Items[index].Size is not { } size)
            throw new ArgumentOutOfRangeException(nameof(index), "Элемент описания — не файл");
        RequireSessionPossible(deviceId);
        return new RemoteFileStream(this, deviceId, LiveSession(deviceId, null), offer.Id, index, size, ct);
    }

    // MARK: - Проверки

    /// Сколько байт файлов отдано по запросам (всего, по всем сеансам).
    private long _servedFileBytes;

    /// Только для проверок (clipvey-peer --drop-after-bytes): один раз закрыть сеанс, когда отдано столько байт
    /// файлов. Меньше 0 — выключено.
    private long _dropAfterFileBytes = -1;

    internal long ServedFileBytes => Interlocked.Read(ref _servedFileBytes);

    internal void DropSessionOnceAfterFileBytes(long bytes) => Interlocked.Exchange(ref _dropAfterFileBytes, bytes);

    /// Отправлен кусок файла. Проверка (DropSessionOnceAfterFileBytes): если пора, закрыть сеанс, как при обрыве;
    /// true — закрыт.
    private bool OnChunkServed(ActiveSession session, int length)
    {
        var total = Interlocked.Add(ref _servedFileBytes, length);
        var limit = Interlocked.Read(ref _dropAfterFileBytes);
        if (limit < 0 || total < limit || Interlocked.CompareExchange(ref _dropAfterFileBytes, -1, limit) != limit)
            return false;
        Log.Write($"Проверка: сеанс с «{session.PeerName}» закрыт после {total} байт файлов");
        session.Close();
        return true;
    }

    // MARK: - Сеанс

    /// Файлы одного сеанса: обслуживание чужих file_get (Server) и приём кусков для своих запросов.
    private sealed class SessionFiles(ActiveSession session)
    {
        private readonly object _lock = new();
        private readonly Dictionary<uint, IFileSink> _sinks = [];
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private uint _lastReq;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;
        private bool _isStopped;

        public ActiveSession Session { get; } = session;

        public FileServer Server { get; } = new(session);

        /// Когда по сеансу последний раз приходил кусок, file_end или file_error (для тайм-аута потоков).
        public DateTime LastActivity => new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

        /// Сеанс завершён, и его цикл приёма вышел: кусков по его запросам больше не будет (Stop).
        public Task Stopped => _stopped.Task;

        public bool IsStopped
        {
            get
            {
                lock (_lock)
                    return _isStopped;
            }
        }

        public void Start(CancellationToken stop) => Server.Start(stop);

        /// Новый req: от 1, в пределах сеанса не повторяется. Сеанс завершён — FileTransferException(DeviceUnavailable).
        public uint Register(IFileSink sink)
        {
            lock (_lock)
            {
                if (_isStopped)
                    throw new FileTransferException(FileTransferFailure.DeviceUnavailable, "сеанс завершён");
                _lastReq = _lastReq == uint.MaxValue ? 1 : _lastReq + 1;
                _sinks[_lastReq] = sink;
                return _lastReq;
            }
        }

        public void Unregister(uint req)
        {
            lock (_lock)
                _sinks.Remove(req);
        }

        private IFileSink? Find(uint req, bool remove)
        {
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
            lock (_lock)
            {
                if (!_sinks.TryGetValue(req, out var sink))
                    return null;
                if (remove)
                    _sinks.Remove(req);
                return sink;
            }
        }

        /// Кусок для неизвестного req (отменённый запрос) пропускается.
        public void OnChunk(SessionFrame frame) => Find(frame.Req, remove: false)?.OnChunk(frame);

        public void OnEnd(uint req, long size) => Find(req, remove: true)?.OnEnd(size);

        public void OnError(uint req, string reason) =>
            Find(req, remove: true)?.Fail(new FileTransferException(FileTransferFailures.FromPeerReason(reason), $"источник ответил {reason}"));

        /// Отправить без ожидания (из потока сеанса и из Dispose). Ошибка отправки значит, что сеанс рвётся:
        /// он закрывается, и запросы продолжаются по новому сеансу.
        public void SendInBackground(JsonObject message) =>
            _ = Task.Run(async () =>
            {
                try
                {
                    await Session.Info.Channel.SendAsync(message, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    Session.Close();
                }
            });

        /// Сеанс завершён (цикл приёма вышел): ожидающим запросам — «сеанс закрылся» (они продолжатся по новому
        /// сеансу с того места, где остановились), обслуживание — остановить.
        public void Stop()
        {
            List<IFileSink> sinks;
            lock (_lock)
            {
                _isStopped = true;
                sinks = [.. _sinks.Values];
                _sinks.Clear();
            }
            foreach (var sink in sinks)
                sink.SessionLost(this);
            _stopped.TrySetResult();
            Server.Stop();
        }
    }

    /// Получатель данных одного запроса.
    private interface IFileSink
    {
        /// Кусок данных; может забрать буфер (frame.TakeChunk). Вызывается из потока сеанса.
        void OnChunk(SessionFrame frame);

        /// file_end.
        void OnEnd(long size);

        /// file_error.
        void Fail(Exception error);

        /// Сеанс files, по которому шёл запрос, завершился: кусков по нему больше не будет. Запрос не закончен —
        /// его повторят по новому сеансу с того места, где остановились.
        void SessionLost(SessionFiles files);
    }

    /// Запрос одного файла при скачивании: куски пишутся прямо на диск из потока сеанса (так сеанс не читает
    /// из сети быстрее, чем пишет диск). Файл открыт до конца: если сеанс закрылся, запрос повторяется по новому
    /// с того места, где остановился (Attach), и файл дописывается.
    private sealed class DownloadRequest : IFileSink
    {
        private readonly string _path;
        private readonly long _size;
        private readonly Action<long> _progress;
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _lock = new();
        private FileStream? _stream;
        private long _received;
        /// Сеанс, по которому идёт запрос, и req в нём; null — не запрошен (новый или сеанс закрылся).
        private SessionFiles? _files;
        private uint _req;
        /// С какого байта запрошено по текущему сеансу.
        private long _requestStart;

        public DownloadRequest(int index, string path, long size, Action<long> progress)
        {
            Index = index;
            _path = path;
            _size = size;
            _progress = progress;
            try
            {
                _stream = new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 0,
                    PreallocationSize = size,
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new FileTransferException(FileTransferFailure.WriteFailed, e.Message);
            }
        }

        /// Номер файла в описании.
        public int Index { get; }

        public Task Completion => _completion.Task;

        /// Запрос надо отправить (снова): он не закончен и не идёт ни по одному сеансу.
        public bool NeedsRequest
        {
            get
            {
                lock (_lock)
                    return _stream is not null && _files is null;
            }
        }

        /// Запросить по сеансу files: req и с какого байта; null — запрос уже закончен.
        /// Сеанс завершён — FileTransferException(DeviceUnavailable).
        public (uint Req, long Offset)? Attach(SessionFiles files)
        {
            lock (_lock)
            {
                if (_stream is null)
                    return null;
                _req = files.Register(this);
                _files = files;
                _requestStart = _received;
                return (_req, _received);
            }
        }

        public void SessionLost(SessionFiles files)
        {
            lock (_lock)
            {
                if (_files != files)
                    return;
                _files = null;
                _req = 0;
            }
        }

        public void OnChunk(SessionFrame frame)
        {
            var data = frame.ChunkData.Span;
            lock (_lock)
            {
                if (_stream is null)
                    return;
                if (_received + data.Length > _size)
                {
                    FailLocked(new FileTransferException(FileTransferFailure.ProtocolError, $"данных больше {_size} байт"), cancel: true);
                    return;
                }
                try
                {
                    _stream.Write(data);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    FailLocked(new FileTransferException(FileTransferFailure.WriteFailed, e.Message), cancel: true);
                    return;
                }
                _received += data.Length;
            }
            _progress(data.Length);
        }

        public void OnEnd(long size)
        {
            lock (_lock)
            {
                if (_stream is null)
                    return;
                _files = null;
                if (size != _received - _requestStart || _received != _size)
                {
                    FailLocked(new FileTransferException(FileTransferFailure.ProtocolError,
                        $"получено {_received - _requestStart} байт с {_requestStart}, по file_end {size}, размер {_size}"), cancel: false);
                    return;
                }
                try
                {
                    _stream.Dispose();
                    _stream = null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _stream = null;
                    _completion.TrySetException(new FileTransferException(FileTransferFailure.WriteFailed, e.Message));
                    return;
                }
            }
            _completion.TrySetResult();
        }

        public void Fail(Exception error)
        {
            lock (_lock)
            {
                _files = null;
                FailLocked(error, cancel: false);
            }
        }

        /// Отмена скачивания: источнику — file_cancel (если запрос идёт), файл закрывается (папку потом удаляет
        /// DownloadFilesAsync).
        public void Cancel()
        {
            lock (_lock)
                FailLocked(new OperationCanceledException(), cancel: true);
        }

        private void FailLocked(Exception error, bool cancel)
        {
            if (_stream is not null)
            {
                try
                {
                    _stream.Dispose();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Файл всё равно будет удалён вместе с временной папкой.
                }
                _stream = null;
                if (cancel && _files is { } files)
                {
                    files.Unregister(_req);
                    files.SendInBackground(FileMessages.Cancel(_req));
                }
            }
            _files = null;
            _req = 0;
            _completion.TrySetException(error);
            // Исключение наблюдается ожидающим; если ожидающего уже нет (отмена), не шуметь в UnobservedTaskException.
            _ = _completion.Task.Exception;
        }
    }

    /// Обслуживание file_get одного сеанса: запросы по очереди, в порядке поступления; файл читается кусками
    /// по 1 МиБ в фоне. Каждый кусок — отдельный кадр, поэтому ping, clip и info проходят между кусками.
    private sealed class FileServer(ActiveSession session)
    {
        private readonly Channel<ServeJob> _jobs = Channel.CreateUnbounded<ServeJob>(new UnboundedChannelOptions { SingleReader = true });
        private readonly object _lock = new();
        private readonly HashSet<uint> _pending = [];
        private readonly HashSet<uint> _cancelled = [];
        private int _files;
        private long _bytes;

        public void Start(CancellationToken stop) => _ = Task.Run(() => RunAsync(stop));

        public void Enqueue(ServeJob job)
        {
            lock (_lock)
                _pending.Add(job.Req);
            _jobs.Writer.TryWrite(job);
        }

        /// file_cancel: запрос в очереди выбрасывается, идущий прекращается перед следующим куском. Ответа нет.
        public void Cancel(uint req)
        {
            lock (_lock)
            {
                if (_pending.Contains(req))
                    _cancelled.Add(req);
            }
        }

        public void Stop() => _jobs.Writer.TryComplete();

        private bool IsCancelled(uint req)
        {
            lock (_lock)
                return _cancelled.Contains(req);
        }

        private async Task RunAsync(CancellationToken stop)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, session.Cancellation.Token);
            var ct = linked.Token;
            try
            {
                while (await _jobs.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (_jobs.Reader.TryRead(out var job))
                    {
                        try
                        {
                            if (!IsCancelled(job.Req))
                                await ServeAsync(job, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            lock (_lock)
                            {
                                _pending.Remove(job.Req);
                                _cancelled.Remove(job.Req);
                            }
                        }
                    }
                    if (_files > 0)
                    {
                        Log.Write($"Отдано «{session.PeerName}»: {_files} файлов, {_bytes} байт");
                        _files = 0;
                        _bytes = 0;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Сеанс завершён.
            }
            catch (Exception e)
            {
                Log.Write($"Отдача файлов «{session.PeerName}» прервана: {e.Message}");
                session.Close();
            }
        }

        private async Task ServeAsync(ServeJob job, CancellationToken ct)
        {
            var channel = session.Info.Channel;
            if (job.Failure is { } failure)
            {
                Log.Write($"Запрос {job.Req} от «{session.PeerName}»: {failure}");
                await channel.SendAsync(FileMessages.Error(job.Req, failure), ct).ConfigureAwait(false);
                return;
            }
            Microsoft.Win32.SafeHandles.SafeFileHandle handle;
            try
            {
                handle = File.OpenHandle(job.LocalPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.SequentialScan);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                await ReplyErrorAsync("changed", $"файла «{job.LocalPath}» больше нет").ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await ReplyErrorAsync("unavailable", e.Message).ConfigureAwait(false);
                return;
            }
            using (handle)
            {
                // Ошибки чтения — file_error; ошибка отправки (сеанс рвётся) уходит выше, в RunAsync.
                if (Length(handle) != job.Size)
                {
                    await ReplyErrorAsync("changed", $"размер «{job.LocalPath}» изменился или файл не читается").ConfigureAwait(false);
                    return;
                }
                using var frame = FileChunkFrame.Rent();
                long sent = 0;
                for (var position = job.Offset; position < job.Size;)
                {
                    if (IsCancelled(job.Req))
                    {
                        Log.Write($"Запрос {job.Req} от «{session.PeerName}» отменён после {sent} байт");
                        return;
                    }
                    var length = (int)Math.Min(Protocol.FileChunkBytes, job.Size - position);
                    int read;
                    try
                    {
                        read = ReadFully(handle, frame.Data.Span[..length], position);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        await ReplyErrorAsync("unavailable", e.Message).ConfigureAwait(false);
                        return;
                    }
                    if (read < length)
                    {
                        await ReplyErrorAsync("changed", $"«{job.LocalPath}» стал короче").ConfigureAwait(false);
                        return;
                    }
                    await channel.SendChunkAsync(frame, job.Req, length, ct).ConfigureAwait(false);
                    position += length;
                    sent += length;
                    if (session.Node.OnChunkServed(session, length))
                        return;
                }
                if (Length(handle) != job.Size)
                {
                    await ReplyErrorAsync("changed", $"размер «{job.LocalPath}» изменился во время передачи").ConfigureAwait(false);
                    return;
                }
                await channel.SendAsync(FileMessages.End(job.Req, sent), ct).ConfigureAwait(false);
                _files++;
                _bytes += sent;
            }

            async Task ReplyErrorAsync(string reason, string text)
            {
                Log.Write($"Запрос {job.Req} от «{session.PeerName}»: {reason} — {text}");
                await channel.SendAsync(FileMessages.Error(job.Req, reason), ct).ConfigureAwait(false);
            }
        }

        /// Размер открытого файла; −1 — не удалось узнать.
        private static long Length(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
        {
            try
            {
                return RandomAccess.GetLength(handle);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return -1;
            }
        }

        private static int ReadFully(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> buffer, long position)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = RandomAccess.Read(handle, buffer[total..], position + total);
                if (read == 0)
                    break;
                total += read;
            }
            return total;
        }
    }

    /// Поток OpenFileStream: данные файла по запросу, с перезапуском запроса при Seek и при отставании читателя.
    private sealed class RemoteFileStream : Stream, IFileSink
    {
        /// Столько данных может ждать читателя; больше — запрос отменяется и потом возобновляется с нужного места.
        private const long HighWater = 16L * 1024 * 1024;
        /// Возобновить запрос, когда у читателя осталось меньше этого.
        private const long LowWater = 4L * 1024 * 1024;

        private readonly ClipveyNode _node;
        private readonly string _deviceId;
        private readonly string _offerId;
        private readonly int _index;
        private readonly long _length;
        private readonly CancellationToken _ct;
        private readonly CancellationTokenRegistration _ctRegistration;
        private readonly object _lock = new();
        private readonly Queue<FileChunkData> _chunks = new();
        /// Сколько байт первого куска в очереди уже прочитано.
        private int _headConsumed;
        private long _buffered;
        private long _position;
        /// Откуда продолжать запрос: _position + _buffered.
        private long _nextOffset;
        /// С какого места идёт текущий запрос.
        private long _requestStart;
        /// req текущего запроса; 0 — запроса нет.
        private uint _activeReq;
        private Exception? _error;
        private DateTime _lastData = DateTime.UtcNow;
        private bool _disposed;
        /// Сеанс, по которому идут запросы; null — сеанса нет (не было при открытии или закрылся), ждём новый.
        private SessionFiles? _files;
        /// Сеанс, который закрылся: новый должен быть другим.
        private ActiveSession? _lostSession;
        /// С какого момента ждём сеанс; null — не ждём.
        private DateTime? _waitingSince;

        public RemoteFileStream(ClipveyNode node, string deviceId, ActiveSession? session, string offerId, int index, long length, CancellationToken ct)
        {
            _node = node;
            _deviceId = deviceId;
            _files = session?.Files;
            _offerId = offerId;
            _index = index;
            _length = length;
            _ct = ct;
            _ctRegistration = ct.Register(Pulse);
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override bool CanTimeout => true;

        /// Сколько ждать данных, если по сеансу за это время не пришло ни одного куска, мс.
        public override int ReadTimeout { get; set; } = 60_000;

        public override long Length => _length;

        public override long Position
        {
            get
            {
                lock (_lock)
                    return _position;
            }
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count), CancellationToken.None);

        public override int Read(Span<byte> buffer) => ReadCore(buffer, CancellationToken.None);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Run(() => ReadCore(buffer.Span, cancellationToken), cancellationToken));

        private int ReadCore(Span<byte> destination, CancellationToken ct)
        {
            if (destination.IsEmpty)
                return 0;
            using var registration = ct.Register(Pulse);
            lock (_lock)
            {
                while (true)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_buffered > 0)
                    {
                        var copied = CopyLocked(destination);
                        ResumeLocked();
                        return copied;
                    }
                    if (_position >= _length)
                        return 0;
                    if (_error is not null)
                        throw _error;
                    _ct.ThrowIfCancellationRequested();
                    ct.ThrowIfCancellationRequested();
                    ResumeLocked();
                    if (_error is not null)
                        throw _error;
                    // Пока ждём новый сеанс, срок задаёт SessionWaitTimeout, а не ReadTimeout.
                    if (_files is { } files)
                    {
                        var lastActivity = files.LastActivity > _lastData ? files.LastActivity : _lastData;
                        if (DateTime.UtcNow - lastActivity > TimeSpan.FromMilliseconds(ReadTimeout))
                        {
                            CancelActiveLocked();
                            _error = new FileTransferException(FileTransferFailure.Unavailable, $"нет данных {ReadTimeout / 1000} с");
                            throw _error;
                        }
                    }
                    Monitor.Wait(_lock, _files is null ? 100 : 250);
                }
            }
        }

        private int CopyLocked(Span<byte> destination)
        {
            var copied = 0;
            while (copied < destination.Length && _chunks.Count > 0)
            {
                var head = _chunks.Peek();
                var count = Math.Min(head.Length - _headConsumed, destination.Length - copied);
                head.Data.Span.Slice(_headConsumed, count).CopyTo(destination[copied..]);
                copied += count;
                _headConsumed += count;
                if (_headConsumed == head.Length)
                {
                    _chunks.Dequeue().Dispose();
                    _headConsumed = 0;
                }
            }
            _position += copied;
            _buffered -= copied;
            return copied;
        }

        /// Запросить данные с _nextOffset, если запроса нет, данные ещё нужны и читатель не отстал.
        /// Сеанса нет — взять новый, если он уже есть; нет дольше SessionWaitTimeout — DeviceUnavailable в _error.
        private void ResumeLocked()
        {
            if (_activeReq != 0 || _error is not null || _disposed || _nextOffset >= _length || _buffered >= LowWater)
                return;
            while (true)
            {
                if (_files is null)
                {
                    if (_node.LiveSession(_deviceId, _lostSession) is not { } session)
                    {
                        _waitingSince ??= DateTime.UtcNow;
                        if (!_node.CanWaitForSession(_deviceId) || DateTime.UtcNow - _waitingSince >= _node.SessionWaitTimeout)
                        {
                            _error = new FileTransferException(FileTransferFailure.DeviceUnavailable,
                                _lostSession is null ? "нет сеанса с устройством" : "сеанс с устройством оборвался, новый не появился");
                        }
                        return;
                    }
                    _files = session.Files;
                    _waitingSince = null;
                    if (_lostSession is not null)
                        Log.Write($"Чтение файла {_index} ({_offerId}): продолжаю по новому сеансу с «{session.PeerName}» с {_nextOffset} байт");
                }
                try
                {
                    _activeReq = _files.Register(this);
                    break;
                }
                catch (FileTransferException)
                {
                    // Сеанс уже завершён, а Stop нас не застал (запрос был отменён: читатель отставал).
                    LoseSessionLocked();
                }
            }
            _requestStart = _nextOffset;
            _lastData = DateTime.UtcNow;
            _files.SendInBackground(FileMessages.Get(_offerId, _activeReq, _index, _nextOffset));
        }

        /// Сеанс закрылся: полученное остаётся, запрос повторится по новому сеансу с _nextOffset.
        private void LoseSessionLocked()
        {
            _lostSession = _files?.Session;
            _files = null;
            _activeReq = 0;
            _waitingSince = DateTime.UtcNow;
        }

        private void CancelActiveLocked()
        {
            if (_activeReq == 0 || _files is null)
                return;
            _files.Unregister(_activeReq);
            _files.SendInBackground(FileMessages.Cancel(_activeReq));
            _activeReq = 0;
        }

        private void ReleaseChunksLocked()
        {
            while (_chunks.Count > 0)
                _chunks.Dequeue().Dispose();
            _headConsumed = 0;
            _buffered = 0;
        }

        public void OnChunk(SessionFrame frame)
        {
            lock (_lock)
            {
                if (_disposed || frame.Req != _activeReq)
                    return;
                var length = frame.ChunkData.Length;
                if (_nextOffset + length > _length)
                {
                    CancelActiveLocked();
                    _error = new FileTransferException(FileTransferFailure.ProtocolError, $"данных больше {_length} байт");
                    Monitor.PulseAll(_lock);
                    return;
                }
                _chunks.Enqueue(frame.TakeChunk());
                _buffered += length;
                _nextOffset += length;
                _lastData = DateTime.UtcNow;
                // Читатель отстал: не держать в памяти больше HighWater — запрос возобновится с _nextOffset.
                if (_buffered >= HighWater && _nextOffset < _length)
                    CancelActiveLocked();
                Monitor.PulseAll(_lock);
            }
        }

        public void OnEnd(long size)
        {
            lock (_lock)
            {
                if (_disposed || _activeReq == 0)
                    return;
                if (size != _nextOffset - _requestStart || _nextOffset != _length)
                {
                    _error = new FileTransferException(FileTransferFailure.ProtocolError,
                        $"по file_end {size} байт, получено {_nextOffset - _requestStart}, до конца файла {_length - _nextOffset}");
                }
                _activeReq = 0;
                Monitor.PulseAll(_lock);
            }
        }

        public void Fail(Exception error)
        {
            lock (_lock)
            {
                _activeReq = 0;
                _error ??= error;
                Monitor.PulseAll(_lock);
            }
        }

        public void SessionLost(SessionFiles files)
        {
            lock (_lock)
            {
                if (_disposed || files != _files)
                    return;
                LoseSessionLocked();
                Monitor.PulseAll(_lock);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var target = origin switch
                {
                    SeekOrigin.Begin => offset,
                    SeekOrigin.Current => _position + offset,
                    SeekOrigin.End => _length + offset,
                    _ => throw new ArgumentOutOfRangeException(nameof(origin)),
                };
                if (target < 0)
                    throw new IOException("Позиция перед началом файла");
                if (target > _position && target <= _position + _buffered)
                {
                    // Вперёд в пределах полученного — просто пропустить.
                    var skip = target - _position;
                    while (skip > 0)
                    {
                        var head = _chunks.Peek();
                        var count = (int)Math.Min(head.Length - _headConsumed, skip);
                        _headConsumed += count;
                        skip -= count;
                        if (_headConsumed == head.Length)
                        {
                            _chunks.Dequeue().Dispose();
                            _headConsumed = 0;
                        }
                    }
                    _buffered -= target - _position;
                    _position = target;
                }
                else if (target != _position)
                {
                    CancelActiveLocked();
                    ReleaseChunksLocked();
                    _position = target;
                    _nextOffset = target;
                }
                return target;
            }
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Pulse()
        {
            lock (_lock)
                Monitor.PulseAll(_lock);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    if (!_disposed)
                    {
                        _disposed = true;
                        CancelActiveLocked();
                        ReleaseChunksLocked();
                        Monitor.PulseAll(_lock);
                    }
                }
                _ctRegistration.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
