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
//   --pair-address HOST:PORT  для --pair-with: подключиться по адресу, не дожидаясь mDNS (проверки без mDNS)
//   --code CODE         код для роли I
//   --code-file FILE    взять код для роли I из файла со строкой «PAIRING_CODE 123456»
//   --auto-confirm      в роли R нажать «Готово», когда другая сторона подтвердит код
//   --send TEXT         после подключения отправить текст всем устройствам
//   --send-delay N      перед отправкой подождать ещё N секунд (пока поднимутся остальные сеансы)
//   --seconds N         сколько работать (по умолчанию 60)
//   --os OS, --form F   свой тип устройства (по умолчанию windows / desktop)
//   --images off        выключить «Передавать картинки»
//   --send-image FILE   после подключения (и --send-delay) отправить картинку; mime по расширению: .png, .jpg/.jpeg
//   --ignore-image-limit  отправить картинку и больше 20 МиБ (проверка отказа у получателя)
//   --save-images DIR   сохранять полученные картинки в DIR (имя — sha256 и расширение)
//   --rename-after N NAME  через N секунд после запуска сменить своё имя
// События: READY, PAIRING_CODE, PAIRED, PAIRING_FAILED <код>, CONNECTED, DISCONNECTED, CLIP, SENT,
//   IMAGE <от кого> <размер> <sha256 hex>, IMAGE_SENT <получателей>, INFO <имя> os=… form=… caps=…,
//   RENAMED <старое> <новое>, NAME <своё новое имя>.
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
            Emit($"DEVICE {device.DeviceId} {device.Name} enabled={device.Enabled} {device.LastHost}:{device.LastPort} os={device.Os ?? "-"} form={device.Form ?? "-"} alias={device.Alias ?? "-"}");
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
            Emit($"FOUND {device.Name} id={device.DeviceId} pair={device.Pairing} port={device.Port} addresses={string.Join(',', device.Addresses)} os={device.Os ?? "-"} form={device.Form ?? "-"}");
        return 0;
}

var deviceType = new DeviceType(Option("--os") ?? "windows", Option("--form") ?? "desktop");
await using var node = new ClipveyNode(identity, store, deviceName, port, deviceType, imagesEnabled: Option("--images") != "off");
using var finished = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
// kill (SIGTERM) и Ctrl+C завершают штатно: узел закрывается и рассылает mDNS-«прощание».
// Иначе запись двойника ещё 2 минуты висит в кэше mDNS у других устройств.
void StopOnSignal(PosixSignalContext context)
{
    context.Cancel = true;
    finished.Cancel();
}
using var onTerminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, StopOnSignal);
using var onInterrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, StopOnSignal);

node.ClipReceived += (text, from) => Emit($"CLIP {from} {JsonSerializer.Serialize(text)}");
node.PairingSucceeded += peer => Emit($"PAIRED {peer}");
node.PairingFailed += reason => Emit($"PAIRING_FAILED {reason}");
node.ImageReceived += (data, mime, from) =>
{
    var hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    if (Option("--save-images") is { } saveDirectory)
    {
        Directory.CreateDirectory(saveDirectory);
        File.WriteAllBytes(Path.Combine(saveDirectory, hash + (mime == "image/jpeg" ? ".jpg" : ".png")), data);
    }
    Emit($"IMAGE {from} {data.Length} {hash}");
};
node.PeerUpdated += update =>
{
    if (update.OldName != update.Name)
        Emit($"RENAMED {update.OldName} {update.Name}");
    var caps = update.Caps.Count > 0 ? string.Join(',', update.Caps) : "-";
    Emit($"INFO {update.Name} os={update.Type.Os ?? "-"} form={update.Type.Form ?? "-"} caps={caps}");
};
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
            var candidate = Option("--pair-address") is { } address
                ? new DiscoveredDevice(target, int.Parse(address[(address.LastIndexOf(':') + 1)..]),
                    [System.Net.IPAddress.Parse(address[..address.LastIndexOf(':')])], null, true)
                : await WaitForCandidateAsync(node, target, finished.Token);
            Emit($"PAIRING_WITH {candidate.Name}");
            await node.PairWithAsync(candidate, (_, ct) => ReadCodeAsync(ct), () => Emit("CODE_ACCEPTED"), finished.Token);
        }
        catch (Exception e)
        {
            Emit($"PAIR_WITH_ERROR {e.Message}");
        }
    });
}

if (Option("--send") is not null || Option("--send-image") is not null)
{
    _ = Task.Run(async () =>
    {
        while (!finished.IsCancellationRequested && !node.Devices.Any(device => device.Connected))
            await Task.Delay(200);
        if (int.TryParse(Option("--send-delay"), out var delaySeconds))
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
        if (finished.IsCancellationRequested)
            return;
        if (Option("--send") is { } textToSend)
        {
            await node.BroadcastClipAsync(textToSend);
            Emit("SENT");
        }
        if (Option("--send-image") is { } imageFile)
        {
            var mime = Path.GetExtension(imageFile).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
            var recipients = node.SendImage(await File.ReadAllBytesAsync(imageFile), mime, Flag("--ignore-image-limit"));
            Emit($"IMAGE_SENT {recipients}");
        }
    });
}

if (arguments.IndexOf("--rename-after") is var renameIndex and >= 0 && renameIndex + 2 < arguments.Count
    && double.TryParse(arguments[renameIndex + 1], System.Globalization.CultureInfo.InvariantCulture, out var renameSeconds))
{
    var newName = arguments[renameIndex + 2];
    _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(renameSeconds), finished.Token);
        node.SetName(newName);
        Emit($"NAME {node.Name}");
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
