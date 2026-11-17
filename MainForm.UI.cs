using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace R2MM;

internal sealed partial class MainForm : Form
{
    private static class UiText
    {
        public const string WindowTitle = "R2MM v";
        public const string MainTitle = "REDOUT 2 MOD MANAGER v";
        public const string DiscordPrefix = "Questions, feedback, or suggestions? Join the offical Discord: ";
        public const string DiscordUrlText = "discord.gg/redout";
        public const string DragDropHint = "Drop individuall mods or mod packs into the mod manager to install them automatically to the game directory.";
        public const string LoadOrderHint = "Reorder installed mods by selecting and dragging them in the mod list to change their load order.";
        public const string ConflictHint = "Important: Mods load top to bottom, so when files conflict, the mod loaded last overrides earlier ones. Conflicting files are highlighted in yellow.";
        public const string GameFolder = "Game directory: ";
        public const string SearchMods = "Search mods...";
        public const string Console = "Debug";
        public const string ConfirmChanges = "Confirm change(s)";
        public const string RevertChanges = "Revert change(s)";
        public const string ToggleAll = "Toggle all";
        public const string UninstallSelected = "Uninstall selected";
        public const string RefreshModList = "Refresh mod list";
        public const string ViewConflicts = "Inspect conflict(s)";
        public const string Options = "Options";
        public const string Downloads = "Links";
        public const string LaunchGame = "Launch game";
        public const string ModListHeader = "Mod list";
        public const string IndexColumn = "#";
        public const string StatusColumn = "Status";
        public const string ConflictsColumn = "Conflicts";
    }

    public MainForm()
    {
        var version = typeof(MainForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? throw new InvalidOperationException("Application version metadata is missing.");
        Text = $"{UiText.WindowTitle}{version}";
        Icon = LoadWindowIcon();
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(960, 680);
        MinimumSize = new Size(960, 680);
        RestoreWindowBounds();
        BackColor = BackgroundColor;
        ForeColor = TextColor;
        Font = Theme.CreateFont(9);
        DoubleBuffered = true;
        ResizeRedraw = true;
        _backgroundArt = LoadBackgroundArt();

        _modList.View = View.Details;
        _modList.FullRowSelect = true;
        _modList.CheckBoxes = true;
        _modList.GridLines = false;
        _modList.HideSelection = false;
        _modList.BackColor = PanelColor;
        _modList.ForeColor = TextColor;
        _modList.BorderStyle = BorderStyle.FixedSingle;
        _modList.Dock = DockStyle.Fill;
        _modList.Scrollable = true;
        _modList.OwnerDraw = true;
        _modList.ShowItemToolTips = true;
        _modList.Columns.Add(UiText.ModListHeader, 300);
        _modList.Columns.Add(UiText.IndexColumn, 44);
        _modList.Columns.Add(UiText.StatusColumn, 120);
        _modList.Columns.Add(UiText.ConflictsColumn, 300);
        _modList.DrawColumnHeader += (_, e) => Theme.DrawColumnHeader(e, _modList.Font);
        _modList.DrawItem += (_, e) => e.DrawDefault = true;
        _modList.DrawSubItem += (_, e) => e.DrawDefault = true;
        _columnTimer.Tick += (_, _) =>
        {
            _columnTimer.Stop();
            ResizeLastColumn();
        };
        _modList.Resize += (_, _) =>
        {
            _columnTimer.Stop();
            _columnTimer.Start();
        };
        _modList.ItemDrag += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(_searchBox.Text)) BeginModDrag(_modList, e);
        };
        _modList.DragOver += HandleModDragOver;
        _modList.DragDrop += HandleModDrop;
        _modList.DragLeave += (_, _) => ClearDropIndicator();
        _modList.ColumnWidthChanged += (_, _) => ResizeLastColumn();
        _modList.ItemCheck += (_, e) =>
        {
            if (!_initializing && e.Index >= 0 && _modList.Items[e.Index].Tag is string name)
                _desiredStates[name] = e.NewValue == CheckState.Checked;
            if (!_initializing && IsHandleCreated) BeginInvoke(new Action(UpdateSelectionButtons));
        };
        _modList.SelectedIndexChanged += (_, _) => UpdateSelectionButtons();

        var sidebar = new Panel
        {
            Dock = DockStyle.Right,
            // Fixed-width sidebar: buttons retain a 24 px right margin.
            Width = 190,
            BackColor = Color.Transparent,
            // Docked below the header; first button starts 10 px below the game-folder row.
            Padding = new Padding(0, 0, 24, 0)
        };
        _sidebarPanel = sidebar;
        _confirmButton = CreateButton(UiText.ConfirmChanges, ConfirmChanges);
        _confirmButton.Enabled = false;
        _revertButton = CreateButton(UiText.RevertChanges, RevertChanges);
        _revertButton.Enabled = false;
        var toggleAllButton = CreateButton(UiText.ToggleAll, ToggleAllMods);
        _removeButton = CreateButton(UiText.UninstallSelected, UninstallSelectedMods);
        var refreshButton = CreateButton(UiText.RefreshModList, RefreshMods);
        _conflictsButton = CreateButton(UiText.ViewConflicts, ShowConflicts);
        var optionsButton = CreateButton(UiText.Options, ShowOptions);
        var downloadsButton = CreateButton(UiText.Downloads, ShowDownloads);
        var launchButton = CreateButton(UiText.LaunchGame, StartGame);
        launchButton.BackColor = AccentColor;
        launchButton.FlatAppearance.BorderSize = 0;
        _sidebarButtons.AddRange([_confirmButton, _revertButton, toggleAllButton, _removeButton,
            refreshButton, _conflictsButton, optionsButton, downloadsButton, launchButton]);
        foreach (var button in _sidebarButtons)
        {
            button.Size = new Size(150, 32);
            // Only top/right anchoring: fixed vertical positions, fixed right inset.
            button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            button.Margin = Padding.Empty;
            sidebar.Controls.Add(button);
        }
        sidebar.Resize += (_, _) => LayoutSidebarButtons();

        var header = new Panel
        {
            Dock = DockStyle.Top,
            // Header ends immediately after the game-folder row (y=232).
            Height = 242,
            BackColor = Color.Transparent
        };
        var title = new Label
        {
            Text = $"{UiText.MainTitle}{version}",
            Font = Theme.CreateFont(18, FontStyle.Bold),
            ForeColor = TextColor,
            Location = new Point(24, 16),
            Size = new Size(480, 36),
            AutoEllipsis = true
        };
        header.Controls.Add(title);
        const string discordText = UiText.DiscordUrlText;
        var discordLink = new LinkLabel
        {
            Text = $"{UiText.DiscordPrefix}{discordText}",
            ForeColor = MutedColor,
            LinkColor = Color.FromArgb(124, 146, 240),
            ActiveLinkColor = TextColor,
            VisitedLinkColor = Color.FromArgb(124, 146, 240),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Cursor = Cursors.Hand,
            Location = new Point(24, 56),
            Size = new Size(680, 22),
            AutoEllipsis = true
        };
        discordLink.Links.Add(discordLink.Text.IndexOf(discordText, StringComparison.Ordinal), discordText.Length);
        discordLink.LinkClicked += (_, _) => OpenUrl("https://discord.gg/redout");
        header.Controls.Add(discordLink);
        var dropHint = new Label
        {
            Text = UiText.DragDropHint,
            ForeColor = MutedColor,
            Location = new Point(24, 96),
            Size = new Size(680, 22),
            AutoEllipsis = true
        };
        header.Controls.Add(dropHint);
        var priorityHint = new Label
        {
            Text = UiText.LoadOrderHint,
            ForeColor = MutedColor,
            Location = new Point(24, 116),
            Size = new Size(680, 22),
            AutoEllipsis = true
        };
        header.Controls.Add(priorityHint);
        var orderHint = new Label
        {
            Text = UiText.ConflictHint,
            ForeColor = MutedColor,
            Location = new Point(24, 136),
            Size = new Size(680, 38)
        };
        header.Controls.Add(orderHint);

        var pathLabel = new Label
        {
            Text = UiText.GameFolder,
            ForeColor = MutedColor,
            Location = new Point(24, 176),
            Size = new Size(130, 20)
        };
        header.Controls.Add(pathLabel);
        var pathField = new Panel
        {
            BackColor = PanelColor,
            BorderStyle = BorderStyle.FixedSingle,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Location = new Point(24, 200),
            Size = new Size(Width - 250, 32),
            Padding = new Padding(8, 0, 8, 0)
        };
        _gamePathBox.ReadOnly = true;
        _gamePathBox.BackColor = PanelColor;
        _gamePathBox.ForeColor = TextColor;
        _gamePathBox.BorderStyle = BorderStyle.None;
        _gamePathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _gamePathBox.Margin = Padding.Empty;
        pathField.Controls.Add(_gamePathBox);
        pathField.Layout += (_, _) =>
        {
            _gamePathBox.SetBounds(
                pathField.Padding.Left,
                Math.Max(0, (pathField.ClientSize.Height - _gamePathBox.PreferredHeight) / 2),
                Math.Max(0, pathField.ClientSize.Width - pathField.Padding.Horizontal),
                _gamePathBox.PreferredHeight);
        };
        header.Controls.Add(pathField);

        var browseButton = CreateButton("Browse...", BrowseForGameFolder);
        browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        browseButton.Location = new Point(Width - 190, 200);
        header.Controls.Add(browseButton);
        header.Resize += (_, _) =>
        {
            browseButton.Left = header.ClientSize.Width - browseButton.Width - 24;
            title.Width = Math.Max(0, header.ClientSize.Width - title.Left - 24);
            discordLink.Width = Math.Max(0, header.ClientSize.Width - discordLink.Left - 24);
            dropHint.Width = Math.Max(0, header.ClientSize.Width - dropHint.Left - 24);
            priorityHint.Width = Math.Max(0, header.ClientSize.Width - priorityHint.Left - 24);
            orderHint.Width = Math.Max(0, header.ClientSize.Width - orderHint.Left - 24);
            pathField.Width = Math.Max(0, browseButton.Left - pathField.Left - 16);
        };

        var listHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = Padding.Empty
        };
        var listArea = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Padding = new Padding(24, 0, 0, 22)
        };
        _modList.Dock = DockStyle.Fill;
        var searchHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32,
            BackColor = PanelColor,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8, 0, 8, 0)
        };
        _searchBox.Dock = DockStyle.None;
        _searchBox.AutoSize = false;
        _searchBox.Multiline = false;
        _searchBox.PlaceholderText = UiText.SearchMods;
        _searchBox.BackColor = PanelColor;
        _searchBox.ForeColor = TextColor;
        _searchBox.BorderStyle = BorderStyle.None;
        _searchBox.Margin = Padding.Empty;
        void CenterMainSearchText()
        {
            var textHeight = Math.Min(_searchBox.PreferredHeight, searchHost.ClientSize.Height);
            _searchBox.SetBounds(searchHost.Padding.Left,
                Math.Max(0, (searchHost.ClientSize.Height - textHeight) / 2),
                Math.Max(0, searchHost.ClientSize.Width - searchHost.Padding.Horizontal), textHeight);
        }
        searchHost.Layout += (_, _) => CenterMainSearchText();
        searchHost.Resize += (_, _) => CenterMainSearchText();
        _searchBox.TextChanged += (_, _) => RefreshModList(preserveDesiredStates: true);
        searchHost.Controls.Add(_searchBox);
        var searchListGap = new Panel
        {
            Dock = DockStyle.Top,
            Height = 10,
            BackColor = Color.Transparent
        };
        listArea.Controls.Add(_modList);
        listArea.Controls.Add(searchListGap);
        listArea.Controls.Add(searchHost);

        var consolePanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            BackColor = PanelColor,
            BorderStyle = BorderStyle.FixedSingle
        };
        _consolePanel = consolePanel;
        var consoleHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 26,
            BackColor = PanelColor,
            Cursor = Cursors.Hand
        };
        var consoleTitle = new Label
        {
            Text = UiText.Console,
            ForeColor = TextColor,
            Font = Theme.CreateFont(9, FontStyle.Bold),
            Location = new Point(8, 4),
            Size = new Size(62, 18),
            Cursor = Cursors.Hand
        };
        _statusLabel.ForeColor = MutedColor;
        _statusLabel.Location = new Point(72, 4);
        _statusLabel.Size = new Size(650, 18);
        _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.Cursor = Cursors.Hand;
        var consoleClose = new Button
        {
            Text = "×",
            Size = new Size(26, 24),
            Location = new Point(consoleHeader.ClientSize.Width - 28, 0),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
            BackColor = ButtonColor,
            ForeColor = TextColor,
            Cursor = Cursors.Hand,
            Visible = false
        };
        consoleClose.FlatAppearance.BorderSize = 0;
        _consoleCloseButton = consoleClose;
        consoleClose.Click += (_, _) => SetConsoleExpanded(false);
        consoleHeader.Resize += (_, _) =>
        {
            consoleClose.Location = new Point(consoleHeader.ClientSize.Width - consoleClose.Width, 1);
            consoleClose.BringToFront();
            consoleTitle.Width = Math.Max(48, consoleClose.Left - consoleTitle.Left - 8);
            _statusLabel.Left = consoleTitle.Right + 4;
            _statusLabel.Width = Math.Max(0, consoleClose.Left - _statusLabel.Left - 8);
        };
        consoleHeader.Controls.Add(consoleTitle);
        consoleHeader.Controls.Add(_statusLabel);
        consoleHeader.Controls.Add(consoleClose);
        _consoleHistory.Dock = DockStyle.Fill;
        _consoleHistory.Multiline = true;
        _consoleHistory.ReadOnly = true;
        _consoleHistory.ScrollBars = ScrollBars.Vertical;
        _consoleHistory.BackColor = PanelColor;
        _consoleHistory.ForeColor = MutedColor;
        _consoleHistory.BorderStyle = BorderStyle.None;
        _consoleHistory.Font = Theme.CreateFont(8);
        _consoleHistory.Visible = false;
        consolePanel.Controls.Add(_consoleHistory);
        consolePanel.Controls.Add(consoleHeader);
        void ExpandConsoleFromHeader(object? _, EventArgs __)
        {
            if (!_consoleExpanded) SetConsoleExpanded(true);
        }
        consolePanel.Click += ExpandConsoleFromHeader;
        consoleHeader.Click += ExpandConsoleFromHeader;
        consoleTitle.Click += ExpandConsoleFromHeader;
        _statusLabel.Click += ExpandConsoleFromHeader;
        var consoleListGap = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 10,
            BackColor = Color.Transparent
        };
        listArea.Controls.Add(consolePanel);
        listArea.Controls.Add(consoleListGap);
        listHost.Controls.Add(listArea);
        Controls.Add(listHost);
        Controls.Add(sidebar);
        Controls.Add(header);
        EnableFileDrop(this);

        Load += (_, _) => InitializeManager();
        Shown += (_, _) => { ResizeLastColumn(); LayoutSidebarButtons(); };
        ResizeBegin += (_, _) => _liveResizing = true;
        ResizeEnd += (_, _) =>
        {
            _liveResizing = false;
            _columnTimer.Stop();
            ResizeLastColumn();
            Invalidate(true);
        };
        FormClosing += (_, _) => SaveWindowBounds();
        FormClosed += (_, _) =>
        {
            _backgroundArt?.Dispose();
            _columnTimer.Dispose();
        };
    }


    private void RestoreWindowBounds()
    {
        try
        {
            string path = Path.Combine(_settingsDirectory, "window.json");
            if (!File.Exists(path)) return;

            int[]? saved = JsonSerializer.Deserialize<int[]>(File.ReadAllText(path));
            if (saved is not { Length: 4 }) return;

            var bounds = new Rectangle(saved[0], saved[1], saved[2], saved[3]);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            // Restore only when the saved position is on a currently available display.
            Screen? screen = Screen.AllScreens.FirstOrDefault(s =>
                s.WorkingArea.IntersectsWith(bounds));
            if (screen is null) return;

            int width = Math.Max(MinimumSize.Width, bounds.Width);
            int height = Math.Max(MinimumSize.Height, bounds.Height);
            Rectangle area = screen.WorkingArea;
            int x = Math.Clamp(bounds.X, area.Left, Math.Max(area.Left, area.Right - width));
            int y = Math.Clamp(bounds.Y, area.Top, Math.Max(area.Top, area.Bottom - height));

            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(x, y, width, height);
        }
        catch
        {
            // Invalid or inaccessible saved settings: keep the default window size/position.
        }
    }


    private void SaveWindowBounds()
    {
        // Save normal bounds only; maximised/minimised states must not replace the last usable size.
        if (WindowState != FormWindowState.Normal) return;

        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            int[] bounds = [Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height];
            string path = Path.Combine(_settingsDirectory, "window.json");
            File.WriteAllText(path, JsonSerializer.Serialize(bounds));
        }
        catch
        {
            // Window persistence is optional; never prevent the application from closing.
        }
    }


    private static Icon? LoadWindowIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("Redout2ModManager.Assets.app.ico");
        return stream is null ? null : new Icon(stream);
    }


    private static Bitmap? LoadBackgroundArt(string? imagePath = null)
    {
        Bitmap source;
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            source = new Bitmap(imagePath);
        }
        else
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream(Theme.BackgroundResource);
            if (stream is null) return null;
            using var embeddedImage = new Bitmap(stream);
            source = new Bitmap(embeddedImage);
        }
        using (source)
        {
            var baked = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(baked);
            graphics.DrawImage(source, new Rectangle(0, 0, baked.Width, baked.Height));
            using var dim = new SolidBrush(Color.FromArgb(Theme.BackgroundDim, Theme.Background));
            graphics.FillRectangle(dim, 0, 0, baked.Width, baked.Height);
            return baked;
        }
    }


    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var size = ClientSize;
        if (!_wallpaperEnabled || _backgroundArt is not { } art || size.Width <= 0 || size.Height <= 0)
        {
            base.OnPaintBackground(e);
            return;
        }

        // "Cover" fit: crop the art to the window's aspect ratio, centred.
        var scale = Math.Max((double)size.Width / art.Width, (double)size.Height / art.Height);
        var sourceWidth = (int)Math.Round(size.Width / scale);
        var sourceHeight = (int)Math.Round(size.Height / scale);
        var source = new Rectangle(
            Math.Max(0, (art.Width - sourceWidth) / 2),
            Math.Max(0, (art.Height - sourceHeight) / 2),
            Math.Min(art.Width, sourceWidth),
            Math.Min(art.Height, sourceHeight));

        var graphics = e.Graphics;
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
        // Cheap sampling while the window is being dragged; smooth once it settles.
        graphics.InterpolationMode = _liveResizing
            ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor
            : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        graphics.DrawImage(art, new Rectangle(0, 0, size.Width, size.Height), source, GraphicsUnit.Pixel);
    }


    private static Image LoadButtonIcon(string resourceName, Size size, bool cropToLeftSquare = false)
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded button image '{resourceName}' was not found.");
        using var source = new Bitmap(stream);
        var sourceBounds = cropToLeftSquare
            ? new Rectangle(0, 0, Math.Min(source.Width, source.Height), Math.Min(source.Width, source.Height))
            : new Rectangle(0, 0, source.Width, source.Height);
        var image = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image: source, destRect: new Rectangle(Point.Empty, size), srcRect: sourceBounds, srcUnit: GraphicsUnit.Pixel);
        }
        return image;
    }


    private static Button CreateButton(string text, Action action)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(150, 32),
            BackColor = ButtonColor,
            ForeColor = TextColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Margin = new Padding(4)
        };
        button.FlatAppearance.BorderColor = BorderColor;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = ButtonHoverColor;
        button.Click += (_, _) => action();
        return button;
    }


    private void ShowOptions()
    {
        using var dialog = new ManagerMenuDialog(this, UiText.Options);
        dialog.AddAction("Open mods folder", OpenModFolder);
        dialog.AddToggle("Wallpaper", _wallpaperEnabled, enabled =>
        {
            _wallpaperEnabled = enabled;
            Invalidate(true);
        });
        dialog.AddAction("Choose wallpaper...", ChooseWallpaper);
        dialog.ShowDialog(this);
    }


    private void ChooseWallpaper()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Choose a custom wallpaper",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            Directory.CreateDirectory(_settingsDirectory);
            var extension = Path.GetExtension(picker.FileName);
            var destination = Path.Combine(_settingsDirectory, "custom-wallpaper" + extension);
            File.Copy(picker.FileName, destination, overwrite: true);
            var replacement = LoadBackgroundArt(destination)
                ?? throw new InvalidDataException("The selected image could not be loaded.");
            _backgroundArt?.Dispose();
            _backgroundArt = replacement;
            _customWallpaperPath = destination;
            _wallpaperEnabled = true;
            SaveSettings();
            Invalidate(true);
            SetStatus("Custom wallpaper selected.");
        }
        catch (Exception ex)
        {
            ShowError("Could not set the custom wallpaper.", ex);
        }
    }


    private void LayoutSidebarButtons()
    {
        if (_sidebarPanel is null || _sidebarButtons.Count == 0) return;

        const int buttonWidth = 150;
        const int buttonHeight = 32;
        const int buttonGap = 10;
        const int topInset = 0;
        const int rightInset = 24;

        // The sidebar is docked to the right and begins immediately below the header.
        // Keep every button at a fixed Y interval; do not distribute them over the
        // available height or anchor the final button to the bottom of the window.
        var x = _sidebarPanel.ClientSize.Width - rightInset - buttonWidth;
        var y = topInset;
        foreach (var button in _sidebarButtons)
        {
            button.SetBounds(x, y, buttonWidth, buttonHeight);
            y += buttonHeight + buttonGap;
        }
    }


    private void ShowDownloads()
    {
        using var dialog = new ManagerMenuDialog(this, UiText.Downloads);
	dialog.AddLink("Github", "https://github.com/AlyraCreations/Redout-2-Mod-Manager");
        dialog.AddLink("Discord", "https://discord.gg/redout");
        dialog.AddLink("Nexus Mods", NexusPage);
        dialog.AddLink("GameBanana", GameBananaPage);
        dialog.ShowDialog(this);
    }


    private void OpenModFolder()
    {
        if (!EnsureGameSelected())
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _paksDirectory,
                UseShellExecute = true
            });
            SetStatus("Mod folder opened");
        }
        catch (Exception ex)
        {
            ShowError("Could not open the game's Paks folder.", ex);
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("Could not open the web page.", ex);
        }
    }


    private void ResizeLastColumn()
    {
        if (_resizingColumns || _modList.Columns.Count == 0)
        {
            return;
        }

        _resizingColumns = true;
        try
        {
            var fixedWidth = 0;
            for (var column = 0; column < _modList.Columns.Count - 1; column++)
            {
                fixedWidth += _modList.Columns[column].Width;
            }
            var width = Math.Max(140, _modList.ClientSize.Width - fixedWidth);
            if (_modList.Columns[^1].Width != width)
            {
                _modList.Columns[^1].Width = width;
            }
        }
        finally
        {
            _resizingColumns = false;
        }
    }


    private void UpdateSelectionButtons()
    {
        if (_initializing)
        {
            return;
        }
        var pending = HasPendingChanges() || HasPendingOrder();
        _confirmButton.Enabled = pending;
        _revertButton.Enabled = pending;
        _confirmButton.FlatAppearance.BorderSize = pending ? 2 : 1;
        _confirmButton.FlatAppearance.BorderColor = pending ? Theme.Highlight : BorderColor;
        _removeButton.Enabled = _modList.SelectedItems.Count > 0;
        UpdateConflictColumn();
    }


    private void SetConsoleExpanded(bool expanded)
    {
        if (_consolePanel is null) return;
        _consoleExpanded = expanded;
        _consolePanel.Height = expanded ? 150 : 28;
        _consoleHistory.Visible = expanded;
        if (_consoleCloseButton is not null) _consoleCloseButton.Visible = expanded;
        _consolePanel.PerformLayout();
    }


    private void SetStatus(string message)
    {
        _statusLabel.Text = message;
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _consoleHistory.AppendText(line + Environment.NewLine);
        // Keep the console bounded during long sessions.
        var lines = _consoleHistory.Lines;
        if (lines.Length > 200)
            _consoleHistory.Lines = lines[^200..];
    }


    private void ShowError(string context, Exception ex)
    {
        SetStatus(context);
        MessageBox.Show(
            this,
            $"{context}\r\n\r\n{ex.Message}",
            "Redout 2 Mod Manager",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

}
