using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Clipvey.Core;
using ComIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using STATSTG = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace Clipvey.Windows;

// «Виртуальные» файлы в буфере обмена (docs/protocol.md, «Поведение сторон → Файлы в буфере»): больше 50 МиБ
// ничего не скачивается заранее. В буфер кладётся свой COM IDataObject (OleSetClipboard) с FileGroupDescriptorW
// и FileContents, и содержимое скачивается, когда Проводник вставляет: каждый GetData(FileContents) — новый поток
// поверх ClipveyNode.OpenFileStream, Проводник читает файлы по одному, по порядку, до конца.
//
// Вызовы GetData и чтения приходят на потоках пула RPC (объекты .NET для COM «проворные»), поэтому всё здесь
// потокобезопасно и не трогает интерфейс. Только GetData через Win32 (GetClipboardData) приходит на поток
// интерфейса — там нет ничего долгого.
//
// Обрыв передачи — ошибка HRESULT из Read (Проводник показывает окно ошибки и прерывает операцию); 0 байт раньше
// конца файла не отдаётся никогда: Проводник молча обрезал бы файл.

/// Что вставляется: элементы описания в порядке FileGroupDescriptorW. Элементы с относительным путём длиннее
/// MaxPath не предлагаются, вместе с папкой выпадает и её содержимое (у него путь ещё длиннее).
internal sealed class VirtualFileSet
{
    /// Относительный путь элемента — не длиннее, символов UTF-16. В FILEDESCRIPTORW помещается 259, но Проводник
    /// создаёт папку, только если весь путь с папкой назначения не длиннее 247 символов, а при первой такой
    /// ошибке («Расположение недоступно») прерывает всю вставку — проверено на Windows 11 24H2. 190 оставляют
    /// 57 символов на папку, куда вставляют: Рабочий стол, Загрузки, Документы, OneDrive — 25–40.
    public const int MaxPath = 190;

    /// Элемент дескриптора: номер в описании, относительный путь через «\», размер (null — папка).
    public sealed record Entry(int OfferIndex, string Path, long? Size)
    {
        public bool IsDirectory => Size is null;

        public string Name => Path[(Path.LastIndexOf('\\') + 1)..];
    }

    public FileOfferReceived Source { get; }
    public FileOffer Offer => Source.Offer;
    public IReadOnlyList<Entry> Entries { get; }

    /// Сколько элементов не предлагается (слишком длинный путь).
    public int Skipped { get; }

    /// Сумма размеров предлагаемых файлов.
    public long TotalBytes { get; }

    public VirtualFileSet(FileOfferReceived source)
    {
        Source = source;
        var local = FileNames.LocalPaths(source.Offer.Items, windows: true);
        var entries = new List<Entry>();
        long total = 0;
        var skipped = 0;
        for (var index = 0; index < source.Offer.Items.Count; index++)
        {
            var path = local[index].Replace('/', '\\');
            if (path.Length > MaxPath)
            {
                skipped++;
                continue;
            }
            var size = source.Offer.Items[index].Size;
            entries.Add(new Entry(index, path, size));
            total += size ?? 0;
        }
        Entries = entries;
        Skipped = skipped;
        TotalBytes = total;
    }

    /// FILEGROUPDESCRIPTORW: UINT cItems и cItems × FILEDESCRIPTORW (592 байта). Время изменения не передаётся
    /// протоколом — Проводник ставит текущее.
    public byte[] Descriptor()
    {
        const uint FdAttributes = 0x4, FdFileSize = 0x40, FdProgressUi = 0x4000;
        const uint FileAttributeDirectory = 0x10, FileAttributeNormal = 0x80;
        const int DescriptorBytes = 592;
        var data = new byte[4 + Entries.Count * DescriptorBytes];
        var span = data.AsSpan();
        BitConverter.TryWriteBytes(span, (uint)Entries.Count);
        for (var i = 0; i < Entries.Count; i++)
        {
            var entry = Entries[i];
            var item = span.Slice(4 + i * DescriptorBytes, DescriptorBytes);
            BitConverter.TryWriteBytes(item, FdAttributes | FdProgressUi | (entry.IsDirectory ? 0 : FdFileSize));
            // clsid (16), sizel (8), pointl (8) — нули.
            BitConverter.TryWriteBytes(item[36..], entry.IsDirectory ? FileAttributeDirectory : FileAttributeNormal);
            // ftCreationTime, ftLastAccessTime, ftLastWriteTime (по 8) — нули.
            var size = (ulong)(entry.Size ?? 0);
            BitConverter.TryWriteBytes(item[64..], (uint)(size >> 32));
            BitConverter.TryWriteBytes(item[68..], (uint)(size & 0xFFFFFFFF));
            Encoding.Unicode.GetBytes(entry.Path, item[72..]);
        }
        return data;
    }
}

/// Итог вставки.
internal enum PasteOutcome
{
    Running,
    Done,
    Cancelled,
    Failed,
}

/// Одна вставка виртуальных файлов: от первого GetData(FileContents) до конца — EndOperation, все файлы дочитаны,
/// отмена или ошибка. Прогресс — сумма прочитанного по каждому файлу (повторное чтение с начала не удваивает его).
/// Потокобезопасна. События вызываются на тех потоках, где это случилось, — интерфейс переносит их к себе.
internal sealed class VirtualPaste
{
    private readonly object _lock = new();
    private readonly VirtualFileSet _set;
    private readonly long[] _read;
    private readonly bool[] _complete;
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<VirtualFileStream> _streams = [];
    private int _remaining;
    private long _received;
    private bool _started;

    public VirtualPaste(VirtualFileSet set)
    {
        _set = set;
        _read = new long[set.Entries.Count];
        _complete = new bool[set.Entries.Count];
        for (var i = 0; i < set.Entries.Count; i++)
        {
            // Пустые файлы Проводник может и не читать: они готовы сразу.
            if (set.Entries[i].Size is > 0)
                _remaining++;
            else
                _complete[i] = true;
        }
    }

    public VirtualFileSet Set => _set;

    /// Отменяется при отмене и ошибке: ожидание данных в потоках прерывается.
    public CancellationToken Token => _cancel.Token;

    public PasteOutcome Outcome { get; private set; } = PasteOutcome.Running;

    /// Код ошибки передачи (Failed); null — ошибка на стороне того, кто вставлял.
    public FileTransferFailure? Failure { get; private set; }

    /// Имя файла, который читается сейчас.
    public string? CurrentName { get; private set; }

    /// Сколько байт получено (по всем файлам).
    public long Received => Interlocked.Read(ref _received);

    /// Начались чтения (показываем окошко только тогда: GetData без чтения — ещё не вставка).
    public event Action<VirtualPaste>? Started;

    /// Вставка закончилась (один раз). WasStarted — были ли чтения.
    public event Action<VirtualPaste>? Ended;

    public bool WasStarted
    {
        get
        {
            lock (_lock)
                return _started;
        }
    }

    public void Attach(VirtualFileStream stream)
    {
        lock (_lock)
        {
            if (Outcome == PasteOutcome.Running)
            {
                _streams.Add(stream);
                return;
            }
        }
        stream.Close();
    }

    /// Файл entry прочитан до position.
    public void Progress(int entry, long position)
    {
        bool first;
        lock (_lock)
        {
            if (Outcome != PasteOutcome.Running)
                return;
            if (position > _read[entry])
            {
                Interlocked.Add(ref _received, position - _read[entry]);
                _read[entry] = position;
            }
            CurrentName = _set.Entries[entry].Name;
            first = !_started;
            _started = true;
        }
        if (first)
            Started?.Invoke(this);
    }

    /// Файл entry дочитан до конца.
    public void Completed(int entry)
    {
        lock (_lock)
        {
            if (Outcome != PasteOutcome.Running || _complete[entry])
                return;
            _complete[entry] = true;
            _remaining--;
            if (_remaining > 0)
                return;
        }
        Finish(PasteOutcome.Done, null, "все файлы прочитаны");
    }

    public void Fail(FileTransferFailure failure, string reason) => Finish(PasteOutcome.Failed, failure, reason);

    /// «Отмена» в окошке: потоки прерываются, Проводник получает ошибку «отменено».
    public void Cancel() => Finish(PasteOutcome.Cancelled, null, "отменено в окошке Clipvey");

    /// IDataObjectAsyncCapability.EndOperation: итог вставки от Проводника.
    public void EndOperation(int result)
    {
        if (result >= 0)
            Finish(PasteOutcome.Done, null, "Проводник сообщил об успехе");
        else if (result == VirtualFileStream.ErrorCancelled)
            Finish(PasteOutcome.Cancelled, null, "отменено в Проводнике");
        else
            Finish(PasteOutcome.Failed, null, $"Проводник сообщил об ошибке 0x{result:X8}");
    }

    private void Finish(PasteOutcome outcome, FileTransferFailure? failure, string reason)
    {
        List<VirtualFileStream> streams;
        bool started;
        lock (_lock)
        {
            if (Outcome != PasteOutcome.Running)
                return;
            Outcome = outcome;
            Failure = failure;
            streams = [.. _streams];
            _streams.Clear();
            started = _started;
        }
        Log.Write($"Вставка файлов {_set.Offer.Id} от «{_set.Source.DeviceName}»: {outcome switch
        {
            PasteOutcome.Done => "готово",
            PasteOutcome.Cancelled => "отменена",
            _ => "прервана",
        }} ({reason}), получено {Received} из {_set.TotalBytes} байт{(started ? "" : ", чтений не было")}");
        if (outcome != PasteOutcome.Done)
            _cancel.Cancel();
        foreach (var stream in streams)
            stream.Close();
        Ended?.Invoke(this);
    }
}

/// Своя реализация IDataObject для OleSetClipboard: FileGroupDescriptorW, FileContents (IStream по lindex),
/// Preferred DropEffect (копирование), маркер ClipveyRemote и ExcludeClipboardContentFromMonitorProcessing
/// (менеджеры буфера не должны читать содержимое и запускать скачивание гигабайт).
/// CF_HDROP нет намеренно: его запрашивают все окна Проводника при каждом изменении буфера.
[ComVisible(true)]
public sealed class VirtualFileDataObject : ComIDataObject, IDataObjectAsyncCapability
{
    private const int SOk = 0;
    private const int DvEFormatEtc = unchecked((int)0x80040064);
    private const int DvETymed = unchecked((int)0x80040069);
    private const int DvELindex = unchecked((int)0x80040068);
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int OleEAdviseNotSupported = unchecked((int)0x80040003);

    private static readonly short DescriptorFormat = Register("FileGroupDescriptorW");
    private static readonly short ContentsFormat = Register("FileContents");
    private static readonly short DropEffectFormat = Register("Preferred DropEffect");
    private static readonly short ExcludeFormat = Register("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly short RemoteFormat = Register(ClipboardWatcher.RemoteFormatName);

    private readonly object _lock = new();
    private readonly VirtualFileSet _set;
    private readonly ClipveyNode _node;
    private readonly Action<VirtualPaste> _onPasteStarted;
    private readonly Action<VirtualPaste> _onPasteEnded;
    private readonly byte[] _descriptor;
    private VirtualPaste? _paste;
    private bool _asyncMode = true;
    private bool _inOperation;

    /// onPasteStarted — начались чтения новой вставки, onPasteEnded — она закончилась (вызываются на потоках RPC).
    internal VirtualFileDataObject(VirtualFileSet set, ClipveyNode node, Action<VirtualPaste> onPasteStarted, Action<VirtualPaste> onPasteEnded)
    {
        _set = set;
        _node = node;
        _onPasteStarted = onPasteStarted;
        _onPasteEnded = onPasteEnded;
        _descriptor = set.Descriptor();
    }

    internal VirtualFileSet Set => _set;

    /// Текущая вставка; закончилась — начинается новая (повторная вставка читает всё заново).
    private VirtualPaste CurrentPaste()
    {
        lock (_lock)
        {
            if (_paste is { Outcome: PasteOutcome.Running } running)
                return running;
            var paste = new VirtualPaste(_set);
            paste.Started += _onPasteStarted;
            paste.Ended += _onPasteEnded;
            _paste = paste;
            return paste;
        }
    }

    /// Отменить идущую вставку (например, при выходе из программы).
    internal void CancelPaste()
    {
        VirtualPaste? paste;
        lock (_lock)
            paste = _paste;
        paste?.Cancel();
    }

    public int QueryGetData(ref FORMATETC format) => Check(ref format, out _);

    private int Check(ref FORMATETC format, out TYMED tymed)
    {
        tymed = TYMED.TYMED_NULL;
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT)
            return DvEFormatEtc;
        if (format.cfFormat == ContentsFormat)
        {
            tymed = TYMED.TYMED_ISTREAM;
            if ((format.tymed & TYMED.TYMED_ISTREAM) == 0)
                return DvETymed;
            if (format.lindex != -1 && (format.lindex < 0 || format.lindex >= _set.Entries.Count || _set.Entries[format.lindex].IsDirectory))
                return DvELindex;
            return SOk;
        }
        if (format.cfFormat == DescriptorFormat || format.cfFormat == DropEffectFormat || format.cfFormat == ExcludeFormat
            || format.cfFormat == RemoteFormat)
        {
            tymed = TYMED.TYMED_HGLOBAL;
            return (format.tymed & TYMED.TYMED_HGLOBAL) == 0 ? DvETymed : SOk;
        }
        return DvEFormatEtc;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        var result = Check(ref format, out var tymed);
        if (result == SOk && format.cfFormat == ContentsFormat && format.lindex == -1)
            result = DvELindex;
        if (result != SOk)
            throw new COMException("Формат не поддерживается", result);
        if (tymed == TYMED.TYMED_ISTREAM)
        {
            var entry = format.lindex;
            var paste = CurrentPaste();
            var stream = new VirtualFileStream(_node, _set, entry, paste);
            paste.Attach(stream);
            medium.tymed = TYMED.TYMED_ISTREAM;
            // Указатель с AddRef уходит получателю — он его и освободит.
            medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream));
            medium.pUnkForRelease = null;
            return;
        }
        byte[] data;
        if (format.cfFormat == DescriptorFormat)
            data = _descriptor;
        else if (format.cfFormat == DropEffectFormat)
            data = BitConverter.GetBytes(1); // DROPEFFECT_COPY
        else if (format.cfFormat == RemoteFormat)
            data = "1"u8.ToArray();
        else
            data = new byte[4]; // Exclude…: содержимое не важно
        medium.tymed = TYMED.TYMED_HGLOBAL;
        medium.unionmember = Native.HGlobalFrom(data);
        medium.pUnkForRelease = null;
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
        throw new COMException("GetDataHere", ENotImpl);

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return ENotImpl;
    }

    /// Проводник сообщает сюда итоги (Performed DropEffect, Paste Succeeded, TargetCLSID): принимаем и освобождаем.
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release)
    {
        if (release)
            Native.ReleaseStgMedium(ref medium);
    }

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET)
            throw new COMException("EnumFormatEtc", ENotImpl);
        FORMATETC[] formats =
        [
            Make(DescriptorFormat, TYMED.TYMED_HGLOBAL),
            Make(ContentsFormat, TYMED.TYMED_ISTREAM),
            Make(DropEffectFormat, TYMED.TYMED_HGLOBAL),
            Make(RemoteFormat, TYMED.TYMED_HGLOBAL),
            Make(ExcludeFormat, TYMED.TYMED_HGLOBAL),
        ];
        var result = Native.SHCreateStdEnumFmtEtc((uint)formats.Length, formats, out var enumerator);
        if (result != SOk)
            throw new COMException("SHCreateStdEnumFmtEtc", result);
        return enumerator;

        static FORMATETC Make(short format, TYMED tymed) =>
            new() { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = tymed };
    }

    public int DAdvise(ref FORMATETC format, ADVF advf, IAdviseSink sink, out int connection)
    {
        connection = 0;
        return OleEAdviseNotSupported;
    }

    public void DUnadvise(int connection) => throw new COMException("DUnadvise", OleEAdviseNotSupported);

    public int EnumDAdvise(out IEnumSTATDATA? enumerator)
    {
        enumerator = null;
        return OleEAdviseNotSupported;
    }

    // IDataObjectAsyncCapability: Проводник вставляет в фоне и сообщает итог в EndOperation (0 — успех,
    // 0x800704C7 — отменено). Без асинхронного режима EndOperation не приходит.

    public void SetAsyncMode(bool doOpAsync)
    {
        lock (_lock)
            _asyncMode = doOpAsync;
    }

    public void GetAsyncMode(out bool isOpAsync)
    {
        lock (_lock)
            isOpAsync = _asyncMode;
    }

    public void StartOperation(IBindCtx? reserved)
    {
        lock (_lock)
            _inOperation = true;
    }

    public void InOperation(out bool inAsyncOp)
    {
        lock (_lock)
            inAsyncOp = _inOperation;
    }

    public void EndOperation(int result, IBindCtx? reserved, uint effects)
    {
        VirtualPaste? paste;
        lock (_lock)
        {
            _inOperation = false;
            paste = _paste;
        }
        paste?.EndOperation(result);
    }

    private static short Register(string name) => unchecked((short)Native.RegisterClipboardFormat(name));
}

/// IStream одного файла поверх ClipveyNode.OpenFileStream: последовательное чтение, запрос к источнику — при первом
/// Read. Read заполняет буфер целиком (или до конца файла): короткое чтение потребитель может принять за конец.
[ComVisible(true)]
public sealed class VirtualFileStream : IStream
{
    /// HRESULT_FROM_WIN32(ERROR_UNEXP_NET_ERR): Проводник показывает ошибку и прерывает копирование.
    public const int NetworkError = unchecked((int)0x8007003B);

    /// HRESULT_FROM_WIN32(ERROR_CANCELLED).
    public const int ErrorCancelled = unchecked((int)0x800704C7);

    private const int StgEInvalidFunction = unchecked((int)0x80030001);
    private const int StgEAccessDenied = unchecked((int)0x80030005);
    private const int ENotImpl = unchecked((int)0x80004001);

    private readonly object _lock = new();
    private readonly ClipveyNode _node;
    private readonly VirtualFileSet _set;
    private readonly int _entry;
    private readonly VirtualPaste _paste;
    private readonly long _size;
    private Stream? _inner;
    private long _position;
    private bool _closed;

    internal VirtualFileStream(ClipveyNode node, VirtualFileSet set, int entry, VirtualPaste paste)
    {
        _node = node;
        _set = set;
        _entry = entry;
        _paste = paste;
        _size = set.Entries[entry].Size ?? 0;
    }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        var total = ReadCore(pv, cb);
        if (pcbRead != IntPtr.Zero)
            Marshal.WriteInt32(pcbRead, total);
    }

    private int ReadCore(byte[] buffer, int count)
    {
        lock (_lock)
        {
            if (_paste.Token.IsCancellationRequested || (_closed && _position < _size))
                throw new COMException("Вставка отменена", ErrorCancelled);
            if (_position >= _size)
            {
                _paste.Completed(_entry);
                return 0;
            }
            var want = (int)Math.Min(Math.Min(count, buffer.Length), _size - _position);
            var total = 0;
            while (true)
            {
                try
                {
                    if (_inner is null)
                    {
                        _inner = _node.OpenFileStream(_set.Offer, _set.Source.DeviceId, _set.Entries[_entry].OfferIndex, _paste.Token);
                        if (_position > 0)
                            _inner.Seek(_position, SeekOrigin.Begin);
                    }
                    while (total < want)
                    {
                        var read = _inner.Read(buffer, total, want - total);
                        if (read == 0)
                            throw new FileTransferException(FileTransferFailure.ProtocolError, "данные кончились раньше конца файла");
                        total += read;
                        _position += read;
                        _paste.Progress(_entry, _position);
                    }
                    if (_position >= _size)
                    {
                        _paste.Completed(_entry);
                        CloseInner();
                    }
                    return total;
                }
                catch (OperationCanceledException)
                {
                    throw new COMException("Вставка отменена", ErrorCancelled);
                }
                catch (FileTransferException e) when (e.Failure == FileTransferFailure.DeviceUnavailable && _reconnects < MaxReconnects)
                {
                    // Сеанс сменился (устройства подключились друг к другу одновременно, и остался второй сеанс)
                    // или коротко оборвался: продолжить с того же места по новому сеансу.
                    CloseInner();
                    if (!Sessions.WaitFor(_node, _set.Source.DeviceId, _paste.Token))
                        throw Fail(e.Failure, e.Message);
                    _reconnects++;
                    Log.Write($"Чтение «{_set.Entries[_entry].Path}» ({_set.Offer.Id}): сеанс сменился, продолжаю с {_position} байт");
                }
                catch (FileTransferException e)
                {
                    throw Fail(e.Failure, e.Message);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    // Поток закрыт отменой или ошибкой вставки, пока Read ждал данных.
                    if (_paste.Token.IsCancellationRequested)
                        throw new COMException("Вставка отменена", ErrorCancelled);
                    throw Fail(FileTransferFailure.DeviceUnavailable, e.Message);
                }
            }
        }
    }

    /// Сколько раз один поток продолжает чтение по новому сеансу.
    private const int MaxReconnects = 3;
    private int _reconnects;

    private COMException Fail(FileTransferFailure failure, string message)
    {
        Log.Write($"Чтение «{_set.Entries[_entry].Path}» ({_set.Offer.Id}) прервано: {message}");
        CloseInner();
        _paste.Fail(failure, message);
        return new COMException(message, NetworkError);
    }

    /// Закрыть запрос к источнику (конец вставки, отмена). Ждущий Read прерывается отменой вставки.
    internal void Close()
    {
        Stream? inner;
        lock (_lock)
        {
            _closed = true;
            inner = _inner;
            _inner = null;
        }
        inner?.Dispose();
    }

    private void CloseInner()
    {
        _inner?.Dispose();
        _inner = null;
    }

    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("Только чтение", StgEAccessDenied);

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        lock (_lock)
        {
            var target = dwOrigin switch
            {
                0 => dlibMove,
                1 => _position + dlibMove,
                2 => _size + dlibMove,
                _ => -1,
            };
            if (target < 0)
                throw new COMException("Seek", StgEInvalidFunction);
            if (target != _position)
            {
                try
                {
                    _inner?.Seek(target, SeekOrigin.Begin);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    throw new COMException(e.Message, NetworkError);
                }
                _position = target;
            }
            if (plibNewPosition != IntPtr.Zero)
                Marshal.WriteInt64(plibNewPosition, _position);
        }
    }

    public void SetSize(long libNewSize) => throw new COMException("SetSize", StgEInvalidFunction);

    public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten)
    {
        var buffer = new byte[256 * 1024];
        long total = 0;
        while (total < cb)
        {
            var read = ReadCore(buffer, (int)Math.Min(buffer.Length, cb - total));
            if (read == 0)
                break;
            pstm.Write(buffer, read, IntPtr.Zero);
            total += read;
        }
        if (pcbRead != IntPtr.Zero)
            Marshal.WriteInt64(pcbRead, total);
        if (pcbWritten != IntPtr.Zero)
            Marshal.WriteInt64(pcbWritten, total);
    }

    public void Commit(int grfCommitFlags)
    {
    }

    public void Revert()
    {
    }

    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("LockRegion", StgEInvalidFunction);

    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("UnlockRegion", StgEInvalidFunction);

    public void Stat(out STATSTG pstatstg, int grfStatFlag)
    {
        pstatstg = new STATSTG
        {
            pwcsName = null!,
            type = 2, // STGTY_STREAM
            cbSize = _size,
        };
    }

    public void Clone(out IStream ppstm) => throw new COMException("Clone", ENotImpl);
}

/// IDataObjectAsyncCapability (IAsyncOperation): получатель вставляет в фоне и сообщает итог.
[ComImport, Guid("3D8B0590-F691-11d2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    void SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool doOpAsync);

    void GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool isOpAsync);

    void StartOperation(IBindCtx? reserved);

    void InOperation([MarshalAs(UnmanagedType.Bool)] out bool inAsyncOp);

    void EndOperation(int result, IBindCtx? reserved, uint effects);
}

/// Сеансы узла для продолжения передачи после смены сеанса.
internal static class Sessions
{
    /// Подождать (до 5 с), пока с устройством снова есть сеанс. Сеанс, сменившийся при одновременном подключении,
    /// уже на месте; короткий обрыв успевает восстановиться, если другое устройство подключится само.
    public static bool WaitFor(ClipveyNode node, string deviceId, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (!ct.IsCancellationRequested)
        {
            if (node.Devices.Any(device => device.DeviceId == deviceId && device.Connected))
                return true;
            if (Environment.TickCount64 >= deadline)
                return false;
            Thread.Sleep(200);
        }
        return false;
    }
}

/// Буфер обмена OLE: положить свой объект и убрать его при выходе.
internal static class OleClipboard
{
    private const int ClipbrdECantOpen = unchecked((int)0x800401D0);

    /// Для OleSetClipboard поток интерфейса должен быть инициализирован OLE (WPF делает это не сразу).
    public static void Initialize()
    {
        var result = Native.OleInitialize(IntPtr.Zero);
        if (result < 0)
            Log.Write($"OleInitialize: 0x{result:X8}");
    }

    /// Положить объект в буфер (поток интерфейса). Буфер может быть занят другой программой — несколько попыток.
    public static bool Set(ComIDataObject data)
    {
        var result = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            result = Native.OleSetClipboard(data);
            if (result != ClipbrdECantOpen)
                break;
            Thread.Sleep(30);
        }
        if (result < 0)
            Log.Write($"Не удалось положить файлы в буфер: OleSetClipboard 0x{result:X8}");
        return result >= 0;
    }

    public static bool IsCurrent(ComIDataObject data) => Native.OleIsCurrentClipboard(data) == 0;

    /// Если в буфере ещё наш объект — очистить буфер: после выхода вставить из него всё равно нельзя.
    public static void ClearIfCurrent(ComIDataObject data)
    {
        if (!IsCurrent(data))
            return;
        var result = Native.OleSetClipboard(null);
        Log.Write($"Буфер очищен при выходе (в нём были виртуальные файлы): 0x{result:X8}");
    }
}

internal static class Native
{
    [DllImport("ole32.dll")]
    public static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    public static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] ComIDataObject? data);

    [DllImport("ole32.dll")]
    public static extern int OleIsCurrentClipboard([MarshalAs(UnmanagedType.Interface)] ComIDataObject data);

    [DllImport("ole32.dll")]
    public static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("shell32.dll")]
    public static extern int SHCreateStdEnumFmtEtc(uint count, [MarshalAs(UnmanagedType.LPArray)] FORMATETC[] formats, out IEnumFORMATETC enumerator);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    public static extern uint RegisterClipboardFormat(string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

    /// HGLOBAL с копией данных; освобождает получатель (ReleaseStgMedium).
    public static IntPtr HGlobalFrom(byte[] data)
    {
        var memory = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (UIntPtr)Math.Max(1, data.Length));
        if (memory == IntPtr.Zero)
            throw new COMException("Нет памяти", unchecked((int)0x8007000E));
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(memory);
            throw new COMException("Нет памяти", unchecked((int)0x8007000E));
        }
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(memory);
        return memory;
    }
}
