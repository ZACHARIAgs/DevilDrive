using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Jarvis.SwAddin;

/// <summary>Colors, fonts and sizes for the task pane. Pixel sizes go through <see cref="Px"/> for DPI.</summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(0xF6, 0xF7, 0xF9);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(0xE1, 0xE4, 0xE9);
    public static readonly Color Text = Color.FromArgb(0x1F, 0x29, 0x37);
    public static readonly Color Muted = Color.FromArgb(0x6B, 0x72, 0x80);
    public static readonly Color Accent = Color.FromArgb(0x25, 0x63, 0xEB);
    public static readonly Color AccentHover = Color.FromArgb(0x1D, 0x4E, 0xD8);
    public static readonly Color AccentPressed = Color.FromArgb(0x1E, 0x40, 0xAF);
    public static readonly Color SecondaryHover = Color.FromArgb(0xF1, 0xF3, 0xF6);
    public static readonly Color SecondaryPressed = Color.FromArgb(0xE5, 0xE7, 0xEB);
    public static readonly Color DisabledBack = Color.FromArgb(0xF3, 0xF4, 0xF6);
    public static readonly Color DisabledText = Color.FromArgb(0xA0, 0xA6, 0xB0);

    public static readonly Font Body = new Font("Segoe UI", 9f);
    public static readonly Font Small = new Font("Segoe UI", 8.25f);
    public static readonly Font Strong = new Font("Segoe UI Semibold", 9.5f);
    public static readonly Font Title = new Font("Segoe UI Semibold", 13f);
    public static readonly Font Glyph = new Font("Segoe MDL2 Assets", 10f);
    public static readonly Font SmallGlyph = new Font("Segoe MDL2 Assets", 7.5f);
    public static readonly Font SmallBold = new Font("Segoe UI Semibold", 8.25f);

    private static float _scale = 1f;

    public static void Init(float dpi) => _scale = dpi > 0 ? dpi / 96f : 1f;

    public static int Px(int value) => (int)Math.Round(value * _scale);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static int TextHeight(string text, Font font, int width) =>
        string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter).Height;
}

/// <summary>A control whose height follows its width (wrapped text).</summary>
internal interface IFitWidth
{
    void FitTo(int width);
}

/// <summary>Base for owner-drawn pane controls: double-buffered, painted on the pane background.</summary>
internal abstract class PaneControl : Control
{
    protected PaneControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Background;
    }

    protected static Graphics Prepare(PaintEventArgs e, Color background)
    {
        e.Graphics.Clear(background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        return e.Graphics;
    }
}

/// <summary>Rounded, flat button with an optional Segoe MDL2 glyph. Primary buttons are filled with the accent color.</summary>
internal sealed class ModernButton : PaneControl
{
    private bool _hover;
    private bool _pressed;
    private bool _primary;

    public ModernButton(string text, string glyph)
    {
        Text = text;
        Glyph = glyph;
        Height = Theme.Px(34);
        Margin = new Padding(0, Theme.Px(3), 0, Theme.Px(3));
        Cursor = Cursors.Hand;
        Font = Theme.Strong;
    }

    public string Glyph { get; }

    public bool Primary
    {
        get => _primary;
        set
        {
            _primary = value;
            Invalidate();
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e, BackColor);
        Color back, fore, border;
        if (!Enabled)
        {
            back = Theme.DisabledBack;
            fore = Theme.DisabledText;
            border = Theme.Border;
        }
        else if (Primary)
        {
            back = _pressed ? Theme.AccentPressed : _hover ? Theme.AccentHover : Theme.Accent;
            fore = Color.White;
            border = back;
        }
        else
        {
            back = _pressed ? Theme.SecondaryPressed : _hover ? Theme.SecondaryHover : Theme.Card;
            fore = Theme.Text;
            border = Theme.Border;
        }

        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.RoundedRect(rect, Theme.Px(7)))
        using (var fill = new SolidBrush(back))
        using (var pen = new Pen(border))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var glyphSize = Glyph.Length > 0 ? TextRenderer.MeasureText(g, Glyph, Theme.Glyph, Size.Empty, TextFormatFlags.NoPadding) : Size.Empty;
        var gap = Glyph.Length > 0 ? Theme.Px(8) : 0;
        var x = (Width - (glyphSize.Width + gap + textSize.Width)) / 2;
        if (Glyph.Length > 0)
        {
            TextRenderer.DrawText(g, Glyph, Theme.Glyph, new Rectangle(x, 0, glyphSize.Width, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        TextRenderer.DrawText(g, Text, Font, new Rectangle(x + glyphSize.Width + gap, 0, textSize.Width + 2, Height), fore, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Centered logo, title and subtitle.</summary>
internal sealed class PaneHeader : PaneControl, IFitWidth
{
    private readonly Image _logo;
    private string _subtitle = "";

    public PaneHeader(Image logo, string title)
    {
        _logo = logo;
        Text = title;
        Margin = new Padding(0, Theme.Px(4), 0, Theme.Px(10));
    }

    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (_subtitle == value) return;
            _subtitle = value;
            FitTo(Width);
            Invalidate();
        }
    }

    public void FitTo(int width)
    {
        Width = width;
        Height = _logo.Height + Theme.Px(6) + Theme.TextHeight(Text, Theme.Title, width) + Theme.TextHeight(Subtitle, Theme.Small, width);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e, BackColor);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(_logo, (Width - _logo.Width) / 2, 0, _logo.Width, _logo.Height);

        var y = _logo.Height + Theme.Px(6);
        var titleHeight = Theme.TextHeight(Text, Theme.Title, Width);
        TextRenderer.DrawText(g, Text, Theme.Title, new Rectangle(0, y, Width, titleHeight), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, Subtitle, Theme.Small, new Rectangle(0, y + titleHeight, Width, Height - y - titleHeight), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }
}

/// <summary>
/// The vault the pane works in, as a rounded drop-down ("[vault icon] Capstone Team 13  v").
/// Clicking it raises <see cref="Opening"/> so the pane can show its list of vaults.
/// </summary>
internal sealed class VaultPicker : PaneControl, IFitWidth
{
    private bool _hover;

    public VaultPicker()
    {
        Height = Theme.Px(36);
        Margin = new Padding(0, 0, 0, Theme.Px(10));
        Cursor = Cursors.Hand;
        Font = Theme.Strong;
    }

    /// <summary>Raised on click, with the point below the control to show a menu at.</summary>
    public event Action<Point>? Opening;

    public void FitTo(int width) => Width = width;

    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) Opening?.Invoke(new Point(0, Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e, BackColor);
        var box = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.RoundedRect(box, Theme.Px(6)))
        using (var fill = new SolidBrush(_hover ? Theme.SecondaryHover : Theme.Card))
        using (var border = new Pen(Theme.Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
        }
        var pad = Theme.Px(10);
        var glyphWidth = Theme.Px(20);
        TextRenderer.DrawText(g, "", Theme.Glyph, new Rectangle(pad, 0, glyphWidth, Height), Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        TextRenderer.DrawText(g, "", Theme.SmallGlyph, new Rectangle(Width - pad - glyphWidth, 0, glyphWidth, Height), Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        var textLeft = pad + glyphWidth + Theme.Px(4);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(textLeft, 0, Width - textLeft - pad - glyphWidth, Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}

internal enum NoticeKind
{
    Info,
    Warning,
    Error,
}

/// <summary>Rounded, tinted message strip (offline, pending check-ins, updates available).</summary>
internal sealed class NoticeBox : PaneControl, IFitWidth
{
    private NoticeKind _kind;

    public NoticeBox()
    {
        Margin = new Padding(0, 0, 0, Theme.Px(10));
        Visible = false;
    }

    public void Show(string text, NoticeKind kind)
    {
        Text = text;
        _kind = kind;
        Visible = text.Length > 0;
        FitTo(Width);
        Invalidate();
    }

    public void FitTo(int width)
    {
        Width = width;
        Height = Theme.TextHeight(Text, Theme.Small, width - Theme.Px(20)) + Theme.Px(16);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e, BackColor);
        Color back, fore;
        switch (_kind)
        {
            case NoticeKind.Error:
                back = Color.FromArgb(0xFD, 0xEC, 0xEC);
                fore = Color.FromArgb(0x99, 0x1B, 0x1B);
                break;
            case NoticeKind.Warning:
                back = Color.FromArgb(0xFF, 0xF4, 0xE5);
                fore = Color.FromArgb(0x92, 0x40, 0x0E);
                break;
            default:
                back = Color.FromArgb(0xE8, 0xF0, 0xFE);
                fore = Color.FromArgb(0x1E, 0x40, 0xAF);
                break;
        }
        using (var path = Theme.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), Theme.Px(7)))
        using (var fill = new SolidBrush(back))
        {
            g.FillPath(fill, path);
        }
        var pad = Theme.Px(10);
        TextRenderer.DrawText(g, Text, Theme.Small, new Rectangle(pad, Theme.Px(8), Width - 2 * pad, Height - Theme.Px(16)), fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }
}

/// <summary>The active document: name, a colored status badge, and details. Everything centered.</summary>
internal sealed class FileCard : PaneControl, IFitWidth
{
    private string _fileName = "";
    private string _badge = "";
    private string _details = "";
    private Color _badgeBack = Color.FromArgb(0xEE, 0xF0, 0xF3);
    private Color _badgeFore = Color.FromArgb(0x4B, 0x55, 0x63);

    public FileCard()
    {
        Margin = new Padding(0, 0, 0, Theme.Px(12));
    }

    public void Show(string fileName, string badge, Color badgeBack, Color badgeFore, string details)
    {
        _fileName = fileName;
        _badge = badge;
        _badgeBack = badgeBack;
        _badgeFore = badgeFore;
        _details = details;
        FitTo(Width);
        Invalidate();
    }

    private int Pad => Theme.Px(14);
    private int Inner => Math.Max(1, Width - 2 * Pad);
    private int BadgeHeight => TextRenderer.MeasureText("Ag", Theme.Strong).Height + Theme.Px(8);

    public void FitTo(int width)
    {
        Width = width;
        var height = Pad + Theme.TextHeight(_fileName, Theme.Strong, Inner) + Theme.Px(10) + BadgeHeight;
        if (_details.Length > 0) height += Theme.Px(10) + Theme.TextHeight(_details, Theme.Small, Inner);
        Height = height + Pad;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e, BackColor);
        using (var path = Theme.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.Px(10)))
        using (var fill = new SolidBrush(Theme.Card))
        using (var pen = new Pen(Theme.Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        var y = Pad;
        var nameHeight = Theme.TextHeight(_fileName, Theme.Strong, Inner);
        TextRenderer.DrawText(g, _fileName, Theme.Strong, new Rectangle(Pad, y, Inner, nameHeight), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        y += nameHeight + Theme.Px(10);

        // Pill-shaped status badge.
        var badgeWidth = Math.Min(Inner, TextRenderer.MeasureText(_badge, Theme.Strong).Width + Theme.Px(16));
        var badgeRect = new RectangleF((Width - badgeWidth) / 2f, y, badgeWidth, BadgeHeight);
        using (var path = Theme.RoundedRect(badgeRect, BadgeHeight / 2f))
        using (var fill = new SolidBrush(_badgeBack))
        {
            g.FillPath(fill, path);
        }
        TextRenderer.DrawText(g, _badge, Theme.Strong, Rectangle.Round(badgeRect), _badgeFore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        y += BadgeHeight;

        if (_details.Length > 0)
        {
            y += Theme.Px(10);
            TextRenderer.DrawText(g, _details, Theme.Small, new Rectangle(Pad, y, Inner, Height - y), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        }
    }
}

/// <summary>Small centered muted text (status line, section captions).</summary>
internal sealed class CaptionLabel : PaneControl, IFitWidth
{
    public CaptionLabel(string text = "")
    {
        Text = text;
        Margin = new Padding(0, Theme.Px(8), 0, Theme.Px(4));
    }

    protected override void OnTextChanged(EventArgs e)
    {
        FitTo(Width);
        Invalidate();
        base.OnTextChanged(e);
    }

    public void FitTo(int width)
    {
        Width = width;
        Height = Math.Max(Theme.Px(4), Theme.TextHeight(Text, Theme.Small, width));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e, BackColor);
        TextRenderer.DrawText(g, Text, Theme.Small, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }
}

/// <summary>
/// Shows that Jarvis is working: a sliding accent bar with a line of text ("Checking the
/// vault..."), so a slow vault or a big assembly never looks frozen. Hidden when idle.
/// </summary>
internal sealed class ActivityBar : PaneControl, IFitWidth
{
    private readonly Timer _animation = new Timer { Interval = 30 };
    private float _position;
    private string _text = "";

    public ActivityBar()
    {
        Margin = new Padding(0, Theme.Px(2), 0, Theme.Px(6));
        Height = Theme.Px(30);
        Visible = false;
        _animation.Tick += (_, _) =>
        {
            _position = (_position + 0.018f) % 1.4f;
            Invalidate();
        };
    }

    public bool IsActive => Visible;

    public void Start(string text)
    {
        _text = text;
        if (!Visible)
        {
            _position = 0;
            Visible = true;
            _animation.Start();
        }
        Invalidate();
    }

    public void SetText(string text)
    {
        if (!Visible || text == _text) return;
        _text = text;
        Invalidate();
    }

    public void Stop()
    {
        _animation.Stop();
        Visible = false;
    }

    public void FitTo(int width) => Width = width;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = Prepare(e, BackColor);
        var track = new Rectangle(0, 0, Width, Theme.Px(4));
        using (var trackBrush = new SolidBrush(Theme.Border)) g.FillRectangle(trackBrush, track);
        // A segment a third of the width slides across and wraps around.
        var segment = Width / 3f;
        var x = (_position - 0.4f) * Width;
        using (var accent = new SolidBrush(Theme.Accent)) g.FillRectangle(accent, x, 0, segment, track.Height);
        TextRenderer.DrawText(g, _text, Theme.Small, new Rectangle(0, Theme.Px(7), Width, Height - Theme.Px(7)), Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _animation.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Thin horizontal rule between button groups.</summary>
internal sealed class Divider : PaneControl, IFitWidth
{
    public Divider()
    {
        Height = Theme.Px(1);
        Margin = new Padding(0, Theme.Px(10), 0, Theme.Px(6));
        BackColor = Theme.Border;
    }

    public void FitTo(int width) => Width = width;

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(Theme.Border);
}
