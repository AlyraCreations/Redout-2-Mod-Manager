using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace R2MM;

/// <summary>How a button looks: normal, the main action of a window, or normal with a highlighted border (pending changes, updates).</summary>
internal enum ButtonKind { Normal, Primary, Attention }

internal sealed class BufferedListView : ListView
{
    private const int WmPaint = 0x000F;
    private int _dropLineIndex = -1;

    public BufferedListView() => DoubleBuffered = true;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int DropLineIndex
    {
        get => _dropLineIndex;
        set
        {
            if (_dropLineIndex == value) return;
            _dropLineIndex = value;
            Invalidate();
        }
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg != WmPaint || _dropLineIndex < 0 || Items.Count == 0) return;

        var dropLineY = _dropLineIndex < Items.Count ? Items[_dropLineIndex].Bounds.Top : Items[^1].Bounds.Bottom;
        if (dropLineY < 0 || dropLineY > ClientSize.Height) return;
        using var graphics = Graphics.FromHwnd(Handle);
        using var pen = new Pen(Theme.HighlightColor, 3);
        graphics.DrawLine(pen, 0, dropLineY, ClientSize.Width, dropLineY);
    }
}

internal static class Theme
{
    public static readonly Color BackgroundColor = Color.FromArgb(8, 10, 17);
    public static readonly Color PanelColor = Color.FromArgb(18, 21, 34);
    public static readonly Color ButtonColor = Color.FromArgb(28, 34, 55);
    public static readonly Color ButtonHoverColor = Color.FromArgb(43, 53, 91);
    public static readonly Color ButtonPressedColor = Color.FromArgb(54, 66, 112);
    public static readonly Color PanelHoverColor = Color.FromArgb(25, 29, 47);
    public static readonly Color DividerColor = Color.FromArgb(36, 44, 78);
    public static readonly Color TextColor = Color.FromArgb(244, 246, 255);
    public static readonly Color MutedColor = Color.FromArgb(158, 174, 211);
    public static readonly Color AccentColor = Color.FromArgb(64, 84, 178);
    public static readonly Color AccentHoverColor = Color.FromArgb(83, 105, 205);
    public static readonly Color AccentPressedColor = Color.FromArgb(50, 68, 150);
    public static readonly Color BorderColor = Color.FromArgb(58, 74, 135);
    public static readonly Color LinkColor = Color.FromArgb(124, 146, 240);
    public static readonly Color HighlightColor = Color.FromArgb(255, 221, 64); // conflicts, unconfirmed changes, drop line
    public static readonly Color SuccessColor = Color.FromArgb(104, 205, 150); // installed / up to date

    public const int WallpaperDim = 185;

    public static Font CreateFont(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style);

    public static Button CreateButton(string text, Action? onClick = null, int width = 150, ButtonKind kind = ButtonKind.Normal)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(width, 32),
            ForeColor = TextColor,
            FlatStyle = FlatStyle.Flat,
            UseMnemonic = false,
            Cursor = Cursors.Hand
        };
        StyleButton(button, kind);
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Applies the colours of a button kind, including its hover and pressed states. Can be called again whenever the state changes.</summary>
    public static void StyleButton(Button button, ButtonKind kind)
    {
        var (back, hover, pressed, border, borderSize) = kind switch
        {
            ButtonKind.Primary => (AccentColor, AccentHoverColor, AccentPressedColor, AccentColor, 1),
            ButtonKind.Attention => (ButtonColor, ButtonHoverColor, ButtonPressedColor, HighlightColor, 2),
            _ => (ButtonColor, ButtonHoverColor, ButtonPressedColor, BorderColor, 1)
        };
        button.UseVisualStyleBackColor = false;
        button.BackColor = back;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = pressed;
        button.FlatAppearance.BorderColor = border;
        button.FlatAppearance.BorderSize = borderSize;
    }

    /// <summary>A footer for dialogs: a divider on top and the buttons centred as a group.</summary>
    public static Panel CreateButtonBar(params Button[] buttons)
    {
        const int gap = 12;
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 64, BackColor = BackgroundColor };
        bar.Controls.AddRange(buttons);
        void Arrange()
        {
            var total = buttons.Sum(button => button.Width) + gap * (buttons.Length - 1);
            var x = Math.Max(0, (bar.ClientSize.Width - total) / 2);
            foreach (var button in buttons)
            {
                button.Location = new Point(x, (bar.ClientSize.Height - button.Height) / 2 + 1);
                x += button.Width + gap;
            }
        }
        bar.Resize += (_, _) => Arrange();
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(DividerColor);
            e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
        };
        Arrange();
        return bar;
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string subAppName, string? subIdList);

    /// <summary>Gives a control's scrollbars the dark look (Windows 10 1809 and later). Does nothing where unsupported.</summary>
    public static void UseDarkScrollbars(Control control)
    {
        void Apply()
        {
            try { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
        control.HandleCreated += (_, _) => Apply();
        if (control.IsHandleCreated) Apply();
    }

    public static Panel CreateGap(DockStyle dock, Color? color = null) =>
        new() { Dock = dock, Height = 10, BackColor = color ?? Color.Transparent };

    /// <summary>A bordered panel that hosts a borderless single-line text box, vertically centred.</summary>
    public static Panel CreateTextFieldHost(TextBox box, DockStyle dock = DockStyle.None)
    {
        box.AutoSize = false;
        box.BorderStyle = BorderStyle.None;
        box.BackColor = PanelColor;
        box.ForeColor = TextColor;
        var host = new Panel
        {
            Dock = dock,
            Height = 32,
            BackColor = PanelColor,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8, 0, 8, 0),
            Controls = { box }
        };
        void CenterBox()
        {
            var height = Math.Min(box.PreferredHeight, host.ClientSize.Height);
            box.SetBounds(host.Padding.Left, Math.Max(0, (host.ClientSize.Height - height) / 2),
                Math.Max(0, host.ClientSize.Width - host.Padding.Horizontal), height);
        }
        host.Layout += (_, _) => CenterBox();
        host.Resize += (_, _) => CenterBox();
        return host;
    }

    /// <summary>
    /// Keeps the columns of a details list usable. Every column keeps a minimum width, the last column always fills
    /// the remaining space (so no empty header strip can appear) and cannot be dragged itself, and no column can be dragged
    /// so wide that the last one is pushed below its minimum. Returns the action that re-fills the last column; call it
    /// when the list is resized.
    /// </summary>
    public static Action ConstrainColumns(ListView list, params int[] minimumWidths)
    {
        var filling = false;
        void FillLastColumn()
        {
            if (filling || list.Columns.Count < 2) return;
            filling = true;
            try
            {
                var last = list.Columns.Count - 1;
                var width = Math.Max(minimumWidths[last], list.ClientSize.Width - list.Columns.Cast<ColumnHeader>().Take(last).Sum(column => column.Width));
                if (list.Columns[last].Width != width) list.Columns[last].Width = width;
            }
            finally { filling = false; }
        }

        list.ColumnWidthChanging += (_, e) =>
        {
            // Only dragging by the user is limited: before the list is shown its columns are still being set up.
            if (filling || !list.Visible || list.ClientSize.Width <= 0) return;
            var last = list.Columns.Count - 1;
            if (e.ColumnIndex == last)
            {
                e.Cancel = true;
                return;
            }
            var others = list.Columns.Cast<ColumnHeader>().Take(last).Where((column, index) => index != e.ColumnIndex).Sum(column => column.Width);
            var maximum = Math.Max(minimumWidths[e.ColumnIndex], list.ClientSize.Width - minimumWidths[last] - others);
            e.NewWidth = Math.Clamp(e.NewWidth, minimumWidths[e.ColumnIndex], maximum);
        };
        list.ColumnWidthChanged += (_, _) => FillLastColumn();
        return FillLastColumn;
    }

    public static void StyleList(ListView list)
    {
        list.View = View.Details;
        list.FullRowSelect = true;
        list.HideSelection = false;
        list.BackColor = PanelColor;
        list.ForeColor = TextColor;
        list.BorderStyle = BorderStyle.FixedSingle;
        list.Dock = DockStyle.Fill;
        list.OwnerDraw = true;
        list.DrawColumnHeader += (_, e) => DrawColumnHeader(e, list.Font);
        list.DrawItem += (_, e) => e.DrawDefault = true;
        list.DrawSubItem += (_, e) => e.DrawDefault = true;
        UseDarkScrollbars(list);
    }

    private static void DrawColumnHeader(DrawListViewColumnHeaderEventArgs e, Font font)
    {
        using var background = new SolidBrush(ButtonColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", font,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height), TextColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var border = new Pen(BorderColor);
        e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
    }
}

/// <summary>
/// The common look of the borderless dialogs: a 1 px border, a centred title and (through <see cref="Compose"/>) a footer with centred buttons.
/// </summary>
internal abstract class ThemedDialog : Form
{
    protected const int TitleHeight = 56, FooterHeight = 64, BorderWidth = 1;

    protected ThemedDialog(string title, Size size)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Size = size;
        BackColor = Theme.BackgroundColor;
        ForeColor = Theme.TextColor;
        Font = Theme.CreateFont(9);
        Padding = new Padding(BorderWidth);
        DoubleBuffered = true;
    }

    /// <summary>Puts the title on top, the buttons at the bottom and the body in between.</summary>
    protected void Compose(Control body, params Button[] footerButtons)
    {
        var title = new Label
        {
            Text = Text.ToUpperInvariant(),
            Dock = DockStyle.Top,
            Height = TitleHeight,
            Font = Theme.CreateFont(14, FontStyle.Bold),
            ForeColor = Theme.TextColor,
            TextAlign = ContentAlignment.MiddleCenter,
            UseMnemonic = false
        };
        title.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.DividerColor);
            e.Graphics.DrawLine(pen, 0, title.Height - 1, title.Width, title.Height - 1);
        };
        body.Dock = DockStyle.Fill;
        // Docked controls are laid out in reverse order of being added, so the body (Fill) goes first.
        Controls.Add(body);
        Controls.Add(Theme.CreateButtonBar(footerButtons));
        Controls.Add(title);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.BorderColor);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>
/// A dialog that is shown next to the main window instead of blocking it, and closes itself as soon as the user clicks
/// somewhere else in the app. Switching to another program leaves it open, and so do windows it opens itself (a file picker, a message).
/// </summary>
internal abstract class PopupForm : ThemedDialog
{
    private const int WmActivate = 0x0006, WaInactive = 0;
    private static string s_dismissedName = "";
    private static long s_dismissedAt;
    private static Point s_dismissedCursor;
    private bool _closing;

    protected PopupForm(string title, Size size) : base(title, size) { }

    /// <summary>
    /// True when the press on <paramref name="opener"/> is the very click that dismissed the popup with this title, so the button closes the popup
    /// instead of reopening it. A press that merely follows a dismissal elsewhere is not affected, and each dismissal is only used once.
    /// </summary>
    public static bool WasJustDismissed(string title, Control opener)
    {
        var recent = s_dismissedName == title && Environment.TickCount64 - s_dismissedAt < 400;
        s_dismissedName = "";
        return recent && opener.RectangleToScreen(opener.ClientRectangle).Contains(s_dismissedCursor);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // WM_ACTIVATE with WA_INACTIVE: this window loses activation and lParam is the window that gets it.
        if (m.Msg == WmActivate && (m.WParam.ToInt64() & 0xFFFF) == WaInactive && m.LParam != IntPtr.Zero && !_closing
            && FromHandle(m.LParam) is Form next && !IsSelfOrOwnedByThis(next))
        {
            _closing = true;
            s_dismissedName = Text;
            s_dismissedAt = Environment.TickCount64;
            s_dismissedCursor = Cursor.Position;
            BeginInvoke(new Action(() => { if (!IsDisposed) Close(); })); // not inside the activation message itself
        }
    }

    private bool IsSelfOrOwnedByThis(Form form)
    {
        for (Form? current = form; current is not null; current = current.Owner)
            if (ReferenceEquals(current, this)) return true;
        return false;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true; // the activation change caused by closing must not count as a dismissal
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData != Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        Close();
        return true;
    }
}

/// <summary>A small popup with a column of centred buttons (used for Options and Links).</summary>
internal sealed class ManagerMenuDialog : PopupForm
{
    private const int ButtonWidth = 180, ButtonGap = 10, ItemsPadding = 16;
    private readonly Panel _items = new() { BackColor = Theme.BackgroundColor };

    public ManagerMenuDialog(string title) : base(title, new Size(260, 240))
    {
        var closeButton = Theme.CreateButton("Close", Close, 110);
        _items.Resize += (_, _) => LayoutItems();
        Compose(_items, closeButton);
        UpdateHeight();
    }

    public Button AddButton(string text, Action onClick)
    {
        var button = Theme.CreateButton(text, onClick, ButtonWidth);
        _items.Controls.Add(button);
        UpdateHeight();
        LayoutItems();
        return button;
    }

    public void AddToggle(string text, bool isOn, Action<bool> onChanged)
    {
        string Caption() => $"{text}: {(isOn ? "On" : "Off")}";
        var toggle = AddButton(Caption(), () => { });
        toggle.Click += (_, _) =>
        {
            isOn = !isOn;
            toggle.Text = Caption();
            onChanged(isOn);
        };
    }

    /// <summary>Sizes the window to its buttons, so there is no empty space below them.</summary>
    private void UpdateHeight()
    {
        var count = _items.Controls.Count;
        var itemsHeight = ItemsPadding * 2 + count * 32 + Math.Max(0, count - 1) * ButtonGap;
        ClientSize = new Size(ClientSize.Width, BorderWidth * 2 + TitleHeight + itemsHeight + FooterHeight);
    }

    /// <summary>Stacks the buttons and centres each of them horizontally.</summary>
    private void LayoutItems()
    {
        var y = ItemsPadding;
        foreach (Control button in _items.Controls)
        {
            button.Location = new Point((_items.ClientSize.Width - button.Width) / 2, y);
            y += button.Height + ButtonGap;
        }
    }
}

/// <summary>Asks for the personal Nexus Mods API key. "Save" returns OK; the second button returns Cancel.</summary>
internal sealed class NexusKeyDialog : ThemedDialog
{
    private readonly TextBox _keyBox = new() { PlaceholderText = "Paste your personal API key" };

    public string ApiKey => _keyBox.Text.Trim();

    public NexusKeyDialog(string currentKey, string message, string cancelText, Action openKeyPage) : base("Nexus Mods API key", new Size(470, 334))
    {
        _keyBox.Text = currentKey;

        const int pad = 24, width = 420;
        var link = new LinkLabel
        {
            Text = "Open the Nexus Mods API key page", Location = new Point(pad, 136), Size = new Size(width, 20),
            LinkColor = Theme.LinkColor, VisitedLinkColor = Theme.LinkColor, ActiveLinkColor = Theme.TextColor, LinkBehavior = LinkBehavior.HoverUnderline
        };
        link.LinkClicked += (_, _) => openKeyPage();
        var keyHost = Theme.CreateTextFieldHost(_keyBox);
        keyHost.SetBounds(pad, 164, width, 32);

        var saveButton = Theme.CreateButton("Save", null, 140, ButtonKind.Primary);
        saveButton.DialogResult = DialogResult.OK;
        var cancelButton = Theme.CreateButton(cancelText, null, 150);
        cancelButton.DialogResult = DialogResult.Cancel;
        AcceptButton = saveButton;
        CancelButton = cancelButton;

        Compose(new Panel
        {
            BackColor = Theme.BackgroundColor,
            Controls =
            {
                new Label { Text = message, ForeColor = Theme.MutedColor, Location = new Point(pad, 12), Size = new Size(width, 120), UseMnemonic = false },
                link, keyHost
            }
        }, saveButton, cancelButton);
    }
}

internal sealed record ConflictRow(string Asset, string Winner, string Overridden);

/// <summary>Lists the files that several mods write to. Like Options and Links it closes when the user clicks elsewhere in the app.</summary>
internal sealed class ConflictsDialog : PopupForm
{
    public const string Title = "Mod conflicts";

    public ConflictsDialog(IReadOnlyList<ConflictRow> rows, IReadOnlyList<string> unreadableMods) : base(Title, new Size(920, 560))
    {
        var summary = new Label
        {
            Text = (rows.Count == 0 ? "No conflicts." : $"{rows.Count} file(s) write to the same address.") +
                   (unreadableMods.Count > 0 ? $"\r\nNot checked (no readable file index or .pak.list file): {string.Join(", ", unreadableMods)}." : ""),
            Dock = DockStyle.Top,
            Height = 48,
            ForeColor = Theme.MutedColor,
            UseMnemonic = false
        };

        var list = new BufferedListView();
        Theme.StyleList(list);
        foreach (var (name, width) in new[] { ("Conflicting file(s)", 420), ("Overrides", 200), ("Overridden", 240) }) list.Columns.Add(name, width);
        var fillLastColumn = Theme.ConstrainColumns(list, 160, 100, 140);
        list.Resize += (_, _) => fillLastColumn();
        Shown += (_, _) => fillLastColumn();

        var searchBox = new TextBox { PlaceholderText = "Search conflicting files or mods..." };
        void ApplyFilter()
        {
            var query = searchBox.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var row in rows.Where(row => new[] { row.Asset, row.Winner, row.Overridden }.Any(text => text.Contains(query, StringComparison.OrdinalIgnoreCase))))
                list.Items.Add(new ListViewItem(new[] { row.Asset, row.Winner, row.Overridden }));
            list.EndUpdate();
        }
        searchBox.TextChanged += (_, _) => ApplyFilter();
        ApplyFilter();

        // Docked controls are laid out in reverse order of being added: the list (Fill) goes first, the summary ends up on top.
        Compose(new Panel
        {
            BackColor = Theme.BackgroundColor,
            Padding = new Padding(24, 12, 24, 16),
            Controls = { list, Theme.CreateGap(DockStyle.Top, Theme.BackgroundColor), Theme.CreateTextFieldHost(searchBox, DockStyle.Top), summary }
        }, Theme.CreateButton("Close", Close, 110));
    }
}

// The main window: layout, wallpaper, window bounds, status console, popups and shell helpers.
internal sealed partial class MainForm
{
    public MainForm()
    {
        var version = typeof(MainForm).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? throw new InvalidOperationException("Application version metadata is missing.");
        Text = $"R2MM v{version}";
        using (var iconStream = OpenAsset("app.ico")) Icon = iconStream is null ? null : new Icon(iconStream);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = Size = new Size(960, 680);
        RestoreWindowBounds();
        BackColor = Theme.BackgroundColor;
        ForeColor = Theme.TextColor;
        Font = Theme.CreateFont(9);
        DoubleBuffered = true;
        ResizeRedraw = true;
        using (var wallpaperStream = OpenAsset("background.jpg")) _wallpaperImage = wallpaperStream is null ? null : LoadWallpaper(wallpaperStream);

        // Mod list
        Theme.StyleList(_modList);
        _modList.CheckBoxes = true;
        _modList.ShowItemToolTips = true;
        foreach (var (name, width) in new[] { ("Mod list", 300), ("#", 44), ("Status", 120), ("Conflicts", 300) }) _modList.Columns.Add(name, width);
        _modListResizeTimer.Tick += (_, _) =>
        {
            _modListResizeTimer.Stop();
            _fillModListColumn();
        };
        _modList.Resize += (_, _) =>
        {
            _modListResizeTimer.Stop();
            _modListResizeTimer.Start();
        };
        _fillModListColumn = Theme.ConstrainColumns(_modList, 160, 36, 80, 140);
        _modList.ItemDrag += (_, e) => { if (_modSearchBox.Text.Trim() == "") BeginModDrag(e); }; // reordering a filtered list would be ambiguous
        _modList.DragOver += HandleModDragOver;
        _modList.DragDrop += HandleModDrop;
        _modList.DragLeave += (_, _) => _modList.DropLineIndex = -1;
        _modList.ItemCheck += (_, e) =>
        {
            if (_modListUpdating) return;
            if (e.Index >= 0 && _modList.Items[e.Index].Tag is string fileName) _modDesiredStates[fileName] = e.NewValue == CheckState.Checked;
            if (IsHandleCreated) BeginInvoke(new Action(UpdateSelectionButtons)); // ItemCheck fires before Checked changes
        };
        _modList.SelectedIndexChanged += (_, _) => UpdateSelectionButtons();

        _modSearchBox.PlaceholderText = "Search mods...";
        _modSearchBox.TextChanged += (_, _) => RefreshModList(preserveDesiredStates: true);

        // Sidebar
        _changesConfirmButton = Theme.CreateButton("Confirm change(s)", ConfirmChanges);
        _changesRevertButton = Theme.CreateButton("Revert change(s)", RevertChanges);
        _modUninstallButton = Theme.CreateButton("Uninstall selected", UninstallSelectedMods);
        _conflictViewButton = CreatePopupButton("View conflicts", ConflictsDialog.Title, ShowConflicts);
        _changesConfirmButton.Enabled = _changesRevertButton.Enabled = false;
        var launchButton = Theme.CreateButton("Launch game", () => OpenShell(SteamLaunchUri, "Could not launch Redout 2 through Steam."), kind: ButtonKind.Primary);
        _browserButton = Theme.CreateButton("Mod browser", OpenModBrowser);
        var sidebar = new Panel { Dock = DockStyle.Right, Width = 190, BackColor = Color.Transparent };
        Button[] sidebarButtons =
        [
            _changesConfirmButton, _changesRevertButton, Theme.CreateButton("Toggle all", ToggleAllMods), _modUninstallButton,
            Theme.CreateButton("Refresh mod list", RefreshMods), _conflictViewButton,
            CreatePopupButton("Options", "Options", ShowOptions), CreatePopupButton("Links", "Links", ShowLinks), _browserButton, launchButton
        ];
        for (var index = 0; index < sidebarButtons.Length; index++)
        {
            sidebarButtons[index].Location = new Point(16, index * 38); // tight enough for ten buttons at the minimum window height
            sidebar.Controls.Add(sidebarButtons[index]);
        }

        // Header
        Label HeaderLabel(string text, int top, int height = 22, bool ellipsis = true) => new()
        {
            Text = text, ForeColor = Theme.MutedColor, Location = new Point(24, top), Size = new Size(680, height), AutoEllipsis = ellipsis
        };
        var title = HeaderLabel($"REDOUT 2 MOD MANAGER v{version}", 16, 36);
        title.Font = Theme.CreateFont(18, FontStyle.Bold);
        title.ForeColor = Theme.TextColor;
        const string discordPrefix = "Questions, feedback, or suggestions? Join the official Discord: ";
        const string discordText = "discord.gg/redout";
        var discordLink = new LinkLabel
        {
            Text = discordPrefix + discordText, ForeColor = Theme.MutedColor, LinkColor = Theme.LinkColor, VisitedLinkColor = Theme.LinkColor,
            ActiveLinkColor = Theme.TextColor, LinkBehavior = LinkBehavior.HoverUnderline,
            Location = new Point(24, 56), Size = new Size(680, 22), AutoEllipsis = true
        };
        discordLink.Links.Add(discordPrefix.Length, discordText.Length);
        discordLink.LinkClicked += (_, _) => OpenShell(DiscordUrl, "Could not open the web page.");
        var headerLabels = new[]
        {
            title, discordLink,
            HeaderLabel("Drop individual mods or mod packs into the mod manager to install them automatically to the game directory.", 96),
            HeaderLabel("Reorder installed mods by selecting and dragging them in the mod list to change their load order.", 116),
            HeaderLabel("Important: Mods load top to bottom, so when files conflict, the mod loaded last overrides earlier ones. Conflicting files are highlighted in yellow.", 136, 38, ellipsis: false)
        };
        var pathLabel = HeaderLabel("Game directory: ", 176, 20, ellipsis: false);
        var pathHost = Theme.CreateTextFieldHost(_gameDirectoryBox);
        pathHost.Top = 200;
        pathHost.Left = 24;
        var browseButton = Theme.CreateButton("Browse...", BrowseForGameDirectory);
        browseButton.Top = 200;
        var header = new Panel { Dock = DockStyle.Top, Height = 242, BackColor = Color.Transparent, Controls = { pathLabel, pathHost, browseButton } };
        header.Controls.AddRange(headerLabels);
        header.Resize += (_, _) =>
        {
            browseButton.Left = header.ClientSize.Width - browseButton.Width - 24;
            pathHost.Width = Math.Max(0, browseButton.Left - pathHost.Left - 16);
            foreach (var label in headerLabels) label.Width = Math.Max(0, header.ClientSize.Width - label.Left - 24);
        };

        // Status console (collapsed bar that expands into a history)
        var consoleTitle = new Label { Text = "Debug", ForeColor = Theme.TextColor, Location = new Point(8, 4), Size = new Size(62, 18), Cursor = Cursors.Hand };
        var consoleHeader = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Theme.PanelColor, Cursor = Cursors.Hand, Controls = { consoleTitle, _consoleStatus } };
        var consolePanel = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Theme.PanelColor, BorderStyle = BorderStyle.FixedSingle, Controls = { _consoleHistory, consoleHeader } };
        Theme.UseDarkScrollbars(_consoleHistory);

        void SetConsoleExpanded(bool expanded)
        {
            consolePanel.Height = expanded ? 150 : 28;
            _consoleHistory.Visible = expanded;
        }
        consoleHeader.Resize += (_, _) => _consoleStatus.Width = Math.Max(0, consoleHeader.ClientSize.Width - _consoleStatus.Left - 8);
        foreach (var control in new Control[] { consoleHeader, consoleTitle, _consoleStatus })
            control.Click += (_, _) => SetConsoleExpanded(!_consoleHistory.Visible);

        // Layout: controls are docked in reverse order of being added, so the list (Fill) goes first.
        var searchHost = Theme.CreateTextFieldHost(_modSearchBox, DockStyle.Top);
        var listArea = new Panel
        {
            Dock = DockStyle.Fill, BackColor = Color.Transparent, Padding = new Padding(24, 0, 0, 22),
            Controls = { _modList, Theme.CreateGap(DockStyle.Top), searchHost, consolePanel, Theme.CreateGap(DockStyle.Bottom) }
        };

        // Mod browser: lives in its own window, opened from the sidebar.
        _nexus = new NexusClient(version);
        _modBrowser = new ModBrowserPanel(_nexus)
        {
            CachePath = Path.Combine(SettingsDirectory, "nexus-catalog.json"),
            GetInstallInfo = GetNexusInstallInfo,
            InstallAsync = NexusInstallAsync,
            Uninstall = NexusUninstall,
            OpenUrl = url => OpenShell(url, "Could not open the web page."),
            ReportStatus = SetStatus,
            ReportError = ShowError,
            PrepareControl = EnableFileDrop,
            Columns = ReadSettings()?.BrowserColumns ?? 1
        };
        _modBrowser.ColumnsChanged += (_, _) =>
        {
            try { SaveSettings(); } catch { }
        };
        _modBrowser.UpdateCountChanged += (_, _) => UpdateBrowserButton();
        _modBrowser.CatalogChanged += (_, _) => AutoLinkInstalledMods();
        _browserWindow = new ModBrowserWindow(_modBrowser, Icon);
        EnableFileDrop(_browserWindow); // ZIPs can be dropped onto the browser window too

        Controls.Add(listArea);
        Controls.Add(sidebar);
        Controls.Add(header);
        EnableFileDrop(this);

        Load += (_, _) =>
        {
            InitializeManager();
            if (_nexusApiKey != "") _ = ValidateNexusKeyAsync(interactive: false);
            _ = _modBrowser.InitializeAsync();
        };
        Shown += (_, _) => _fillModListColumn();
        ResizeBegin += (_, _) => _windowResizing = true;
        ResizeEnd += (_, _) =>
        {
            _windowResizing = false;
            _modListResizeTimer.Stop();
            _fillModListColumn();
            Invalidate(true);
        };
        FormClosing += (_, _) => SaveWindowBounds();
        FormClosed += (_, _) =>
        {
            _wallpaperImage?.Dispose();
            _modListResizeTimer.Dispose();
            _browserWindow.Dispose(); // also disposes the browser panel it hosts
            _nexus.Dispose();
        };
    }

    // ---- Window bounds

    private void RestoreWindowBounds()
    {
        try
        {
            if (!File.Exists(WindowFile) || JsonSerializer.Deserialize<int[]>(File.ReadAllText(WindowFile)) is not [var x, var y, var width, var height] || width <= 0 || height <= 0) return;
            if (Screen.AllScreens.FirstOrDefault(screen => screen.WorkingArea.IntersectsWith(new Rectangle(x, y, width, height))) is not { } savedScreen) return;
            var area = savedScreen.WorkingArea;
            width = Math.Max(MinimumSize.Width, width);
            height = Math.Max(MinimumSize.Height, height);
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width)), Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height)), width, height);
        }
        catch { } // a damaged window.json just means the default size
    }

    private void SaveWindowBounds()
    {
        if (WindowState != FormWindowState.Normal) return;
        try { SaveJson(WindowFile, new[] { Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height }); }
        catch { }
    }

    // ---- Wallpaper

    private static Stream? OpenAsset(string name) => typeof(MainForm).Assembly.GetManifestResourceStream("Redout2ModManager.Assets." + name);

    /// <summary>Loads an image and bakes the dimming overlay into it so painting is a single DrawImage.</summary>
    private static Bitmap LoadWallpaper(string imagePath)
    {
        using var stream = File.OpenRead(imagePath);
        return LoadWallpaper(stream);
    }

    private static Bitmap LoadWallpaper(Stream imageStream)
    {
        using var source = new Bitmap(imageStream);
        var dimmed = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(dimmed);
        graphics.DrawImage(source, 0, 0, dimmed.Width, dimmed.Height);
        using var overlay = new SolidBrush(Color.FromArgb(Theme.WallpaperDim, Theme.BackgroundColor));
        graphics.FillRectangle(overlay, 0, 0, dimmed.Width, dimmed.Height);
        return dimmed;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (!_wallpaperEnabled || _wallpaperImage is not { } image || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            base.OnPaintBackground(e);
            return;
        }
        // "Cover" fit: scale to fill the client area and crop the overflow from the centre.
        var scale = Math.Max((double)ClientSize.Width / image.Width, (double)ClientSize.Height / image.Height);
        var cropWidth = Math.Min(image.Width, (int)Math.Round(ClientSize.Width / scale));
        var cropHeight = Math.Min(image.Height, (int)Math.Round(ClientSize.Height / scale));
        e.Graphics.CompositingMode = CompositingMode.SourceCopy;
        e.Graphics.CompositingQuality = CompositingQuality.HighSpeed;
        e.Graphics.InterpolationMode = _windowResizing ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(image, ClientRectangle, new Rectangle((image.Width - cropWidth) / 2, (image.Height - cropHeight) / 2, cropWidth, cropHeight), GraphicsUnit.Pixel);
    }

    private void ChooseWallpaper()
    {
        using var picker = new OpenFileDialog { Title = "Choose a custom wallpaper", Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*", CheckFileExists = true };
        if (picker.ShowDialog(DialogParent) != DialogResult.OK) return;
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var destination = Path.Combine(SettingsDirectory, "custom-wallpaper" + Path.GetExtension(picker.FileName));
            File.Copy(picker.FileName, destination, overwrite: true);
            var replacement = LoadWallpaper(destination);
            _wallpaperImage?.Dispose();
            _wallpaperImage = replacement;
            _wallpaperCustomPath = destination;
            _wallpaperEnabled = true;
            SaveSettings();
            Invalidate(true);
            SetStatus("Custom wallpaper selected.");
        }
        catch (Exception ex) { ShowError("Could not use that wallpaper.", ex); }
    }

    // ---- Status, errors, shell helpers

    private void SetStatus(string message)
    {
        _consoleStatus.Text = message;
        _consoleHistory.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        var lines = _consoleHistory.Lines;
        if (lines.Length > 200) _consoleHistory.Lines = lines[^200..];
    }

    private void ShowError(string message, Exception exception)
    {
        SetStatus($"{message} {exception.Message}");
        MessageBox.Show(DialogParent, $"{message}\r\n\r\n{exception.Message}", "R2MM", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private bool OpenShell(string target, string errorMessage)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            ShowError(errorMessage, ex);
            return false;
        }
    }

    private void OpenModFolder()
    {
        if (EnsureGameSelected() && OpenShell(_gameDirectoryPaks, "Could not open the game's mod folder.")) SetStatus("Mod folder opened");
    }

    // ---- Popups (Options, Links, View conflicts)

    private PopupForm? _popup;

    /// <summary>The window that owns message boxes and dialogs: the open popup, so it stays put behind them, or else the main window.</summary>
    private IWin32Window DialogParent => _popup is { IsDisposed: false, Visible: true } ? _popup : this;

    /// <summary>Shows a popup next to the main window. It closes itself when the user clicks elsewhere in the app.</summary>
    private void ShowPopup(PopupForm dialog)
    {
        _popup?.Close();
        _popup = dialog;
        // Centred on the main window, worked out here from the final size so the popup always opens in the same place.
        var host = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var area = Screen.FromRectangle(host).WorkingArea;
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(
            Math.Clamp(host.Left + (host.Width - dialog.Width) / 2, area.Left, Math.Max(area.Left, area.Right - dialog.Width)),
            Math.Clamp(host.Top + (host.Height - dialog.Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - dialog.Height)));
        dialog.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_popup, dialog)) _popup = null;
        };
        dialog.Show(this);
    }

    /// <summary>A sidebar button that opens a popup. Pressing it while that popup is open closes the popup (the press itself already dismissed it) instead of reopening it.</summary>
    private Button CreatePopupButton(string text, string popupTitle, Action show)
    {
        var button = Theme.CreateButton(text);
        var pressClosedPopup = false;
        button.MouseDown += (_, _) => pressClosedPopup = PopupForm.WasJustDismissed(popupTitle, button);
        button.Click += (_, _) =>
        {
            var skip = pressClosedPopup;
            pressClosedPopup = false;
            if (!skip) show();
        };
        return button;
    }

    private void ShowOptions()
    {
        var dialog = new ManagerMenuDialog("Options");
        dialog.AddButton("Open mods folder", OpenModFolder);
        dialog.AddToggle("Wallpaper", _wallpaperEnabled, isOn =>
        {
            _wallpaperEnabled = isOn;
            Invalidate(true);
        });
        dialog.AddButton("Choose wallpaper...", ChooseWallpaper);
        dialog.AddButton("Nexus API key...", ConfigureNexusApiKey);
        ShowPopup(dialog);
    }

    private void ShowLinks()
    {
        var dialog = new ManagerMenuDialog("Links");
        foreach (var (name, url) in new[] { ("GitHub", "https://github.com/AlyraCreations/Redout-2-Mod-Manager"), ("Discord", DiscordUrl), ("Nexus Mods", NexusUrl), ("GameBanana", GameBananaUrl) })
            dialog.AddButton(name, () => OpenShell(url, "Could not open the web page."));
        ShowPopup(dialog);
    }
}
