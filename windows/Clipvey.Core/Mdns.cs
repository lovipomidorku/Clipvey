using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Clipvey.Core;

/// Os и Form — из TXT, если устройство их объявляет (иначе null).
public sealed record DiscoveredDevice(
    string Name, int Port, IReadOnlyList<IPAddress> Addresses, string? DeviceId, bool Pairing, string? Os = null, string? Form = null)
{
    public DeviceType Type => new(Os, Form);
}

internal static class LocalNetwork
{
    private static readonly DnsName Service = new(["_clipvey", "_tcp", "local"]);

    public static DnsName ServiceName => Service;

    /// IPv4-адреса работающих интерфейсов с поддержкой multicast (без loopback).
    public static List<IPAddress> Addresses()
    {
        var result = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || !nic.SupportsMulticast)
                continue;
            result.AddRange(nic.GetIPProperties().UnicastAddresses
                .Select(unicast => unicast.Address)
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork));
        }
        return result;
    }
}

/// Поиск устройств «однократными» mDNS-запросами (RFC 6762 §6.7): запрос уходит с эфемерного порта,
/// ответ приходит на него же, порт 5353 не нужен. Запрос отправляется с каждого IPv4-интерфейса:
/// на Windows обычно есть виртуальные адаптеры (VPN, Hyper-V), и «по умолчанию» запрос ушёл бы не туда.
public static class MdnsBrowser
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<IReadOnlyList<DiscoveredDevice>> BrowseAsync(TimeSpan duration, CancellationToken ct)
    {
        var sockets = OpenSockets();
        if (sockets.Count == 0)
            return [];

        var records = new List<DnsRecord>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receivers = sockets.Select(socket => ReceiveAsync(socket, records, stop.Token)).ToList();
        try
        {
            await SendAsync(sockets, DnsWire.BuildQuery([(LocalNetwork.ServiceName, DnsWire.TypePtr)]));
            await Task.Delay(duration / 2, ct);

            // Если в ответе не хватило SRV/TXT/A — дозапрашиваем.
            var followUps = MissingQuestions(Snapshot(records));
            if (followUps.Count > 0)
                await SendAsync(sockets, DnsWire.BuildQuery(followUps));
            await Task.Delay(duration / 2, ct);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(receivers);
            foreach (var socket in sockets)
                socket.Dispose();
        }
        return Assemble(Snapshot(records));
    }

    private static List<UdpClient> OpenSockets()
    {
        var sockets = new List<UdpClient>();
        foreach (var address in LocalNetwork.Addresses())
        {
            try
            {
                var socket = new UdpClient(new IPEndPoint(address, 0));
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                sockets.Add(socket);
            }
            catch (SocketException e)
            {
                Log.Write($"mDNS: адрес {address} пропущен: {e.Message}");
            }
        }
        return sockets;
    }

    private static async Task SendAsync(List<UdpClient> sockets, byte[] query)
    {
        foreach (var socket in sockets)
        {
            try
            {
                await socket.SendAsync(query, MulticastEndpoint);
            }
            catch (SocketException e)
            {
                Log.Write($"mDNS: запрос через {socket.Client.LocalEndPoint} не отправлен: {e.Message}");
            }
        }
    }

    private static async Task ReceiveAsync(UdpClient socket, List<DnsRecord> records, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveAsync(ct);
                var message = DnsWire.Parse(result.Buffer);
                if (message is { IsResponse: true })
                {
                    lock (records)
                        records.AddRange(message.Records);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private static List<DnsRecord> Snapshot(List<DnsRecord> records)
    {
        lock (records)
            return [.. records];
    }

    private static List<DnsName> Instances(List<DnsRecord> records) =>
        records.OfType<PtrRecord>()
            .Where(ptr => ptr.Name.Key == LocalNetwork.ServiceName.Key)
            .Select(ptr => ptr.Target)
            .DistinctBy(name => name.Key)
            .ToList();

    private static List<(DnsName, ushort)> MissingQuestions(List<DnsRecord> records)
    {
        var questions = new List<(DnsName, ushort)>();
        foreach (var instance in Instances(records))
        {
            var srv = records.OfType<SrvRecord>().FirstOrDefault(record => record.Name.Key == instance.Key);
            if (srv is null)
            {
                questions.Add((instance, DnsWire.TypeSrv));
                questions.Add((instance, DnsWire.TypeTxt));
            }
            else if (!records.OfType<ARecord>().Any(record => record.Name.Key == srv.Target.Key))
            {
                questions.Add((srv.Target, DnsWire.TypeA));
            }
        }
        return questions;
    }

    private static List<DiscoveredDevice> Assemble(List<DnsRecord> records)
    {
        var result = new List<DiscoveredDevice>();
        foreach (var instance in Instances(records))
        {
            var srv = records.OfType<SrvRecord>().LastOrDefault(record => record.Name.Key == instance.Key);
            if (srv is null)
                continue;
            // Берём последний TXT: он отражает текущее состояние (например, режим связывания).
            var txt = records.OfType<TxtRecord>().LastOrDefault(record => record.Name.Key == instance.Key);
            var addresses = records.OfType<ARecord>()
                .Where(record => record.Name.Key == srv.Target.Key)
                .Select(record => record.Address)
                .Distinct()
                .ToList();
            if (addresses.Count == 0)
                continue;
            result.Add(new DiscoveredDevice(
                instance.Parts.Count > 0 ? instance.Parts[0] : "?",
                srv.Port,
                addresses,
                txt?.Values.GetValueOrDefault("id"),
                txt?.Values.GetValueOrDefault("pair") == "1",
                txt?.Values.GetValueOrDefault("os"),
                txt?.Values.GetValueOrDefault("form")));
        }
        return result;
    }
}

/// Объявление своей службы в сети: собственный mDNS-ответчик на UDP 5353 (порт делится с системной
/// службой mDNS). Отвечает на запросы о _clipvey._tcp, своём экземпляре и своём имени хоста.
public sealed class MdnsAdvertiser : IDisposable
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private const int MulticastTtl = 120;
    private const int LegacyUnicastTtl = 10;

    // Меняется при переименовании (Rename); читается потоком ответов.
    private volatile DnsName _instance;
    private readonly DnsName _host;
    private readonly int _port;
    private readonly Func<IReadOnlyDictionary<string, string>> _txt;
    private Socket? _socket;
    private CancellationTokenSource? _stop;

    public MdnsAdvertiser(string instanceName, string hostLabel, int port, Func<IReadOnlyDictionary<string, string>> txt)
    {
        _instance = new DnsName([instanceName, .. LocalNetwork.ServiceName.Parts]);
        _host = new DnsName([hostLabel, "local"]);
        _port = port;
        _txt = txt;
    }

    public void Start()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        if (OperatingSystem.IsMacOS())
            TrySetRawOption(socket, 0xffff, 0x0200);  // SO_REUSEPORT: порт 5353 занят системным mDNSResponder
        else if (OperatingSystem.IsLinux())
            TrySetRawOption(socket, 1, 15);
        socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
        foreach (var address in LocalNetwork.Addresses())
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, address));
            }
            catch (SocketException e)
            {
                Log.Write($"mDNS: не удалось подписаться на multicast через {address}: {e.Message}");
            }
        }
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        // Отправка через «застрявший» интерфейс (например, туннель VPN, который не читает multicast) иначе
        // может заблокироваться навсегда: тогда не уходили бы ни объявления, ни «прощание» при выходе.
        socket.SendTimeout = 500;

        _socket = socket;
        _stop = new CancellationTokenSource();
        _ = Task.Run(() => ReceiveLoopAsync(socket, _stop.Token));
        Announce();
        Log.Write($"mDNS: объявлено «{_instance.Parts[0]}» на порту {_port}");
    }

    /// Сменить имя экземпляра: «прощание» (TTL 0) для записей старого имени, затем объявление нового.
    /// Запись A имени хоста не прощается: имя хоста не меняется. Рассылка — в фоне, как и Announce.
    public void Rename(string instanceName)
    {
        var old = _instance;
        var renamed = new DnsName([instanceName, .. LocalNetwork.ServiceName.Parts]);
        if (renamed.ToString() == old.ToString())
            return;
        var goodbye = DnsResourceWriter.Response(null,
        [
            DnsResourceWriter.Ptr(LocalNetwork.ServiceName, old, 0),
            DnsResourceWriter.Srv(old, _port, _host, 0, cacheFlush: true),
            DnsResourceWriter.Txt(old, _txt(), 0, cacheFlush: true),
        ]);
        _instance = renamed;
        Log.Write($"mDNS: «{old.Parts[0]}» переименовано в «{instanceName}»");
        _ = Task.Run(async () =>
        {
            SendMulticast(goodbye);
            await AnnounceAsync();
        });
    }

    /// Разослать свои записи без запроса (при запуске и когда меняется TXT, например режим связывания).
    public void Announce() => _ = Task.Run(AnnounceAsync);

    private async Task AnnounceAsync()
    {
        SendMulticast(BuildResponse(MulticastTtl, legacyQuery: null));
        await Task.Delay(1000);
        SendMulticast(BuildResponse(MulticastTtl, legacyQuery: null));
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[9000];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            var message = DnsWire.Parse(buffer.AsSpan(0, received.ReceivedBytes).ToArray());
            if (message is null || message.IsResponse || !message.Questions.Any(IsOurs))
                continue;

            var remote = (IPEndPoint)received.RemoteEndPoint;
            try
            {
                if (remote.Port != 5353)
                {
                    // Однократный запрос: отвечаем напрямую, повторяя ID и вопрос.
                    await socket.SendToAsync(BuildResponse(LegacyUnicastTtl, message), SocketFlags.None, remote, ct);
                }
                else
                {
                    SendMulticast(BuildResponse(MulticastTtl, legacyQuery: null));
                }
            }
            catch (SocketException e)
            {
                Log.Write($"mDNS: не удалось ответить {remote}: {e.Message}");
            }
        }
    }

    private bool IsOurs(DnsQuestion question) =>
        (question.Name.Key == LocalNetwork.ServiceName.Key && question.Type is DnsWire.TypePtr or DnsWire.TypeAny)
        || (question.Name.Key == _instance.Key && question.Type is DnsWire.TypeSrv or DnsWire.TypeTxt or DnsWire.TypeAny)
        || (question.Name.Key == _host.Key && question.Type is DnsWire.TypeA or DnsWire.TypeAny);

    private void SendMulticast(byte[] response)
    {
        var socket = _socket;
        if (socket is null)
            return;
        var destination = new IPEndPoint(Group, 5353);
        foreach (var address in LocalNetwork.Addresses())
        {
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                socket.SendTo(response, destination);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                // Интерфейс мог пропасть — пробуем остальные.
            }
        }
    }

    private byte[] BuildResponse(int ttl, DnsMessage? legacyQuery)
    {
        var legacy = legacyQuery is not null;
        var records = new List<DnsResourceWriter.Record>
        {
            DnsResourceWriter.Ptr(LocalNetwork.ServiceName, _instance, ttl),
            DnsResourceWriter.Srv(_instance, _port, _host, ttl, cacheFlush: !legacy),
            DnsResourceWriter.Txt(_instance, _txt(), ttl, cacheFlush: !legacy),
        };
        records.AddRange(LocalNetwork.Addresses().Select(address => DnsResourceWriter.A(_host, address, ttl, cacheFlush: !legacy)));
        return DnsResourceWriter.Response(legacyQuery, records);
    }

    private static void TrySetRawOption(Socket socket, int level, int name)
    {
        try
        {
            socket.SetRawSocketOption(level, name, BitConverter.GetBytes(1));
        }
        catch (SocketException e)
        {
            Log.Write($"mDNS: SO_REUSEPORT недоступен: {e.Message}");
        }
    }

    public void Dispose()
    {
        if (_socket is null)
            return;
        try
        {
            SendMulticast(BuildResponse(0, legacyQuery: null));  // «прощание»: TTL 0
        }
        catch (Exception)
        {
            // Выходим в любом случае.
        }
        _stop?.Cancel();
        _socket.Dispose();
        _socket = null;
    }
}

/// Имя DNS как список меток (метка имени экземпляра может содержать точки и пробелы).
internal sealed class DnsName(IReadOnlyList<string> parts)
{
    public IReadOnlyList<string> Parts { get; } = parts;

    /// Ключ для сравнения без учёта регистра.
    public string Key { get; } = string.Join("\u0001", parts).ToLowerInvariant();

    public override string ToString() => string.Join(".", Parts);
}

internal abstract record DnsRecord(DnsName Name);
internal sealed record PtrRecord(DnsName Name, DnsName Target) : DnsRecord(Name);
internal sealed record SrvRecord(DnsName Name, ushort Port, DnsName Target) : DnsRecord(Name);
internal sealed record TxtRecord(DnsName Name, IReadOnlyDictionary<string, string> Values) : DnsRecord(Name);
internal sealed record ARecord(DnsName Name, IPAddress Address) : DnsRecord(Name);

internal sealed record DnsQuestion(DnsName Name, ushort Type);

internal sealed record DnsMessage(ushort Id, bool IsResponse, IReadOnlyList<DnsQuestion> Questions, IReadOnlyList<DnsRecord> Records);

/// Минимальный разбор и сборка DNS-сообщений: только то, что нужно для поиска и объявления службы.
internal static class DnsWire
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeSrv = 33;
    public const ushort TypeAny = 255;
    public const ushort ClassIn = 1;

    public static byte[] BuildQuery(IReadOnlyList<(DnsName Name, ushort Type)> questions)
    {
        var buffer = new List<byte>(512);
        WriteUInt16(buffer, (ushort)Random.Shared.Next(1, ushort.MaxValue));  // ID однократного запроса
        WriteUInt16(buffer, 0);
        WriteUInt16(buffer, (ushort)questions.Count);
        WriteUInt16(buffer, 0);
        WriteUInt16(buffer, 0);
        WriteUInt16(buffer, 0);
        foreach (var (name, type) in questions)
        {
            WriteName(buffer, name);
            WriteUInt16(buffer, type);
            WriteUInt16(buffer, ClassIn);
        }
        return [.. buffer];
    }

    public static DnsMessage? Parse(byte[] message)
    {
        try
        {
            if (message.Length < 12)
                return null;
            var id = ReadUInt16(message, 0);
            var isResponse = (message[2] & 0x80) != 0;
            int questionCount = ReadUInt16(message, 4);
            int recordCount = ReadUInt16(message, 6) + ReadUInt16(message, 8) + ReadUInt16(message, 10);
            var questions = new List<DnsQuestion>();
            var records = new List<DnsRecord>();
            var offset = 12;
            for (var i = 0; i < questionCount; i++)
            {
                var name = ReadName(message, ref offset);
                questions.Add(new DnsQuestion(name, ReadUInt16(message, offset)));
                offset += 4;
            }
            for (var i = 0; i < recordCount; i++)
            {
                var name = ReadName(message, ref offset);
                if (offset + 10 > message.Length)
                    break;
                var type = ReadUInt16(message, offset);
                var dataLength = ReadUInt16(message, offset + 8);
                var dataStart = offset + 10;
                if (dataStart + dataLength > message.Length)
                    break;
                switch (type)
                {
                    case TypePtr:
                    {
                        var position = dataStart;
                        records.Add(new PtrRecord(name, ReadName(message, ref position)));
                        break;
                    }
                    case TypeSrv when dataLength >= 7:
                    {
                        var position = dataStart + 6;
                        records.Add(new SrvRecord(name, ReadUInt16(message, dataStart + 4), ReadName(message, ref position)));
                        break;
                    }
                    case TypeTxt:
                        records.Add(new TxtRecord(name, ParseTxt(message, dataStart, dataLength)));
                        break;
                    case TypeA when dataLength == 4:
                        records.Add(new ARecord(name, new IPAddress(message.AsSpan(dataStart, 4))));
                        break;
                }
                offset = dataStart + dataLength;
            }
            return new DnsMessage(id, isResponse, questions, records);
        }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    internal static void WriteName(List<byte> buffer, DnsName name)
    {
        foreach (var label in name.Parts)
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            var length = Math.Min(bytes.Length, 63);
            buffer.Add((byte)length);
            buffer.AddRange(bytes.AsSpan(0, length).ToArray());
        }
        buffer.Add(0);
    }

    internal static void WriteUInt16(List<byte> buffer, ushort value)
    {
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static DnsName ReadName(byte[] message, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            var length = message[position];
            if (length == 0)
            {
                position++;
                break;
            }
            if ((length & 0xC0) == 0xC0)
            {
                var pointer = ((length & 0x3F) << 8) | message[position + 1];
                if (!jumped)
                    offset = position + 2;
                jumped = true;
                position = pointer;
                if (++jumps > 32)
                    throw new FormatException("Петля сжатия имён");
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(message, position + 1, length));
            position += 1 + length;
        }
        if (!jumped)
            offset = position;
        return new DnsName(labels);
    }

    private static Dictionary<string, string> ParseTxt(byte[] message, int start, int length)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var position = start;
        while (position < start + length)
        {
            var itemLength = message[position];
            var item = Encoding.UTF8.GetString(message, position + 1, itemLength);
            var separator = item.IndexOf('=');
            if (separator > 0)
                values[item[..separator]] = item[(separator + 1)..];
            position += 1 + itemLength;
        }
        return values;
    }

    private static ushort ReadUInt16(byte[] data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);
}

/// Сборка ответов mDNS. Все записи кладутся в раздел ответов, без сжатия имён.
internal static class DnsResourceWriter
{
    internal sealed record Record(DnsName Name, ushort Type, bool CacheFlush, int Ttl, byte[] Data);

    public static Record Ptr(DnsName name, DnsName target, int ttl)
    {
        var data = new List<byte>();
        DnsWire.WriteName(data, target);
        return new Record(name, DnsWire.TypePtr, false, ttl, [.. data]);
    }

    public static Record Srv(DnsName name, int port, DnsName target, int ttl, bool cacheFlush)
    {
        var data = new List<byte>();
        DnsWire.WriteUInt16(data, 0);  // приоритет
        DnsWire.WriteUInt16(data, 0);  // вес
        DnsWire.WriteUInt16(data, (ushort)port);
        DnsWire.WriteName(data, target);
        return new Record(name, DnsWire.TypeSrv, cacheFlush, ttl, [.. data]);
    }

    public static Record Txt(DnsName name, IReadOnlyDictionary<string, string> values, int ttl, bool cacheFlush)
    {
        var data = new List<byte>();
        foreach (var (key, value) in values)
        {
            var bytes = Encoding.UTF8.GetBytes($"{key}={value}");
            var length = Math.Min(bytes.Length, 255);
            data.Add((byte)length);
            data.AddRange(bytes.AsSpan(0, length).ToArray());
        }
        return new Record(name, DnsWire.TypeTxt, cacheFlush, ttl, [.. data]);
    }

    public static Record A(DnsName name, IPAddress address, int ttl, bool cacheFlush) =>
        new(name, DnsWire.TypeA, cacheFlush, ttl, address.GetAddressBytes());

    /// legacyQuery — для ответа на однократный запрос (повторяются ID и вопросы), иначе null.
    public static byte[] Response(DnsMessage? legacyQuery, IReadOnlyList<Record> records)
    {
        var buffer = new List<byte>(512);
        DnsWire.WriteUInt16(buffer, legacyQuery?.Id ?? 0);
        DnsWire.WriteUInt16(buffer, 0x8400);  // ответ, авторитетный
        DnsWire.WriteUInt16(buffer, (ushort)(legacyQuery?.Questions.Count ?? 0));
        DnsWire.WriteUInt16(buffer, (ushort)records.Count);
        DnsWire.WriteUInt16(buffer, 0);
        DnsWire.WriteUInt16(buffer, 0);
        if (legacyQuery is not null)
        {
            foreach (var question in legacyQuery.Questions)
            {
                DnsWire.WriteName(buffer, question.Name);
                DnsWire.WriteUInt16(buffer, question.Type);
                DnsWire.WriteUInt16(buffer, DnsWire.ClassIn);
            }
        }
        foreach (var record in records)
        {
            DnsWire.WriteName(buffer, record.Name);
            DnsWire.WriteUInt16(buffer, record.Type);
            DnsWire.WriteUInt16(buffer, (ushort)(DnsWire.ClassIn | (record.CacheFlush ? 0x8000 : 0)));
            buffer.Add((byte)(record.Ttl >> 24));
            buffer.Add((byte)(record.Ttl >> 16));
            buffer.Add((byte)(record.Ttl >> 8));
            buffer.Add((byte)record.Ttl);
            DnsWire.WriteUInt16(buffer, (ushort)record.Data.Length);
            buffer.AddRange(record.Data);
        }
        return [.. buffer];
    }
}
