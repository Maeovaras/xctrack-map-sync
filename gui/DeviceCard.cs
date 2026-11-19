using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace XCTrackMapSync;

internal sealed class DeviceCard : Panel
{
    private readonly StatusDot _dot;
    private readonly Label _caption;
    private readonly Label _name;
    private readonly Label _state;
    private readonly Label _details;
    private readonly Label _incoming;
    private string _disconnectedText = "Neprijungtas";
    private string _waitingText = "Laukiama USB / MTP įrenginio";
    private string _unlockText = "Atrakinkite įrenginį ir pasirinkite File transfer.";
    private string _connectedText = "Prijungtas ir paruoštas";
    private bool _isConnected;

    public DeviceCard(string caption)
    {
        DoubleBuffered = true;
        BackColor = Color.White;
        Padding = new Padding(1);
        Margin = new Padding(0, 0, 12, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(20, 14, 20, 12),
            Margin = Padding.Empty,
            ColumnCount = 1,
            RowCount = 4
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _caption = new Label
        {
            Text = caption.ToUpperInvariant(),
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(100, 110, 125),
            Font = new Font("Segoe UI Semibold", 9f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };

        _dot = new StatusDot
        {
            Size = new Size(18, 18),
            IsConnected = false,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 7, 0)
        };

        _name = new Label
        {
            Text = "Neprijungtas",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(31, 41, 55),
            Font = new Font("Segoe UI Semibold", 13.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = Padding.Empty
        };

        var nameRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            ColumnCount = 2,
            RowCount = 1
        };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameRow.Controls.Add(_dot, 0, 0);
        nameRow.Controls.Add(_name, 1, 0);

        _state = new Label
        {
            Text = "Laukiama USB / MTP įrenginio",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(185, 55, 55),
            Font = new Font("Segoe UI", 9.5f),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(30, 0, 0, 0)
        };

        _details = new Label
        {
            Text = "Atrakinkite įrenginį ir pasirinkite File transfer.",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(100, 110, 125),
            Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty
        };

        _incoming = new Label
        {
            Text = string.Empty,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(154, 103, 0),
            Font = new Font("Segoe UI Semibold", 9f),
            TextAlign = ContentAlignment.TopLeft,
            Margin = Padding.Empty
        };

        var detailsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(0, 5, 0, 0),
            Margin = Padding.Empty,
            ColumnCount = 1,
            RowCount = 2
        };
        detailsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        detailsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        detailsPanel.Controls.Add(_details, 0, 0);
        detailsPanel.Controls.Add(_incoming, 0, 1);

        layout.Controls.Add(_caption, 0, 0);
        layout.Controls.Add(nameRow, 0, 1);
        layout.Controls.Add(_state, 0, 2);
        layout.Controls.Add(detailsPanel, 0, 3);
        Controls.Add(layout);
    }

    public void SetLanguage(
        string caption,
        string disconnected,
        string waiting,
        string unlock,
        string connected)
    {
        _caption.Text = caption.ToUpperInvariant();
        _disconnectedText = disconnected;
        _waitingText = waiting;
        _unlockText = unlock;
        _connectedText = connected;

        if (_isConnected)
        {
            _state.Text = _connectedText;
        }
        else
        {
            _name.Text = _disconnectedText;
            _state.Text = _waitingText;
            _details.Text = _unlockText;
            _incoming.Text = string.Empty;
        }
    }

    public void SetDisconnected(string? message = null)
    {
        _isConnected = false;
        _dot.IsConnected = false;
        _name.Text = _disconnectedText;
        _state.Text = message ?? _waitingText;
        _state.ForeColor = Color.FromArgb(185, 55, 55);
        _details.Text = _unlockText;
        _incoming.Text = string.Empty;
    }

    public void SetConnected(string name, string layout, string details, string incoming = "")
    {
        _isConnected = true;
        _dot.IsConnected = true;
        _name.Text = name;
        _state.Text = _connectedText;
        _state.ForeColor = Color.FromArgb(18, 135, 80);
        _details.Text = string.IsNullOrWhiteSpace(details) ? layout : details;
        _incoming.Text = incoming;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(224, 229, 237));
        e.Graphics.DrawRoundedRectangle(pen, new Rectangle(0, 0, Width - 1, Height - 1), 12);
    }
}

internal sealed class StatusDot : Control
{
    private bool _isConnected;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            _isConnected = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = IsConnected ? Color.FromArgb(24, 181, 106) : Color.FromArgb(221, 75, 75);
        using var glow = new SolidBrush(Color.FromArgb(45, color));
        using var fill = new SolidBrush(color);
        e.Graphics.FillEllipse(glow, 0, 0, Width - 1, Height - 1);
        e.Graphics.FillEllipse(fill, 4, 4, Width - 9, Height - 9);
    }
}

internal static class GraphicsExtensions
{
    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        using var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.DrawPath(pen, path);
    }
}
