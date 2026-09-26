using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Clipvey.Core;

/// Связанное устройство для интерфейса. Problem — почему не удаётся подключиться (если известно).
public sealed record DeviceStatus(string DeviceId, string Name, bool Enabled, bool Connected, string? Problem);

/// Узел Clipvey:
/// - слушает TCP и объявляет себя через mDNS;
/// - находит связанные устройства и держит с каждым не больше одного сеанса;
/// - ведёт связывание в обеих ролях.
/// Правила — docs/protocol.md. События вызываются из фоновых потоков.
public sealed class ClipveyNode(Identity identity, DeviceStore store, string name, int preferredPort = Protocol.DefaultPort) : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ActiveSession> _sessions = [];
    private readonly Dictionary<string, List<(string Host, int Port)>> _discovered = [];
    private readonly Dictionary<string, DateTime> _disconnectedSince = [];
    private readonly Dictionary<string, string> _problems = [];
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

    public string DeviceId => identity.DeviceId;
    public string Name => name;
    public int Port { get; private set; }

    /// Изменились связанные устройства, их состояние или список кандидатов для связывания.
    public event Action? Changed;

    /// Пришёл текст (переводы строк — \n) и имя устройства-отправителя.
    public event Action<string, string>? ClipReceived;

    /// Изменилось входящее связывание (роль R); null — связывание закончилось.
    public event Action<IncomingPairing?>? IncomingPairingChanged;

    public event Action<string>? PairingSucceeded;
    public event Action<string>? PairingFailed;

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
            foreach (var device in store.Load())
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
            var stored = store.Load();
            lock (_lock)
            {
                return stored
                    .Select(device => new DeviceStatus(
                        device.DeviceId,
                        device.Name,
                        device.Enabled,
                        _sessions.ContainsKey(device.DeviceId),
                        _problems.GetValueOrDefault(device.DeviceId)))
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
                var paired = await Pairing.InitiateAsync(tcp.GetStream(), identity, Name, requestCode, codeAccepted, timeout.Token);
                SavePaired(paired, host, port);
                return paired;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !_stop.IsCancellationRequested)
        {
            PairingFailed?.Invoke(e is OperationCanceledException ? "Связывание отменено" : e.Message);
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
        store.Remove(deviceId);
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
        store.SetEnabled(deviceId, enabled);
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

    /// Отправить текст, скопированный на этом устройстве, всем подключённым устройствам.
    public Task BroadcastClipAsync(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > Protocol.MaxClipBytes)
        {
            Log.Write("Текст больше 1 МиБ — не отправлен");
            return Task.CompletedTask;
        }
        var id = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        MarkSeen(id);
        return SendClipAsync(new Clip(id, DeviceId, 0, text), exceptDeviceId: null);
    }

    private sealed record Clip(string Id, string Origin, int Hops, string Text);

    /// true — фрагмент новый; false — уже был (пришёл другим путём).
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

    private async Task SendClipAsync(Clip clip, string? exceptDeviceId)
    {
        List<ActiveSession> targets;
        lock (_lock)
        {
            targets = _sessions.Values
                .Where(session => session.Info.Peer.DeviceId != exceptDeviceId
                    && session.Info.Peer.DeviceId != clip.Origin
                    && !_disabled.Contains(session.Info.Peer.DeviceId))
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
                Log.Write($"{(clip.Hops == 0 ? "Отправлено" : "Переслано")} «{session.Info.PeerName}»: {clip.Text.Length} символов");
            }
            catch (Exception e)
            {
                Log.Write($"Не удалось отправить «{session.Info.PeerName}»: {e.Message}");
                session.Close();
            }
        }
    }

    // MARK: - Входящие соединения

    private TcpListener OpenListener()
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, preferredPort);
            listener.Start();
            return listener;
        }
        catch (SocketException e)
        {
            Log.Write($"Порт {preferredPort} занят ({e.Message}), беру свободный");
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            return listener;
        }
    }

    private IReadOnlyDictionary<string, string> Txt() => new Dictionary<string, string>
    {
        ["id"] = DeviceId,
        ["v"] = "1",
        ["pair"] = IsPairingMode ? "1" : "0",
    };

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
                    if (store.Load().FirstOrDefault(device => device.DeviceId == helloId) is { Enabled: false } disabled)
                    {
                        await Pairing.Send(client.GetStream(), new JsonObject { ["t"] = "error", ["reason"] = "disabled" }, ct);
                        Log.Write($"Сеанс с выключенным устройством «{disabled.Name}» отклонён");
                        break;
                    }
                    var info = await Session.RespondAsync(client, first, identity, FindPaired, Name, ct);
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
            incoming = new IncomingPairing(Messages.String(hello, "name"));
            lock (_lock)
                _incoming = incoming;
            IncomingPairingChanged?.Invoke(incoming);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Protocol.PairingTimeout);
            var current = incoming;
            var paired = await Pairing.RespondAsync(stream, hello, identity, Name, incoming,
                () => IncomingPairingChanged?.Invoke(current), timeout.Token);
            SavePaired(paired, remote, Protocol.DefaultPort);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            PairingFailed?.Invoke("Связывание отменено или истекло время");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            PairingFailed?.Invoke(e.Message);
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
        store.Load().FirstOrDefault(device => device.DeviceId == deviceId)?.ToPaired();

    private void SavePaired(PairedDevice paired, string? host, int port)
    {
        store.Upsert(new StoredDevice(paired.DeviceId, paired.Name, Convert.ToBase64String(paired.PublicKey), host, port));
        lock (_lock)
        {
            _disconnectedSince[paired.DeviceId] = DateTime.UtcNow;
            _problems.Remove(paired.DeviceId);
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
        if (host is not null)
            store.UpdateEndpoint(peerId, info.PeerName, host, port);
        Log.Write($"Сеанс с «{info.PeerName}» установлен ({(info.InitiatorId == DeviceId ? "исходящий" : "входящий")})");
        _ = Task.Run(() => RunSessionAsync(session));
        Changed?.Invoke();
        return true;
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
                JsonObject message;
                try
                {
                    message = await info.Channel.ReceiveAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ProtocolException("устройство не отвечает");
                }

                switch (Messages.Type(message))
                {
                    case "clip":
                        var clip = new Clip(
                            Messages.String(message, "id"),
                            Messages.String(message, "origin"),
                            message["hops"]?.GetValue<int>() ?? 0,
                            Messages.String(message, "text"));
                        if (!MarkSeen(clip.Id))
                        {
                            Log.Write($"Повтор фрагмента от «{info.PeerName}» отброшен");
                            break;
                        }
                        Log.Write($"Получено от «{info.PeerName}»: {clip.Text.Length} символов");
                        ClipReceived?.Invoke(clip.Text, info.PeerName);
                        if (clip.Hops + 1 < Protocol.MaxHops)
                            _ = SendClipAsync(clip with { Hops = clip.Hops + 1 }, exceptDeviceId: info.Peer.DeviceId);
                        break;
                    case "ping":
                        await info.Channel.SendAsync(new JsonObject { ["t"] = "pong" }, ct);
                        break;
                    case "pong":
                        break;
                    default:
                        Log.Write($"Неизвестное сообщение от «{info.PeerName}»: {Messages.Type(message)}");
                        break;
                }
            }
        }
        catch (Exception e)
        {
            if (!ct.IsCancellationRequested || e is not OperationCanceledException)
                Log.Write($"Сеанс с «{info.PeerName}» завершён: {e.Message}");
        }
        finally
        {
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
                removed = _sessions.TryGetValue(info.Peer.DeviceId, out var current) && current == session;
                if (removed)
                {
                    _sessions.Remove(info.Peer.DeviceId);
                    _disconnectedSince[info.Peer.DeviceId] = DateTime.UtcNow;
                }
            }
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

        var stored = store.Load();
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

    private List<(string Host, int Port)> EndpointsFor(StoredDevice device)
    {
        var endpoints = new List<(string Host, int Port)>();
        lock (_lock)
        {
            if (_discovered.TryGetValue(device.DeviceId, out var discovered))
                endpoints.AddRange(discovered);
        }
        if (device.LastHost is { } host && device.LastPort > 0)
            endpoints.Add((host, device.LastPort));
        return endpoints.Distinct().ToList();
    }

    private async Task TryConnectAsync(StoredDevice device, List<(string Host, int Port)> endpoints, CancellationToken ct)
    {
        TcpClient? tcp = null;
        try
        {
            var (client, host, port) = await Net.ConnectAnyAsync(endpoints, ct);
            tcp = client;
            var info = await Session.InitiateAsync(client, identity, device.ToPaired(), Name, ct);
            tcp = null;  // теперь соединением владеет канал сеанса
            Adopt(info, host, port);
        }
        catch (PeerRejectedException e)
        {
            Log.Write($"«{device.Name}»: {e.Message}");
            lock (_lock)
            {
                _problems[device.DeviceId] = e.Message;
                _retryAfter[device.DeviceId] = DateTime.UtcNow + Protocol.DisabledRetryDelay;
            }
            Changed?.Invoke();
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Write($"Подключение к «{device.Name}»: {e.Message}");
        }
        finally
        {
            tcp?.Dispose();
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

    private sealed class ActiveSession(SessionInfo info)
    {
        public SessionInfo Info { get; } = info;
        public CancellationTokenSource Cancellation { get; } = new();

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
