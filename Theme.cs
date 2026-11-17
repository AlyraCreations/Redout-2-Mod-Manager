using System.ComponentModel;

namespace R2MM;

internal sealed class BufferedListView : ListView
{
    private const int WmPaint = 0x000F;
    private int _insertionIndex = -1;

    public BufferedListView()
    {
        DoubleBuffered = true;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int InsertionIndex
    {
        get => _insertionIndex;
        set
        {
            if (_insertionIndex == value)
            {
                return;
            }
            _insertionIndex = value;
            Invalidate();
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WmPaint || _insertionIndex < 0 || Items.Count == 0)
        {
            return;
        }

        var y = _insertionIndex < Items.Count
            ? Items[_insertionIndex].Bounds.Top
            : Items[^1].Bounds.Bottom;
        if (y < 0 || y > ClientSize.Height)
        {
            return;
        }
        using var graphics = Graphics.FromHwnd(Handle);
        using var pen = new Pen(Theme.Highlight, 3);
        graphics.DrawLine(pen, 0, y, ClientSize.Width, y);
    }
}

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(8, 10, 17);
    public static readonly Color Panel = Color.FromArgb(18, 21, 34);
    public static readonly Color Button = Color.FromArgb(28, 34, 55);
    public static readonly Color Text = Color.FromArgb(244, 246, 255);
    public static readonly Color Muted = Color.FromArgb(158, 174, 211);
    public static readonly Color Accent = Color.FromArgb(64, 84, 178);
    public static readonly Color Border = Color.FromArgb(58, 74, 135);
    public static readonly Color ButtonHover = Color.FromArgb(43, 53, 91);
    public static readonly Color AccentHover = Color.FromArgb(83, 105, 203);
    public static readonly Color Highlight = Color.FromArgb(255, 221, 64); // conflicts, unconfirmed changes, drop line

    public const int BackgroundDim = 185;
    public const string BackgroundResource = "Redout2ModManager.Assets.background.jpg";

    public static Font CreateFont(float size, FontStyle style = FontStyle.Regular) =>
        new("Segoe UI", size, style);

    public static void DrawColumnHeader(DrawListViewColumnHeaderEventArgs e, Font font)
    {
        using var background = new SolidBrush(Button);
        e.Graphics.FillRectangle(background, e.Bounds);
        var textBounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            e.Header?.Text ?? "",
            font,
            textBounds,
            Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var border = new Pen(Border);
        e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }
}
