namespace R2MM;

/// <summary>One file that is provided by more than one enabled mod.</summary>
internal sealed record ConflictRow(string Asset, string Winner, string Overridden);

internal sealed class ConflictsDialog : Form
{
    private static readonly Color BackgroundColor = Theme.Background;
    private static readonly Color PanelColor = Theme.Panel;
    private static readonly Color ButtonColor = Theme.Button;
    private static readonly Color TextColor = Theme.Text;
    private static readonly Color MutedColor = Theme.Muted;
    private static readonly Color BorderColor = Theme.Border;
    private static readonly Color ButtonHoverColor = Theme.ButtonHover;

    public ConflictsDialog(IReadOnlyList<ConflictRow> rows, IReadOnlyList<string> unreadableMods)
    {
        Text = "Mod conflicts";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        Size = new Size(920, 560);
        MinimumSize = new Size(640, 360);
        BackColor = BackgroundColor;
        ForeColor = TextColor;
        Font = Theme.CreateFont(9);
        ShowInTaskbar = false;

        var message = rows.Count == 0
            ? "No conflicts: ."
            : $"{rows.Count} file(s) write to the same address.";
        if (unreadableMods.Count > 0)
        {
            message += $"\r\nNot checked (no readable PAK index or .pak.list file): {string.Join(", ", unreadableMods)}.";
        }
        var header = new Label
        {
            Text = message,
            Dock = DockStyle.Top,
            Height = 64,
            Padding = new Padding(16, 12, 16, 0),
            ForeColor = MutedColor
        };

        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            BackColor = PanelColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.FixedSingle,
            OwnerDraw = true
        };
        list.Columns.Add("File", 460);
        list.Columns.Add("Overrides", 180);
        list.Columns.Add("Overwridden", 220);
        list.DrawColumnHeader += (_, e) => Theme.DrawColumnHeader(e, list.Font);
        list.DrawItem += (_, e) => e.DrawDefault = true;
        list.DrawSubItem += (_, e) => e.DrawDefault = true;
        list.Resize += (_, _) =>
        {
            if (list.Columns.Count == 3)
            {
                list.Columns[0].Width = Math.Max(240, list.ClientSize.Width - list.Columns[1].Width - list.Columns[2].Width);
            }
        };
        list.BeginUpdate();
        foreach (var row in rows)
        {
            var item = new ListViewItem(row.Asset);
            item.SubItems.Add(row.Winner);
            item.SubItems.Add(row.Overridden);
            list.Items.Add(item);
        }
        list.EndUpdate();

        var closeButton = new Button
        {
            Text = "Close",
            Size = new Size(110, 32),
            BackColor = ButtonColor,
            ForeColor = TextColor,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        closeButton.FlatAppearance.BorderColor = BorderColor;
        closeButton.FlatAppearance.MouseOverBackColor = ButtonHoverColor;
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = BackgroundColor };
        footer.Controls.Add(closeButton);
        footer.Layout += (_, _) => closeButton.Location = new Point((footer.ClientSize.Width - closeButton.Width) / 2, 10);
        AcceptButton = closeButton;
        CancelButton = closeButton;

        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 0), BackColor = BackgroundColor };
        var searchHost = new Panel
        {
            Dock = DockStyle.Top,
            Height = 32,
            BackColor = PanelColor,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(8, 0, 8, 0)
        };
        var search = new TextBox
        {
            Dock = DockStyle.None,
            AutoSize = false,
            Multiline = false,
            PlaceholderText = "Search conflicting files or mods...",
            BackColor = PanelColor,
            ForeColor = TextColor,
            BorderStyle = BorderStyle.None,
            Font = Theme.CreateFont(9),
            Margin = Padding.Empty
        };
        void CenterConflictSearchText()
        {
            var textHeight = Math.Min(search.PreferredHeight, searchHost.ClientSize.Height);
            search.SetBounds(searchHost.Padding.Left,
                Math.Max(0, (searchHost.ClientSize.Height - textHeight) / 2),
                Math.Max(0, searchHost.ClientSize.Width - searchHost.Padding.Horizontal), textHeight);
        }
        searchHost.Layout += (_, _) => CenterConflictSearchText();
        searchHost.Resize += (_, _) => CenterConflictSearchText();
        searchHost.Controls.Add(search);
        var searchListGap = new Panel
        {
            Dock = DockStyle.Top,
            Height = 10,
            BackColor = BackgroundColor
        };
        var allRows = rows.ToList();
        void ApplyFilter()
        {
            var query = search.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var row in allRows.Where(row => string.IsNullOrEmpty(query) ||
                row.Asset.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Winner.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Overridden.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                var item = new ListViewItem(row.Asset);
                item.SubItems.Add(row.Winner);
                item.SubItems.Add(row.Overridden);
                list.Items.Add(item);
            }
            list.EndUpdate();
        }
        search.TextChanged += (_, _) => ApplyFilter();
        listHost.Controls.Add(list);
        listHost.Controls.Add(searchListGap);
        listHost.Controls.Add(searchHost);
        Controls.Add(listHost);
        Controls.Add(footer);
        Controls.Add(header);
    }
}
