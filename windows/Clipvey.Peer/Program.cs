// Консольный «двойник» Windows-приложения: запускает тот же ClipveyNode и печатает события в stdout
// строками вида «СОБЫТИЕ данные», чтобы их было удобно ждать в скриптах. Журнал — в stderr.
//
//   clipvey-peer [команда] [флаги]
//   команды:  run (по умолчанию) | list | discover | enable ИМЯ | disable ИМЯ
//   --data DIR          папка с ключом и списком устройств (по умолчанию ~/.clipvey-peer)
//   --name NAME         имя устройства (по умолчанию peer-<имя компьютера>)
//   --port N            желаемый TCP-порт (по умолчанию 48620)
//   --pair              открыть режим связывания
//   --pair-with TEXT    связаться (роль I) с устройством в режиме связывания, в имени которого есть TEXT
//   --code CODE         код для роли I
//   --code-file FILE    взять код для роли I из файла со строкой «PAIRING_CODE 123456»
//   --auto-confirm      в роли R нажать «Готово», когда другая сторона подтвердит код
//   --send TEXT         после подключения отправить текст всем устройствам
//   --send-delay N      перед отправкой подождать ещё N секунд (пока поднимутся остальные сеансы)
//   --seconds N         сколько работать (по умолчанию 60)
using System.Text.Json;
using System.Text.RegularExpressions;
using Clipvey.Core;

var arguments = args.ToList();
var command = arguments.Count > 0 && !arguments[0].StartsWith("--") ? arguments[0] : "run";
string? Option(string name)
{
    var index = arguments.IndexOf(name);
    return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
}
bool Flag(string name) => arguments.Contains(name);

var outputLock = new object();
void Emit(string line)
{
    lock (outputLock)
        Console.WriteLine(line);
}
Log.Sink = line =>
{
    lock (outputLock)
        Console.Error.WriteLine(line);
};

var dataDirectory = Option("--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".clipvey-peer");
var deviceName = Option("--name") ?? $"peer-{Environment.MachineName}";
var port = int.TryParse(Option("--port"), out var parsedPort) ? parsedPort : Protocol.DefaultPort;
var seconds = int.TryParse(Option("--seconds"), out var parsedSeconds) ? parsedSeconds : 60;

using var identity = Identity.LoadOrCreate(new FileSecretStore(dataDirectory));
var store = new DeviceStore(dataDirectory);

switch (command)
{
    case "list":
        foreach (var device in store.Load())
            Emit($"DEVICE {device.DeviceId} {device.Name} enabled={device.Enabled} {device.LastHost}:{device.LastPort}");
        return 0;
    case "enable" or "disable":
        var fragment = arguments.Count > 1 ? arguments[1] : "";
        foreach (var device in store.Load().Where(device => device.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
        {
            store.SetEnabled(device.DeviceId, command == "enable");
            Emit($"{command.ToUpperInvariant()}D {device.Name}");
        }
        return 0;
    case "discover":
        foreach (var device in await MdnsBrowser.BrowseAsync(TimeSpan.FromSeconds(3), CancellationToken.None))
            Emit($"FOUND {device.Name} id={device.DeviceId} pair={device.Pairing} port={device.Port} addresses={string.Join(',', device.Addresses)}");
        return 0;
}

await using var node = new ClipveyNode(identity, store, deviceName, port);
using var finished = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));

node.ClipReceived += (text, from) => Emit($"CLIP {from} {JsonSerializer.Serialize(text)}");
node.PairingSucceeded += peer => Emit($"PAIRED {peer}");
node.PairingFailed += reason => Emit($"PAIRING_FAILED {reason}");
var lastConnected = new HashSet<string>();
node.Changed += () =>
{
    lock (lastConnected)
    {
        foreach (var device in node.Devices)
        {
            if (device.Connected && lastConnected.Add(device.DeviceId))
                Emit($"CONNECTED {device.Name}");
            else if (!device.Connected && lastConnected.Remove(device.DeviceId))
                Emit($"DISCONNECTED {device.Name}");
        }
    }
};
node.IncomingPairingChanged += incoming =>
{
    if (incoming is null)
        return;
    if (incoming.Code is { } code && !incoming.Verified)
        Emit($"PAIRING_CODE {code} FROM {incoming.PeerName}");
    if (incoming.Verified)
    {
        Emit($"PAIRING_VERIFIED {incoming.PeerName}");
        if (Flag("--auto-confirm"))
            incoming.Confirm();
    }
};

node.Start();
Emit($"READY {deviceName} id={node.DeviceId} port={node.Port}");
if (Flag("--pair") || Option("--pair-with") is not null)
    node.StartPairingMode();

if (Option("--pair-with") is { } target)
{
    _ = Task.Run(async () =>
    {
        try
        {
            var candidate = await WaitForCandidateAsync(node, target, finished.Token);
            Emit($"PAIRING_WITH {candidate.Name}");
            await node.PairWithAsync(candidate, (_, ct) => ReadCodeAsync(ct), () => Emit("CODE_ACCEPTED"), finished.Token);
        }
        catch (Exception e)
        {
            Emit($"PAIR_WITH_ERROR {e.Message}");
        }
    });
}

if (Option("--send") is { } textToSend)
{
    _ = Task.Run(async () =>
    {
        while (!finished.IsCancellationRequested && !node.Devices.Any(device => device.Connected))
            await Task.Delay(200);
        if (int.TryParse(Option("--send-delay"), out var delaySeconds))
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        if (finished.IsCancellationRequested)
            return;
        await node.BroadcastClipAsync(textToSend);
        Emit("SENT");
    });
}

try
{
    await Task.Delay(Timeout.Infinite, finished.Token);
}
catch (OperationCanceledException)
{
}
Emit("EXIT");
return 0;

async Task<DiscoveredDevice> WaitForCandidateAsync(ClipveyNode clipvey, string nameFragment, CancellationToken ct)
{
    while (true)
    {
        var match = clipvey.PairingCandidates.FirstOrDefault(device => device.Name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match;
        await Task.Delay(300, ct);
    }
}

async Task<string?> ReadCodeAsync(CancellationToken ct)
{
    if (Option("--code") is { } code)
        return code;
    if (Option("--code-file") is not { } file)
        return Console.ReadLine();
    var pattern = new Regex(@"PAIRING_CODE (\d{6})");
    while (true)
    {
        if (File.Exists(file) && pattern.Match(await File.ReadAllTextAsync(file, ct)) is { Success: true } match)
            return match.Groups[1].Value;
        await Task.Delay(200, ct);
    }
}
