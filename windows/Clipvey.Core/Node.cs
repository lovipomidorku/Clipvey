using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Clipvey.Core;

/// Связанное устройство для интерфейса.
/// Name — имя, которое устройство сообщает о себе; Alias — локальный псевдоним (SetAlias), null — нет.
/// Problem — почему не удаётся подключиться (если известно); текст подбирает интерфейс.
/// AcceptsImages — есть сеанс и в его caps есть image: картинки этому устройству отправляются.
public sealed record DeviceStatus(
    string DeviceId,
    string Name,
    bool Enabled,
    bool Connected,
    FailureReason? Problem,
    string? Alias = null,
    DeviceType? Type = null,
    bool AcceptsImages = false)
{
    /// Что показывать: псевдоним или имя.
    public string DisplayName => Alias ?? Name;
}

/// Что известно о другом устройстве после ready или info. OldName — имя до этого сообщения (для «переименовано»).
public sealed record PeerUpdate(string DeviceId, string OldName, string Name, DeviceType Type, IReadOnlyCollection<string> Caps);

/// Узел Clipvey:
/// - слушает TCP и объявляет себя через mDNS;
/// - находит связанные устройства и держит с каждым не больше одного сеанса;
/// - пересылает фрагменты буфера (текст и картинки);
/// - ведёт связывание в обеих ролях.
/// Правила — docs/protocol.md. События вызываются из фоновых потоков.
/// Узел платформенно-нейтральный: имя, тип устройства и «Передавать картинки» задаёт и хранит приложение.
public sealed class ClipveyNode : IAsyncDisposable
{
    /// Сколько картинок может ждать отправки в одном сеансе; при переполнении отбрасывается самая старая.
    private const int MaxQueuedImages = 3;

    private readonly Identity _identity;
    private readonly DeviceStore _store;
    private readonly int _preferredPort;
    private readonly object _lock = new();
    private readonly Dictionary<string, ActiveSession> _sessions = [];
    private readonly Dictionary<string, List<(string Host, int Port)>> _discovered = [];
    /// Порт, который объявляет через mDNS любое найденное устройство (в том числе ещё не связанное).
    private readonly Dictionary<string, int> _advertisedPorts = [];
    private readonly Dictionary<string, DateTime> _disconnectedSince = [];
    private readonly Dictionary<string, FailureReason> _problems = [];
    private readonly Dictionary<string, DateTime> _retryAfter = [];
    private readonly HashSet<string> _disabled = [];
    private readonly HashSet<string> _seenClips = [];
    private readonly Queue<string> _seenOrder = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly List<Task> _background = [];
    private List<DiscoveredDevice> _candidates = [];
    private DateTime? _pairingModeUntil;
    private bool _pairingBusy;
    private IncomingPairing? _incoming;
    private TcpListener? _listener;
    private MdnsAdvertiser? _advertiser;
    private string _name;
    private volatile bool _imagesEnabled;

    /// deviceType — свои os и form; imagesEnabled — «Передавать картинки» при запуске.
    public ClipveyNode(
        Identity identity,
        DeviceStore store,
        string name,
        int preferredPort = Protocol.DefaultPort,
        DeviceType? deviceType = null,
        bool imagesEnabled = true)
    {
        _identity = identity;
        _store = store;
        _preferredPort = preferredPort;
        var normalized = NormalizeName(name);
        _name = normalized.Length > 0 ? normalized : "Clipvey";
        DeviceType = deviceType ?? DeviceType.Unknown;
        _imagesEnabled = imagesEnabled;
    }

    public string DeviceId => _identity.DeviceId;

    /// Своё имя: TXT, имя экземпляра mDNS, pair_*, ready и info. Меняется через SetName.
    public string Name
    {
        get
        {
            lock (_lock)
                return _name;
        }
    }

    public DeviceType DeviceType { get; }

    /// «Передавать картинки». Меняется через SetImagesEnabled.
    public bool ImagesEnabled => _imagesEnabled;

    public int Port { get; private set; }

    /// Изменились связанные устройства, их состояние или список кандидатов для связывания.
    public event Action? Changed;

    /// Пришёл текст (переводы строк — \n) и имя устройства-отправителя (псевдоним, если задан).
    public event Action<string, string>? ClipReceived;

    /// Пришла картинка: данные (sha256 проверен), mime (image/png или image/jpeg), имя устройства-отправителя.
    public event Action<byte[], string, string>? ImageReceived;

    /// Другое устройство сообщило имя, тип и caps (ready при подключении или info).
    public event Action<PeerUpdate>? PeerUpdated;

    /// Изменилось входящее связывание (роль R); null — связывание закончилось.
    public event Action<IncomingPairing?>? IncomingPairingChanged;

    /// Связывание удалось; аргумент — имя другого устройства.
    public event Action<string>? PairingSucceeded;

    /// Связывание не удалось; текст по коду подбирает интерфейс.
    public event Action<FailureReason>? PairingFailed;

    /// Имя без пробелов по краям и не длиннее 63 байт UTF-8 (предел метки DNS для имени экземпляра).
    public static string NormalizeName(string raw)
    {
        var result = new StringBuilder();
        var bytes = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(raw.Trim());
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var length = Encoding.UTF8.GetByteCount(element);
            if (bytes + length > 63)
                break;
            result.Append(element);
            bytes += length;
        }
        return result.ToString();
    }

    public void Start()
    {
        _listener = OpenListener();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Log.Write($"Устройство «{Name}» ({DeviceId}) слушает TCP-порт {Port}");
        try
        {
            var advertiser = new MdnsAdvertiser(Name, $"clipvey-{DeviceId[..8]}", Port, Txt);
            advertiser.Start();
            _advertiser = advertiser;
        }
        catch (Exception e)
        {
            Log.Write($"mDNS-объявление недоступно ({e.Message}): это устройство найдут только после его собственного подключения");
        }
        lock (_lock)
        {
            foreach (var device in _store.Load())
            {
                _disconnectedSince[device.DeviceId] = DateTime.UtcNow;
                if (!device.Enabled)
                    _disabled.Add(device.DeviceId);
            }
        }
        _background.Add(Task.Run(() => AcceptLoopAsync(_stop.Token)));
        _background.Add(Task.Run(() => MaintenanceLoopAsync(_stop.Token)));
    }

    public bool IsPairingMode
    {
        get
        {
            lock (_lock)
                return _pairingModeUntil is { } until && DateTime.UtcNow < until;
        }
    }

    public IReadOnlyList<DeviceStatus> Devices
    {
        get
        {
            var stored = _store.Load();
            lock (_lock)
            {
                return stored
                    .Select(device =>
                    {
                        _sessions.TryGetValue(device.DeviceId, out var session);
                        return new DeviceStatus(
                            device.DeviceId,
                            device.Name,
                            device.Enabled,
                            session is not null,
                            _problems.TryGetValue(device.DeviceId, out var problem) ? problem : null,
                            device.Alias,
                            device.Type,
                            session?.Caps.Contains(Protocol.ImageCapability) ?? false);
                    })
                    .ToList();
            }
        }
    }

    /// Устройства в режиме связывания, с которыми ещё нет связи.
    public IReadOnlyList<DiscoveredDevice> PairingCandidates
    {
        get
        {
            lock (_lock)
                return _candidates;
        }
    }

    public IncomingPairing? IncomingPairing
    {
        get
        {
            lock (_lock)
                return _incoming;
        }
    }

    // MARK: - Настройки

    /// Сменить своё имя: TXT и имя экземпляра mDNS («прощание» старого имени), info всем сеансам.
    public void SetName(string newName)
    {
        var normalized = NormalizeName(newName);
        string old;
        lock (_lock)
        {
            if (normalized.Length == 0 || normalized == _name)
                return;
            old = _name;
            _name = normalized;
        }
        Log.Write($"Имя устройства: «{old}» → «{normalized}»");
        try
        {
            _advertiser?.Rename(normalized);
        }
        catch (Exception e)
        {
            Log.Write($"mDNS: не удалось объявить новое имя: {e}");
        }
        SendInfoToAll();
        Changed?.Invoke();
    }

    /// Задать локальный псевдоним устройства; пустая строка — сбросить. Другим устройствам не передаётся.
    public void SetAlias(string deviceId, string alias)
    {
        var trimmed = alias.Trim();
        _store.SetAlias(deviceId, trimmed.Length == 0 ? null : trimmed);
        Log.Write($"Псевдоним {deviceId}: «{trimmed}»");
        Changed?.Invoke();
    }

    /// Включить или выключить «Передавать картинки»: новый caps в info всем сеансам.
    public void SetImagesEnabled(bool enabled)
    {
        if (_imagesEnabled == enabled)
            return;
        _imagesEnabled = enabled;
        Log.Write($"Передача картинок {(enabled ? "включена" : "выключена")}");
        if (!enabled)
        {
            lock (_lock)
            {
                foreach (var session in _sessions.Values)
                    session.Outgoing.Clear();
            }
        }
        SendInfoToAll();
        Changed?.Invoke();
    }

    private PeerInfo OwnInfo => new(Name, DeviceType, _imagesEnabled ? [Protocol.ImageCapability] : []);

    private void SendInfoToAll()
    {
        List<ActiveSession> targets;
        lock (_lock)
            targets = [.. _sessions.Values];
        var message = OwnInfo.ToMessage("info");
        foreach (var session in targets)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await session.Info.Channel.SendAsync((JsonObject)message.DeepClone(), _stop.Token);
                }
                catch (Exception e)
                {
                    Log.Write($"Не удалось отправить info «{session.PeerName}»: {e.Message}");
                }
            });
        }
    }

    // MARK: - Связывание

    public void StartPairingMode()
    {
        lock (_lock)
            _pairingModeUntil = DateTime.UtcNow + Protocol.PairingTimeout;
        Log.Write("Режим связывания открыт");
        _advertiser?.Announce();
        Wake();
        Changed?.Invoke();
    }

    public void StopPairingMode()
    {
        IncomingPairing? incoming;
        lock (_lock)
        {
            if (_pairingModeUntil is null)
                return;
            _pairingModeUntil = null;
            _candidates = [];
            incoming = _incoming;
        }
        incoming?.Cancel();
        Log.Write("Режим связывания закрыт");
        _advertiser?.Announce();
        Changed?.Invoke();
    }

    /// Роль I: связаться с устройством из списка кандидатов. Код вводит пользователь этого устройства.
    public async Task<PairedDevice> PairWithAsync(
        DiscoveredDevice device,
        Func<string, CancellationToken, Task<string?>> requestCode,
        Action? codeAccepted,
        CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_pairingBusy)
                throw new ProtocolException("Уже идёт связывание");
            _pairingBusy = true;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            timeout.CancelAfter(Protocol.PairingTimeout);
            var endpoints = device.Addresses.Select(address => (address.ToString(), device.Port));
            var (tcp, host, port) = await Net.ConnectAnyAsync(endpoints, timeout.Token);
            using (tcp)
            {
                var paired = await Pairing.InitiateAsync(tcp.GetStream(), _identity, Name, DeviceType, requestCode, codeAccepted, timeout.Token);
                SavePaired(paired, host, port);
                return paired;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !_stop.IsCancellationRequested)
        {
            Log.Write($"Связывание не удалось: {e.Message}");
            PairingFailed?.Invoke(Failures.Of(e));
            throw;
        }
        finally
        {
            lock (_lock)
                _pairingBusy = false;
        }
    }

    public void Unpair(string deviceId)
    {
        _store.Remove(deviceId);
        ActiveSession? session;
        lock (_lock)
        {
            _sessions.TryGetValue(deviceId, out session);
            _disconnectedSince.Remove(deviceId);
            _discovered.Remove(deviceId);
            _problems.Remove(deviceId);
            _retryAfter.Remove(deviceId);
            _disabled.Remove(deviceId);
        }
        session?.Close();
        Log.Write($"Связь с {deviceId} разорвана");
        Changed?.Invoke();
    }

    /// Включить или выключить синхронизацию с устройством, не разрывая связь.
    public void SetEnabled(string deviceId, bool enabled)
    {
        _store.SetEnabled(deviceId, enabled);
        ActiveSession? session = null;
        lock (_lock)
        {
            _problems.Remove(deviceId);
            _retryAfter.Remove(deviceId);
            if (enabled)
            {
                _disabled.Remove(deviceId);
                _disconnectedSince[deviceId] = DateTime.UtcNow;
            }
            else
            {
                _disabled.Add(deviceId);
                _sessions.TryGetValue(deviceId, out session);
            }
        }
        session?.Close();
        Log.Write($"Синхронизация с {deviceId} {(enabled ? "включена" : "выключена")}");
        if (enabled)
            Wake();
        Changed?.Invoke();
    }

    // MARK: - Текст

    /// Отправить текст, скопированный на этом устройстве, всем подключённым устройствам.
    public Task BroadcastClipAsync(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > Protocol.MaxClipBytes)
        {
            Log.Write("Текст больше 1 МиБ — не отправлен");
            return Task.CompletedTask;
        }
        var id = Blob.NewId();
        MarkSeen(id);
        return SendClipAsync(new Clip(id, DeviceId, 0, text), exceptDeviceId: null);
    }

    private sealed record Clip(string Id, string Origin, int Hops, string Text);

    /// true — фрагмент новый; false — уже был (пришёл другим путём). Список общий для текста и картинок.
    private bool MarkSeen(string id)
    {
        lock (_lock)
        {
            if (!_seenClips.Add(id))
                return false;
            _seenOrder.Enqueue(id);
            while (_seenOrder.Count > Protocol.SeenClipsCapacity)
                _seenClips.Remove(_seenOrder.Dequeue());
            return true;
        }
    }

    private bool IsSeen(string id)
    {
        lock (_lock)
            return _seenClips.Contains(id);
    }

    private async Task SendClipAsync(Clip clip, string? exceptDeviceId)
    {
        List<ActiveSession> targets;
        lock (_lock)
        {
            targets = _sessions.Values
                .Where(session => session.PeerId != exceptDeviceId
                    && session.PeerId != clip.Origin
                    && !_disabled.Contains(session.PeerId))
                .ToList();
        }
        foreach (var session in targets)
        {
            try
            {
                await session.Info.Channel.SendAsync(new JsonObject
                {
                    ["t"] = "clip",
                    ["id"] = clip.Id,
                    ["origin"] = clip.Origin,
                    ["hops"] = clip.Hops,
                    ["text"] = clip.Text,
                }, _stop.Token);
                Log.Write($"{(clip.Hops == 0 ? "Отправлено" : "Переслано")} «{session.PeerName}»: {clip.Text.Length} символов");
            }
            catch (Exception e)
            {
                Log.Write($"Не удалось отправить «{session.PeerName}»: {e.Message}");
                session.Close();
            }
        }
    }

    private void HandleClip(JsonObject message, ActiveSession session)
    {
        var clip = new Clip(
            Messages.String(message, "id"),
            Messages.String(message, "origin"),
            (int)(Messages.OptionalInteger(message, "hops") ?? 0),
            Messages.String(message, "text"));
        if (!MarkSeen(clip.Id))
        {
            Log.Write($"Повтор фрагмента от «{session.PeerName}» отброшен");
            return;
        }
        Log.Write($"Получено от «{session.PeerName}»: {clip.Text.Length} символов");
        ClipReceived?.Invoke(clip.Text, DisplayName(session));
        if (clip.Hops + 1 < Protocol.MaxHops)
            _ = SendClipAsync(clip with { Hops = clip.Hops + 1 }, exceptDeviceId: session.PeerId);
    }

    private string DisplayName(ActiveSession session) =>
        _store.Load().FirstOrDefault(device => device.DeviceId == session.PeerId)?.Alias ?? session.PeerName;

    // MARK: - Картинки

    /// Отправить картинку (image/png или image/jpeg) всем подключённым включённым устройствам с image в caps.
    /// Возвращает, скольким устройствам она поставлена в очередь (0 — никому или картинка не подходит).
    /// Данные не копируются: все очереди держат один и тот же массив, менять его после вызова нельзя.
    public int SendImage(byte[] data, string mime) => SendImage(data, mime, ignoreSizeLimit: false);

    /// Только для проверки отказа у получателя (clipvey-peer --ignore-image-limit): отправить и больше 20 МиБ.
    internal int SendImage(byte[] data, string mime, bool ignoreSizeLimit)
    {
        if (!_imagesEnabled)
        {
            Log.Write("Передача картинок выключена — картинка не отправлена");
            return 0;
        }
        if (!Protocol.ImageMimes.Contains(mime))
        {
            Log.Write($"Картинка {mime} не отправлена: незнакомый mime");
            return 0;
        }
        if (data.Length == 0 || (data.Length > Protocol.MaxImageBytes && !ignoreSizeLimit))
        {
            Log.Write($"Картинка {data.Length} байт больше 20 МиБ — не отправлена");
            return 0;
        }
        var header = new BlobStart(Blob.NewId(), DeviceId, 0, "image", mime, data.Length, SHA256.HashData(data));
        MarkSeen(header.Id);
        var recipients = EnqueueImage(new OutgoingImage(header, data), exceptDeviceId: null);
        Log.Write($"Картинка {mime}, {data.Length} байт: в очереди для {recipients} устройств");
        return recipients;
    }

    private sealed record OutgoingImage(BlobStart Header, byte[] Data);

    private void HandleBlobStart(JsonObject message, ActiveSession session)
    {
        var start = BlobStart.Parse(message);
        if (session.Receiving is { } previous)
            Log.Write($"Картинка {previous.Header.Id} от «{session.PeerName}» не закончена — отброшена");
        session.Receiving = null;
        // Пока не решено иное, картинка пропускается до blob_end.
        session.SkippingId = start.Id;
        if (IsSeen(start.Id))
        {
            Log.Write($"Повтор картинки от «{session.PeerName}» пропущен");
            return;
        }
        if (!_imagesEnabled)
        {
            Log.Write($"Картинка от «{session.PeerName}» пропущена: передача картинок выключена");
            return;
        }
        if (BlobAssembly.Refusal(start) is { } refusal)
        {
            Log.Write($"Картинка от «{session.PeerName}» не принята: {refusal}");
            return;
        }
        session.SkippingId = null;
        session.Receiving = new BlobAssembly(start);
    }

    private void HandleBlobChunk(JsonObject message, ActiveSession session)
    {
        if (session.Receiving is not { } assembly)
            return;
        var id = Messages.String(message, "id");
        try
        {
            if (id != assembly.Header.Id)
                throw new ProtocolException("кусок с другим id");
            assembly.Append((int)(Messages.OptionalInteger(message, "seq") ?? -1), Blob.ChunkData(message));
        }
        catch (ProtocolException e)
        {
            Log.Write($"Картинка от «{session.PeerName}» отброшена: {e.Message}");
            session.Receiving = null;
            session.SkippingId = assembly.Header.Id;
        }
    }

    private void HandleBlobEnd(JsonObject message, ActiveSession session)
    {
        var id = Messages.String(message, "id");
        if (session.Receiving is { } assembly && assembly.Header.Id == id)
        {
            session.Receiving = null;
            try
            {
                DeliverImage(assembly.Header, assembly.Finish(), session);
            }
            catch (ProtocolException e)
            {
                Log.Write($"Картинка от «{session.PeerName}» отброшена: {e.Message}");
            }
        }
        else if (session.SkippingId == id)
        {
            session.SkippingId = null;
        }
    }

    private void DeliverImage(BlobStart header, byte[] data, ActiveSession session)
    {
        if (!_imagesEnabled)
            return;
        // id заносится в список последних только после успешной сборки.
        if (!MarkSeen(header.Id))
        {
            Log.Write($"Повтор картинки от «{session.PeerName}» отброшен");
            return;
        }
        Log.Write($"Картинка от «{session.PeerName}»: {header.Mime}, {data.Length} байт");
        ImageReceived?.Invoke(data, header.Mime, DisplayName(session));
        if (header.Hops + 1 < Protocol.MaxHops)
        {
            var forwarded = EnqueueImage(new OutgoingImage(header with { Hops = header.Hops + 1 }, data), exceptDeviceId: session.PeerId);
            if (forwarded > 0)
                Log.Write($"Картинка пересылается {forwarded} устройствам");
        }
    }

    /// Поставить картинку в очередь всем подходящим сеансам. В каждом сеансе картинки уходят по одной.
    private int EnqueueImage(OutgoingImage image, string? exceptDeviceId)
    {
        var recipients = 0;
        var toStart = new List<ActiveSession>();
        lock (_lock)
        {
            foreach (var session in _sessions.Values)
            {
                if (session.PeerId == exceptDeviceId || session.PeerId == image.Header.Origin
                    || _disabled.Contains(session.PeerId) || !session.Caps.Contains(Protocol.ImageCapability))
                    continue;
                if (session.Outgoing.Count >= MaxQueuedImages)
                {
                    var dropped = session.Outgoing.Dequeue();
                    Log.Write($"Очередь картинок для «{session.PeerName}» полна — {dropped.Header.Id} не отправлена");
                }
                session.Outgoing.Enqueue(image);
                recipients++;
                if (!session.Sending)
                {
                    session.Sending = true;
                    toStart.Add(session);
                }
            }
        }
        foreach (var session in toStart)
            _ = Task.Run(() => DrainImagesAsync(session));
        return recipients;
    }

    private async Task DrainImagesAsync(ActiveSession session)
    {
        while (true)
        {
            OutgoingImage image;
            lock (_lock)
            {
                if (session.Outgoing.Count == 0 || session.Cancellation.IsCancellationRequested)
                {
                    session.Outgoing.Clear();
                    session.Sending = false;
                    return;
                }
                image = session.Outgoing.Dequeue();
            }
            try
            {
                await SendBlobAsync(session.Info.Channel, image, session.Cancellation.Token);
                Log.Write($"{(image.Header.Hops == 0 ? "Отправлена" : "Переслана")} картинка «{session.PeerName}»: {image.Data.Length} байт");
            }
            catch (Exception e)
            {
                Log.Write($"Не удалось отправить картинку «{session.PeerName}»: {e.Message}");
                session.Close();
            }
        }
    }

    /// blob_start, куски по порядку, blob_end. Каждый кадр — отдельный SendAsync (он сериализует отправку),
    /// поэтому между кусками проходят ping, clip и info.
    private static async Task SendBlobAsync(SecureChannel channel, OutgoingImage image, CancellationToken ct)
    {
        await channel.SendAsync(image.Header.ToMessage(), ct);
        var seq = 0;
        foreach (var chunk in Blob.Chunks(image.Data))
            await channel.SendAsync(Blob.ChunkMessage(image.Header.Id, seq++, chunk.Span), ct);
        await channel.SendAsync(Blob.EndMessage(image.Header.Id), ct);
    }

    // MARK: - Входящие соединения

    private TcpListener OpenListener()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, _preferredPort);
            listener.Start();
            return listener;
        }
        catch (SocketException e)
        {
            Log.Write($"Порт {_preferredPort} занят ({e.Message}), беру свободный");
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            return listener;
        }
    }

    private IReadOnlyDictionary<string, string> Txt()
    {
        var txt = new Dictionary<string, string>
        {
            ["id"] = DeviceId,
            ["v"] = "1",
            ["pair"] = IsPairingMode ? "1" : "0",
        };
        if (DeviceType.Os is { } os)
            txt["os"] = os;
        if (DeviceType.Form is { } form)
            txt["form"] = form;
        return txt;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                Log.Write($"Приём соединения: {e.Message}");
                continue;
            }
            _ = Task.Run(() => HandleIncomingAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task HandleIncomingAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString();
        var adopted = false;
        try
        {
            using var firstMessageTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            firstMessageTimeout.CancelAfter(Protocol.HandshakeTimeout);
            var first = await Pairing.Receive(client.GetStream(), firstMessageTimeout.Token);
            switch (Messages.Type(first))
            {
                case "pair_hello":
                    await HandleIncomingPairingAsync(client, first, remote, ct);
                    break;
                case "hello":
                    var helloId = Messages.String(first, "id");
                    if (_store.Load().FirstOrDefault(device => device.DeviceId == helloId) is { Enabled: false } disabled)
                    {
                        await Pairing.Send(client.GetStream(), new JsonObject { ["t"] = "error", ["reason"] = "disabled" }, ct);
                        Log.Write($"Сеанс с выключенным устройством «{disabled.Name}» отклонён");
                        break;
                    }
                    var info = await Session.RespondAsync(client, first, _identity, FindPaired, OwnInfo, ct);
                    adopted = Adopt(info, remote, port: null);
                    break;
                default:
                    Log.Write($"Непонятное первое сообщение от {remote}: {Messages.Type(first)}");
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Write($"Входящее соединение {remote}: {e.Message}");
        }
        finally
        {
            if (!adopted)
                client.Dispose();
        }
    }

    private async Task HandleIncomingPairingAsync(TcpClient client, JsonObject hello, string? remote, CancellationToken ct)
    {
        var stream = client.GetStream();
        string? refusal = null;
        lock (_lock)
        {
            if (!(_pairingModeUntil is { } until && DateTime.UtcNow < until))
                refusal = "not_pairing";
            else if (_pairingBusy)
                refusal = "busy";
            else
                _pairingBusy = true;
        }
        if (refusal is not null)
        {
            Log.Write($"Запрос на связывание от {remote} отклонён: {refusal}");
            await Pairing.Send(stream, new JsonObject { ["t"] = "error", ["reason"] = refusal }, ct);
            return;
        }

        IncomingPairing? incoming = null;
        try
        {
            incoming = new IncomingPairing(Messages.String(hello, "name"), DeviceType.Parse(hello));
            lock (_lock)
                _incoming = incoming;
            IncomingPairingChanged?.Invoke(incoming);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Protocol.PairingTimeout);
            var current = incoming;
            var paired = await Pairing.RespondAsync(stream, hello, _identity, Name, DeviceType, incoming,
                () => IncomingPairingChanged?.Invoke(current), timeout.Token);
            // Порт слушателя I — тот, что он объявляет через mDNS (I тоже в режиме связывания, его видно в поиске).
            // Если он неизвестен, запасной адрес получает порт по умолчанию: при ошибке по нему узел просто
            // попробует следующий адрес, а при исходящем сеансе запомнит настоящий порт.
            int port;
            lock (_lock)
                port = _advertisedPorts.GetValueOrDefault(paired.DeviceId, Protocol.DefaultPort);
            SavePaired(paired, remote, port);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Write("Связывание отменено или истекло время");
            PairingFailed?.Invoke(FailureReason.Cancelled);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Write($"Связывание не удалось: {e.Message}");
            PairingFailed?.Invoke(Failures.Of(e));
        }
        finally
        {
            lock (_lock)
            {
                _pairingBusy = false;
                if (_incoming == incoming)
                    _incoming = null;
            }
            IncomingPairingChanged?.Invoke(null);
        }
    }

    private PairedDevice? FindPaired(string deviceId) =>
        _store.Load().FirstOrDefault(device => device.DeviceId == deviceId)?.ToPaired();

    private void SavePaired(PairedDevice paired, string? host, int port)
    {
        var type = paired.Type ?? DeviceType.Unknown;
        // Псевдоним переживает повторное связывание с тем же устройством.
        var alias = _store.Load().FirstOrDefault(device => device.DeviceId == paired.DeviceId)?.Alias;
        _store.Upsert(new StoredDevice(paired.DeviceId, paired.Name, Convert.ToBase64String(paired.PublicKey), host, port,
            Enabled: true, Os: type.Os, Form: type.Form, Alias: alias));
        lock (_lock)
        {
            _disconnectedSince[paired.DeviceId] = DateTime.UtcNow;
            _problems.Remove(paired.DeviceId);
            _disabled.Remove(paired.DeviceId);
        }
        StopPairingMode();
        PairingSucceeded?.Invoke(paired.Name);
        Wake();
        Changed?.Invoke();
    }

    // MARK: - Сеансы

    /// true — сеанс принят и теперь принадлежит узлу; false — закрыт как лишний.
    private bool Adopt(SessionInfo info, string? host, int? port)
    {
        var peerId = info.Peer.DeviceId;
        var session = new ActiveSession(info);
        ActiveSession? replaced = null;
        lock (_lock)
        {
            if (_sessions.TryGetValue(peerId, out var existing))
            {
                // Два одновременных сеанса: оставляем тот, где подключался меньший deviceId.
                var preferredInitiator = string.CompareOrdinal(DeviceId, peerId) < 0 ? DeviceId : peerId;
                if (existing.Info.InitiatorId == preferredInitiator || info.InitiatorId != preferredInitiator)
                {
                    Log.Write($"Лишний сеанс с «{info.PeerName}» закрыт");
                    _ = info.Channel.DisposeAsync().AsTask();
                    return false;
                }
                replaced = existing;
            }
            _sessions[peerId] = session;
            _disconnectedSince.Remove(peerId);
            _problems.Remove(peerId);
        }
        replaced?.Close();
        _store.UpdateInfo(peerId, info.Remote.Name, info.Remote.Type);
        if (host is not null)
            _store.UpdateEndpoint(peerId, host, port);
        Log.Write($"Сеанс с «{info.PeerName}» установлен ({(info.InitiatorId == DeviceId ? "исходящий" : "входящий")})");
        if (info.Peer.Name != info.PeerName)
            Log.Write($"«{info.Peer.Name}» теперь называется «{info.PeerName}»");
        _ = Task.Run(() => RunSessionAsync(session));
        Changed?.Invoke();
        PeerUpdated?.Invoke(Update(session, info.Peer.Name));
        return true;
    }

    private PeerUpdate Update(ActiveSession session, string oldName)
    {
        var type = _store.Load().FirstOrDefault(device => device.DeviceId == session.PeerId)?.Type ?? DeviceType.Unknown;
        lock (_lock)
            return new PeerUpdate(session.PeerId, oldName, session.PeerName, type, [.. session.Caps.Order(StringComparer.Ordinal)]);
    }

    private void HandleInfo(JsonObject message, ActiveSession session)
    {
        var info = PeerInfo.Parse(message);
        var oldName = session.PeerName;
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(info.Name))
                session.PeerName = info.Name;
            if (info.Caps is not null)
            {
                session.Caps = [.. info.Caps];
                if (!session.Caps.Contains(Protocol.ImageCapability))
                    session.Outgoing.Clear();
            }
        }
        if (oldName != session.PeerName)
            Log.Write($"«{oldName}» теперь называется «{session.PeerName}»");
        _store.UpdateInfo(session.PeerId, info.Name, info.Type);
        Changed?.Invoke();
        PeerUpdated?.Invoke(Update(session, oldName));
    }

    private async Task RunSessionAsync(ActiveSession session)
    {
        var info = session.Info;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.Cancellation.Token, _stop.Token);
        var ct = linked.Token;
        var pinger = PingAsync(info.Channel, ct);
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(Protocol.IdleTimeout);
                SessionFrame frame;
                try
                {
                    frame = await info.Channel.ReceiveFrameAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ProtocolException("устройство не отвечает");
                }
                using var received = frame;
                if (frame.Invalid is { } invalid)
                {
                    Log.Write($"Двоичный кадр от «{session.PeerName}» пропущен: {invalid}");
                    continue;
                }
                if (frame.Message is not { } message)
                    continue;

                switch (Messages.Type(message))
                {
                    case "clip":
                        HandleClip(message, session);
                        break;
                    case "info":
                        HandleInfo(message, session);
                        break;
                    case "blob_start":
                        HandleBlobStart(message, session);
                        break;
                    case "blob_chunk":
                        HandleBlobChunk(message, session);
                        break;
                    case "blob_end":
                        HandleBlobEnd(message, session);
                        break;
                    case "ping":
                        await info.Channel.SendAsync(new JsonObject { ["t"] = "pong" }, ct);
                        break;
                    case "pong":
                        break;
                    default:
                        Log.Write($"Неизвестное сообщение от «{session.PeerName}»: {Messages.Type(message)}");
                        break;
                }
            }
        }
        catch (Exception e)
        {
            if (!ct.IsCancellationRequested || e is not OperationCanceledException)
                Log.Write($"Сеанс с «{session.PeerName}» завершён: {e.Message}");
        }
        finally
        {
            session.Close();
            await linked.CancelAsync();
            try
            {
                await pinger;
            }
            catch (Exception)
            {
                // Пинг завершается вместе с сеансом.
            }
            bool removed;
            lock (_lock)
            {
                session.Outgoing.Clear();
                removed = _sessions.TryGetValue(session.PeerId, out var current) && current == session;
                if (removed)
                {
                    _sessions.Remove(session.PeerId);
                    _disconnectedSince[session.PeerId] = DateTime.UtcNow;
                }
            }
            session.Receiving = null;
            await info.Channel.DisposeAsync();
            if (removed)
            {
                Changed?.Invoke();
                Wake();
            }
        }
    }

    private static async Task PingAsync(SecureChannel channel, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(Protocol.PingInterval, ct);
            await channel.SendAsync(new JsonObject { ["t"] = "ping" }, ct);
        }
    }

    // MARK: - Поиск и исходящие подключения

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await MaintainAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Write($"Обслуживание: {e.Message}");
            }
            try
            {
                await _wake.WaitAsync(IsPairingMode ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task MaintainAsync(CancellationToken ct)
    {
        bool expired;
        lock (_lock)
            expired = _pairingModeUntil is { } until && DateTime.UtcNow >= until;
        if (expired)
            StopPairingMode();

        var stored = _store.Load();
        var pairing = IsPairingMode;
        List<StoredDevice> disconnected;
        lock (_lock)
            disconnected = stored.Where(device => device.Enabled && !_sessions.ContainsKey(device.DeviceId)).ToList();
        if (!pairing && disconnected.Count == 0)
            return;

        var found = (await MdnsBrowser.BrowseAsync(TimeSpan.FromSeconds(1.5), ct))
            .Where(device => device.DeviceId is not null && device.DeviceId != DeviceId)
            .ToList();
        var pairedIds = stored.Select(device => device.DeviceId).ToHashSet();
        bool candidatesChanged;
        lock (_lock)
        {
            foreach (var device in found)
                _advertisedPorts[device.DeviceId!] = device.Port;
            foreach (var device in found.Where(device => pairedIds.Contains(device.DeviceId!)))
                _discovered[device.DeviceId!] = device.Addresses.Select(address => (address.ToString(), device.Port)).ToList();
            var candidates = IsPairingMode
                ? found.Where(device => device.Pairing && !pairedIds.Contains(device.DeviceId!)).ToList()
                : [];
            candidatesChanged = !candidates.Select(Key).SequenceEqual(_candidates.Select(Key));
            _candidates = candidates;
        }
        if (candidatesChanged)
            Changed?.Invoke();

        foreach (var device in disconnected)
        {
            if (!ShouldInitiate(device.DeviceId))
                continue;
            var endpoints = EndpointsFor(device);
            if (endpoints.Count > 0)
                await TryConnectAsync(device, endpoints, ct);
        }

        static string Key(DiscoveredDevice device) => $"{device.DeviceId}|{device.Port}|{string.Join(',', device.Addresses)}";
    }

    /// Первым подключается меньший deviceId; больший — только если сеанс не появился за 5 секунд.
    private bool ShouldInitiate(string peerId)
    {
        lock (_lock)
        {
            if (_sessions.ContainsKey(peerId))
                return false;
            if (_retryAfter.TryGetValue(peerId, out var retryAfter) && DateTime.UtcNow < retryAfter)
                return false;
            if (string.CompareOrdinal(DeviceId, peerId) < 0)
                return true;
            if (!_disconnectedSince.TryGetValue(peerId, out var since))
            {
                _disconnectedSince[peerId] = DateTime.UtcNow;
                return false;
            }
            return DateTime.UtcNow - since >= Protocol.SecondaryConnectDelay;
        }
    }

    /// Сначала адреса, найденные через mDNS, затем запасной (последний удачный).
    private List<(string Host, int Port, bool Discovered)> EndpointsFor(StoredDevice device)
    {
        var endpoints = new List<(string Host, int Port, bool Discovered)>();
        lock (_lock)
        {
            if (_discovered.TryGetValue(device.DeviceId, out var discovered))
                endpoints.AddRange(discovered.Select(endpoint => (endpoint.Host, endpoint.Port, true)));
        }
        if (device.LastHost is { } host && device.LastPort > 0 && !endpoints.Any(e => e.Host == host && e.Port == device.LastPort))
            endpoints.Add((host, device.LastPort, false));
        return endpoints;
    }

    /// Пробует адреса по очереди. Пауза 30 с — только после disabled. unknown_device или не тот deviceId
    /// по запасному адресу значат, что там теперь другой Clipvey: пробуется следующий адрес.
    private async Task TryConnectAsync(StoredDevice device, List<(string Host, int Port, bool Discovered)> endpoints, CancellationToken ct)
    {
        foreach (var (host, port, discovered) in endpoints)
        {
            TcpClient? tcp = null;
            try
            {
                var (client, _, _) = await Net.ConnectAnyAsync([(host, port)], ct);
                tcp = client;
                var info = await Session.InitiateAsync(client, _identity, device.ToPaired(), OwnInfo, ct);
                tcp = null;  // теперь соединением владеет канал сеанса
                Adopt(info, host, port);
                return;
            }
            catch (PeerRejectedException e) when (e.Failure == FailureReason.Disabled)
            {
                Log.Write($"«{device.Name}»: {e.Message}");
                lock (_lock)
                {
                    _problems[device.DeviceId] = FailureReason.Disabled;
                    _retryAfter[device.DeviceId] = DateTime.UtcNow + Protocol.DisabledRetryDelay;
                }
                Changed?.Invoke();
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Write($"Подключение к «{device.Name}» ({(discovered ? "найден в сети" : "запасной адрес")} {host}:{port}): {e.Message}");
                // Устройство, найденное в сети по своему id, нас не знает — надо связать заново.
                if (discovered && Failures.Of(e) == FailureReason.UnknownDevice)
                {
                    bool changed;
                    lock (_lock)
                    {
                        changed = !_problems.TryGetValue(device.DeviceId, out var problem) || problem != FailureReason.UnknownDevice;
                        _problems[device.DeviceId] = FailureReason.UnknownDevice;
                    }
                    if (changed)
                        Changed?.Invoke();
                }
            }
            finally
            {
                tcp?.Dispose();
            }
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener?.Stop();
        _advertiser?.Dispose();
        List<ActiveSession> sessions;
        lock (_lock)
            sessions = [.. _sessions.Values];
        foreach (var session in sessions)
            session.Close();
        try
        {
            await Task.WhenAll(_background).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Выходим в любом случае.
        }
    }

    /// Сеанс с устройством. PeerName, Caps, Outgoing и Sending меняются под _lock узла;
    /// Receiving и SkippingId — только в цикле приёма сеанса.
    private sealed class ActiveSession(SessionInfo info)
    {
        public SessionInfo Info { get; } = info;
        public CancellationTokenSource Cancellation { get; } = new();
        public string PeerId => Info.Peer.DeviceId;
        public string PeerName { get; set; } = info.PeerName;
        /// Последний известный caps другой стороны. В ready отсутствие caps значит «ничего» (так у 0.1.0).
        public HashSet<string> Caps { get; set; } = [.. info.Remote.Caps ?? []];
        public BlobAssembly? Receiving { get; set; }
        public string? SkippingId { get; set; }
        public Queue<OutgoingImage> Outgoing { get; } = new();
        public bool Sending { get; set; }

        public void Close()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Сеанс уже завершён.
            }
        }
    }
}
