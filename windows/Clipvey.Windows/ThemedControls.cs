using System.Drawing.Drawing2D;
using static Clipvey.Windows.Localization;

namespace Clipvey.Windows;

/// Карточка: скруглённый прямоугольник с тонкой рамкой. Дочерние элементы — с фоном palette.Card.
internal sealed class Card : Panel
{
    private readonly Palette _palette;
    private readonly float _radius;

    public Card(Palette palette, int dpi)
    {
        _palette = palette;
        _radius = 8f * dpi / 96f;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = palette.Background;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var padding = 12 * dpi / 96;
        Padding = new Padding(padding + 2 * dpi / 96, padding, padding, padding);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.RoundedRect(rect, _radius);
        using var fill = new SolidBrush(_palette.Card);
        using var border = new Pen(_palette.CardBorder);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);
    }
}

/// Вид кнопки.
internal enum ButtonKind
{
    /// Обычная кнопка с рамкой.
    Standard,

    /// Главное действие: залита акцентным цветом.
    Accent,

    /// Кнопка-ссылка без фона (второстепенное действие).
    Subtle,

    /// Опасное действие: красный текст.
    Danger,
}

/// Кнопка в стиле Windows 11: скруглённая, в цветах темы. Наследует Button, поэтому
/// клавиатура (Tab, пробел, Enter), доступность и Enabled работают как у обычной кнопки.
internal sealed class ThemedButton : Button
{
    private readonly Palette _palette;
    private readonly ButtonKind _kind;
    private readonly int _dpi;
    private bool _hover;
    private bool _pressed;

    public ThemedButton(string text, Palette palette, ButtonKind kind, int dpi, Color background)
    {
        _palette = palette;
        _kind = kind;
        _dpi = dpi;
        Text = text;
        UseMnemonic = false;
        FlatStyle = FlatStyle.Flat;
        BackColor = background;
        Font = Theme.Text(14, dpi);
        AutoSize = true;
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 0, 8 * dpi / 96, 0);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    private int Scale(int pixels) => pixels * _dpi / 96;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        var horizontal = _kind == ButtonKind.Subtle ? Scale(8) : Scale(14);
        return new Size(text.Width + horizontal * 2, Math.Max(Scale(32), text.Height + Scale(10)));
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        if (mevent.Button == MouseButtons.Left)
            _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var radius = Scale(4);

        Color? fill = _kind switch
        {
            ButtonKind.Accent => !Enabled ? _palette.Disabled : _pressed || _hover ? _palette.AccentHover : _palette.Accent,
            ButtonKind.Subtle => _pressed ? _palette.ButtonPressed : _hover ? _palette.ButtonHover : null,
            _ => _pressed ? _palette.ButtonPressed : _hover ? _palette.ButtonHover : _palette.Button,
        };
        using (var path = Theme.RoundedRect(rect, radius))
        {
            if (fill is { } color)
            {
                using var brush = new SolidBrush(color);
                g.FillPath(brush, path);
            }
            if (_kind is ButtonKind.Standard or ButtonKind.Danger)
            {
                using var pen = new Pen(_palette.ButtonBorder);
                g.DrawPath(pen, path);
            }
        }

        var textColor = !Enabled ? (_kind == ButtonKind.Accent ? _palette.Card : _palette.Disabled)
            : _kind switch
            {
                ButtonKind.Accent => _palette.AccentText,
                ButtonKind.Subtle => _palette.Accent,
                ButtonKind.Danger => _palette.Danger,
                _ => _palette.Text,
            };
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

        if (Focused && ShowFocusCues)
        {
            var focus = new RectangleF(1, 1, Width - 3, Height - 3);
            using var path = Theme.RoundedRect(focus, radius);
            using var pen = new Pen(_palette.Text, Math.Max(1.5f, Scale(2)));
            g.DrawPath(pen, path);
        }
    }
}

/// Переключатель «вкл/выкл» в стиле Windows 11 вместо CheckBox.
/// Фокус с клавиатуры (Tab), переключение пробелом и кликом, для экранного диктора — флажок с состоянием.
internal sealed class ToggleSwitch : Control
{
    private readonly Palette _palette;
    private readonly int _dpi;
    private bool _checked;

    public ToggleSwitch(Palette palette, int dpi, Color background, string accessibleName)
    {
        _palette = palette;
        _dpi = dpi;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
        // Каждый клик переключает, даже быстрый двойной.
        SetStyle(ControlStyles.StandardDoubleClick, false);
        TabStop = true;
        BackColor = background;
        Cursor = Cursors.Hand;
        Size = new Size(Scale(44), Scale(24));
        Margin = new Padding(Scale(8), 0, 0, 0);
        AccessibleName = accessibleName;
        AccessibleRole = AccessibleRole.CheckButton;
    }

    /// Пользователь переключил (клик, пробел или диктор). Установка Checked из кода событие не вызывает.
    public event EventHandler? Toggled;

    [System.ComponentModel.DefaultValue(false)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
                return;
            _checked = value;
            Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        }
    }

    private int Scale(int pixels) => pixels * _dpi / 96;

    public void Toggle()
    {
        Checked = !Checked;
        Toggled?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        Toggle();
        base.OnClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            Toggle();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var track = new RectangleF(Scale(3), Scale(3), Width - Scale(6) - 1, Height - Scale(6) - 1);
        var radius = track.Height / 2;
        using (var path = Theme.RoundedRect(track, radius))
        {
            if (_checked)
            {
                using var fill = new SolidBrush(Enabled ? _palette.Accent : _palette.Disabled);
                g.FillPath(fill, path);
            }
            else
            {
                using var pen = new Pen(Enabled ? _palette.ToggleOff : _palette.Disabled, Math.Max(1f, Scale(1)));
                g.DrawPath(pen, path);
            }
        }

        var knob = track.Height - Scale(8);
        var x = _checked ? track.Right - Scale(4) - knob : track.Left + Scale(4);
        using (var brush = new SolidBrush(_checked ? _palette.AccentText : _palette.ToggleOff))
            g.FillEllipse(brush, x, track.Top + (track.Height - knob) / 2, knob, knob);

        if (Focused && ShowFocusCues)
        {
            var focus = new RectangleF(1, 1, Width - 3, Height - 3);
            using var path = Theme.RoundedRect(focus, focus.Height / 2);
            using var pen = new Pen(_palette.Text, Math.Max(1.5f, Scale(2)));
            g.DrawPath(pen, path);
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessibleObject(this);

    private sealed class ToggleAccessibleObject(ToggleSwitch owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.CheckButton;

        public override AccessibleStates State =>
            base.State | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);

        public override string DefaultAction => owner.Checked ? L("Выключить", "Turn off") : L("Включить", "Turn on");

        public override void DoDefaultAction() => owner.Toggle();
    }
}

/// Меню в цветах темы: фон, выделение, текст, галочки и стрелки подменю.
internal sealed class ThemedMenuRenderer(Palette palette) : ToolStripProfessionalRenderer(new ThemedMenuColors(palette))
{
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? palette.Text : palette.SecondaryText;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = palette.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // Своя галочка: стандартная — чёрная картинка, на тёмном фоне её не видно.
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = e.ImageRectangle;
        var side = Math.Min(rect.Width, rect.Height);
        var box = new RectangleF(rect.X + (rect.Width - side) / 2f, rect.Y + (rect.Height - side) / 2f, side, side);
        using var pen = new Pen(palette.Text, Math.Max(1.5f, side / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLines(pen,
        [
            new PointF(box.Left + side * 0.2f, box.Top + side * 0.52f),
            new PointF(box.Left + side * 0.42f, box.Top + side * 0.72f),
            new PointF(box.Left + side * 0.8f, box.Top + side * 0.3f),
        ]);
    }
}

internal sealed class ThemedMenuColors(Palette palette) : ProfessionalColorTable
{
    private Color Menu => palette.IsDark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
    private Color Hover => palette.IsDark ? Color.FromArgb(61, 61, 61) : Color.FromArgb(232, 232, 232);
    private Color Separator => palette.IsDark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(220, 220, 220);

    public override Color ToolStripDropDownBackground => Menu;
    public override Color ImageMarginGradientBegin => Menu;
    public override Color ImageMarginGradientMiddle => Menu;
    public override Color ImageMarginGradientEnd => Menu;
    public override Color MenuBorder => palette.CardBorder;
    public override Color MenuItemBorder => Hover;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Hover;
    public override Color MenuItemPressedGradientMiddle => Hover;
    public override Color MenuItemPressedGradientEnd => Hover;
    public override Color CheckBackground => Menu;
    public override Color CheckSelectedBackground => Hover;
    public override Color CheckPressedBackground => Hover;
    public override Color SeparatorDark => Separator;
    public override Color SeparatorLight => Separator;
}
