using Clipvey.Core;

namespace Clipvey.Tests;

/// Повтор того же содержимого буфера в течение 2 с после отправки не отправляется (RepeatedCopyFilter).
public class CopyRepeatTests
{
    private long _now = 1_000_000;

    private RepeatedCopyFilter Filter() => new(() => _now);

    [Fact]
    public void SameContentWithinWindowIsRepeat()
    {
        var filter = Filter();
        var text = RepeatedCopyFilter.TextKey("привет");
        Assert.False(filter.IsRepeat(text));
        _now += 5; // Программа на WinForms: второе изменение буфера сразу за первым.
        Assert.True(filter.IsRepeat(RepeatedCopyFilter.TextKey("привет")));
        _now += 1994;
        Assert.True(filter.IsRepeat(text));
        _now += 1; // Ровно 2 с после отправки — уже не повтор.
        Assert.False(filter.IsRepeat(text));
    }

    [Fact]
    public void WindowCountsFromSendNotFromRepeat()
    {
        var filter = Filter();
        var image = RepeatedCopyFilter.ImageKey([1, 2, 3]);
        Assert.False(filter.IsRepeat(image));
        _now += 1500;
        Assert.True(filter.IsRepeat(image));
        _now += 600; // 2,1 с после отправки, 0,6 с после повтора.
        Assert.False(filter.IsRepeat(image));
        _now += 100;
        Assert.True(filter.IsRepeat(image));
    }

    [Fact]
    public void OtherContentOrRemoteWriteResets()
    {
        var filter = Filter();
        var a = RepeatedCopyFilter.TextKey("a");
        Assert.False(filter.IsRepeat(a));
        _now += 100;
        Assert.False(filter.IsRepeat(RepeatedCopyFilter.TextKey("b")));
        _now += 100;
        Assert.False(filter.IsRepeat(a)); // Скопировали A, B и снова A — все три отправляются.

        // С другого устройства пришло другое и записано в буфер — то же A снова отправляется.
        _now += 100;
        filter.Reset();
        Assert.False(filter.IsRepeat(a));
        _now += 100;
        Assert.True(filter.IsRepeat(a));
    }

    [Fact]
    public void KeysDistinguishKindsAndIgnorePathCase()
    {
        Assert.NotEqual(RepeatedCopyFilter.TextKey("x"), RepeatedCopyFilter.ImageKey("x"u8));
        Assert.NotEqual(RepeatedCopyFilter.TextKey("x"), RepeatedCopyFilter.FilesKey(["x"]));
        Assert.Equal(RepeatedCopyFilter.FilesKey([@"C:\Папка\Отчёт.PDF", @"C:\b"]), RepeatedCopyFilter.FilesKey([@"c:\папка\отчёт.pdf", @"C:\B"]));
        Assert.NotEqual(RepeatedCopyFilter.FilesKey(["a", "b"]), RepeatedCopyFilter.FilesKey(["b", "a"]));
        Assert.NotEqual(RepeatedCopyFilter.TextKey("a"), RepeatedCopyFilter.TextKey("A"));
    }
}
