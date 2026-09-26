using System.Runtime.InteropServices;
using Clipvey.Core;

namespace Clipvey.Windows;

/// Следит за буфером обмена Windows (AddClipboardFormatListener) и записывает в него текст с других устройств.
/// Работает на потоке интерфейса: буфер Windows требует STA-потока.
internal sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly IntPtr MessageOnlyParent = new(-3);

    /// Отметка «текст пришёл с другого устройства»: такой текст не отправляется обратно.
    private const string RemoteFormat = "ClipveyRemote";

    /// Так менеджеры паролей и системные программы просят не передавать содержимое буфера.
    private static readonly string[] SecretFormats = ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore"];

    private readonly Action<string> _onCopy;

    public ClipboardWatcher(Action<string> onCopy)
    {
        _onCopy = onCopy;
        CreateHandle(new CreateParams { Parent = MessageOnlyParent });
        if (!AddClipboardFormatListener(Handle))
            Log.Write($"Не удалось подписаться на изменения буфера (ошибка {Marshal.GetLastWin32Error()})");
    }

    /// Записать текст с другого устройства (переводы строк \n превращаются в \r\n).
    public void WriteRemote(string text)
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        data.SetData(RemoteFormat, "1");
        try
        {
            Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 50);
            Log.Write($"Записано в буфер: {text.Length} символов");
        }
        catch (ExternalException e)
        {
            Log.Write($"Не удалось записать в буфер (занят другой программой): {e.Message}");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmClipboardUpdate)
            OnClipboardUpdate();
        base.WndProc(ref m);
    }

    private void OnClipboardUpdate()
    {
        // Программа-источник может ещё держать буфер открытым — пробуем несколько раз.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data is null)
                    return;
                var formats = data.GetFormats();
                if (formats.Contains(RemoteFormat))
                    return;
                if (formats.Any(format => SecretFormats.Contains(format)))
                {
                    Log.Write("Секретное содержимое (пароль) не передаётся");
                    return;
                }
                if (data.GetData(DataFormats.UnicodeText) is string { Length: > 0 } text)
                    _onCopy(text.Replace("\r\n", "\n"));
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        Log.Write("Буфер обмена занят другой программой — изменение пропущено");
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
