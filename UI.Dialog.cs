using System.Diagnostics;

namespace R2MM;

internal sealed class ManagerMenuDialog : Form
{
    private readonly FlowLayoutPanel _content;
    private readonly Button _closeButton;

    public ManagerMenuDialog(Form owner, string title)
    {
        Owner = owner;
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        Size = new Size(198, 430);
        MinimumSize = new Size(198, 340);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.CreateFont(9);

        var outer = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            BackColor = Theme.Background
        };
        var heading = new Label
        {
            Text = title.ToUpperInvariant(),
            Dock = DockStyle.Top,
            Height = 48,
            Font = Theme.CreateFont(16, FontStyle.Bold),
            ForeColor = Theme.Text,
            TextAlign = ContentAlignment.MiddleCenter
        };
        _content = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Theme.Background,
            Padding = new Padding(0, 8, 8, 8)
        };
        _content.Resize += (_, _) => CenterMenuControls();

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Theme.Background };
        _closeButton = MakeButton("Close", Close);
        _closeButton.Size = new Size(150, 32);
        footer.Controls.Add(_closeButton);
        footer.Resize += (_, _) =>
            _closeButton.Location = new Point((footer.ClientSize.Width - _closeButton.Width) / 2,
                (footer.ClientSize.Height - _closeButton.Height) / 2);

        outer.Controls.Add(_content);
        outer.Controls.Add(footer);
        outer.Controls.Add(heading);
        Controls.Add(outer);
    }

    public void AddSection(string title)
    {
        _content.Controls.Add(new Label
        {
            Text = title,
            Width = 150,
            Height = 30,
            Font = Theme.CreateFont(11, FontStyle.Bold),
            ForeColor = Theme.Muted,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = new Padding(0, 8, 0, 2)
        });
    }

    public void AddAction(string label, Action action) => AddButton(label, action);

    public void AddLink(string label, string url) =>
        AddButton(label, () => Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }));

    public void AddToggle(string label, bool initial, Action<bool> changed)
    {
        var isOn = initial;
        var toggle = MakeButton($"{label}: {(isOn ? "On" : "Off")}", () => { });
        toggle.Click += (_, _) =>
        {
            isOn = !isOn;
            toggle.Text = $"{label}: {(isOn ? "On" : "Off")}";
            changed(isOn);
        };
        toggle.Size = new Size(150, 32);
        toggle.Margin = new Padding(0, 5, 0, 5);
        _content.Controls.Add(toggle);
        CenterMenuControls();
    }

    private void AddButton(string label, Action action)
    {
        var button = MakeButton(label, action);
        button.Size = new Size(150, 32);
        button.Margin = new Padding(0, 5, 0, 5);
        _content.Controls.Add(button);
        CenterMenuControls();
    }

    private void CenterMenuControls()
    {
        foreach (Control control in _content.Controls)
        {
            if (control is Button button)
                button.Margin = new Padding(Math.Max(0, (_content.ClientSize.Width - button.Width) / 2), 5, 0, 5);
            else if (control is Label label)
                label.Margin = new Padding(Math.Max(0, (_content.ClientSize.Width - label.Width) / 2), label.Margin.Top, 0, label.Margin.Bottom);
        }
    }

    private static Button MakeButton(string label, Action action)
    {
        var button = new Button
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Theme.Button,
            ForeColor = Theme.Text,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            Font = Theme.CreateFont(9)
        };
        button.FlatAppearance.BorderColor = Theme.Border;
        button.FlatAppearance.MouseOverBackColor = Theme.ButtonHover;
        button.Click += (_, _) => action();
        return button;
    }
}
