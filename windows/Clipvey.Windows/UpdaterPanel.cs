using Clipvey.Core;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Обновления в панели трея: полоса «Доступна версия X — обновить?» и пункты настроек.
internal sealed partial class TrayPanel
{
    private void AddUpdateBanner()
    {
        if (!_updater.ShowsBanner || _updater.Available is not { } release)
            return;
        var card = NewCard();
        var column = NewColumn(P.Card);
        column.Controls.Add(NewLabel(L($"Доступна версия {release.Version} — обновить?", $"Version {release.Version} is available. Update?"),
            Theme.Text(14, _dpi, FontStyle.Bold), P.Text, P.Card, CardInnerWidth, new Padding(0)));
        if (_updater.Phase is UpdatePhase.Downloading or UpdatePhase.Installing)
        {
            column.Controls.Add(NewLabel(_updater.Phase == UpdatePhase.Downloading
                    ? L("Загрузка и проверка…", "Downloading and verifying…")
                    : L("Установка…", "Installing…"),
                Theme.Text(13, _dpi), P.SecondaryText, P.Card, CardInnerWidth, new Padding(0, S(6), 0, 0)));
        }
        else
        {
            column.Controls.Add(NewRow(P.Card,
                NewButton(L("Обновить", "Update"), () => _ = _updater.InstallAsync(), ButtonKind.Accent, P.Card, "update-install",
                    enabled: _updater.Phase == UpdatePhase.Idle),
                NewButton(L("Позже", "Later"), _updater.Dismiss, ButtonKind.Standard, P.Card, "update-later")));
        }
        AddUpdateNotice(column);
        card.Controls.Add(column);
        _root.Controls.Add(card);
    }

    private void AddUpdateSettings()
    {
        var card = NewCard();
        var table = NewTable(CardInnerWidth, P.Card, new Padding(0));
        var labelWidth = CardInnerWidth - S(150);

        var autoText = L("Проверять обновления автоматически", "Check for updates automatically");
        var autoLabel = NewLabel(autoText, Theme.Text(14, _dpi), P.Text, P.Card, labelWidth, new Padding(0));
        autoLabel.Anchor = AnchorStyles.Left;
        var auto = new ToggleSwitch(P, _dpi, P.Card, autoText)
        {
            Checked = _updater.ChecksAutomatically,
            Name = "update-auto",
            Anchor = AnchorStyles.Right,
        };
        auto.Toggled += (_, _) => _updater.ChecksAutomatically = auto.Checked;
        table.Controls.Add(autoLabel, 0, 0);
        table.Controls.Add(auto, 2, 0);

        var version = _updater.CurrentVersion is { } current ? L($"Версия {current}", $"Version {current}") : L("Версия неизвестна", "Unknown version");
        var versionLabel = NewLabel(version, Theme.Text(13, _dpi), P.SecondaryText, P.Card, labelWidth, new Padding(0, S(10), 0, 0));
        versionLabel.Anchor = AnchorStyles.Left;
        var check = NewButton(_updater.Phase == UpdatePhase.Checking ? L("Проверка…", "Checking…") : L("Проверить сейчас", "Check Now"),
            () => _ = _updater.CheckAsync(manual: true), ButtonKind.Standard, P.Card, "update-check",
            enabled: _updater.Phase == UpdatePhase.Idle && _updater.CurrentVersion is not null);
        check.Anchor = AnchorStyles.Right;
        check.Margin = new Padding(0, S(10), 0, 0);
        table.Controls.Add(versionLabel, 0, 1);
        table.Controls.Add(check, 2, 1);

        var column = NewColumn(P.Card);
        column.Controls.Add(table);
        if (_updater.Available is { } release && _updater.Dismissed)
        {
            var install = NewButton(L($"Обновить до {release.Version}", $"Update to {release.Version}"), () => _ = _updater.InstallAsync(),
                ButtonKind.Accent, P.Card, "update-install-settings", enabled: _updater.Phase == UpdatePhase.Idle);
            install.Margin = new Padding(0, S(8), 0, 0);
            column.Controls.Add(install);
        }
        if (!_updater.ShowsBanner)
            AddUpdateNotice(column);
        card.Controls.Add(column);
        _root.Controls.Add(card);
    }

    private void AddUpdateNotice(Control column)
    {
        if (_updater.Notice is not { } notice)
            return;
        column.Controls.Add(NewLabel(_updater.NoticeText, Theme.Text(13, _dpi),
            notice == UpdateNotice.UpToDate ? P.SecondaryText : P.Warning, P.Card, CardInnerWidth, new Padding(0, S(6), 0, 0)));
    }
}
