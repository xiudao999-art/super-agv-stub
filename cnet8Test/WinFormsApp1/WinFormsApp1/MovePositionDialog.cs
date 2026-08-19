using System.Drawing.Drawing2D;
using System.Text.Json;

namespace WinFormsApp1;

internal sealed class MovePositionDialog : Form
{
    private static readonly Color Primary = Color.FromArgb(31, 111, 235);
    private static readonly Color Surface = Color.White;
    private static readonly Color Background = Color.FromArgb(246, 248, 251);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private readonly ComboBox _positions = new();
    private readonly Label _xValue = ValueLabel();
    private readonly Label _yValue = ValueLabel();
    private readonly Label _yawValue = ValueLabel();
    private readonly Label _mapValue = ValueLabel();
    private Point _dragOrigin;

    public string SelectedPosition => (_positions.SelectedItem as PositionItem)?.Name ?? string.Empty;
    public PositionItem? SelectedPositionItem => _positions.SelectedItem as PositionItem;

    public MovePositionDialog(IReadOnlyList<PositionItem> positions)
    {
        Text = "选择 MOVE 目标站点";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Background;
        ClientSize = new Size(560, 360);
        MinimumSize = ClientSize;
        Font = new Font("Microsoft YaHei UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = CreateHeader();
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 24, 28, 22), BackColor = Background };
        content.Controls.Add(CreateContent());
        Controls.Add(content);
        Controls.Add(header);

        _positions.DataSource = positions.ToArray();
        _positions.DisplayMember = nameof(PositionItem.Name);
        _positions.SelectedIndexChanged += (_, _) => RefreshDetails();
        RefreshDetails();
        Resize += (_, _) => ApplyRoundedRegion();
        Shown += (_, _) => ApplyRoundedRegion();
    }

    private Panel CreateHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Primary, Padding = new Padding(24, 0, 12, 0) };
        var title = new Label
        {
            Text = "选择目标站点",
            ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(24, 20)
        };
        var subtitle = new Label
        {
            Text = "MOVE",
            ForeColor = Color.FromArgb(210, 229, 255),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(166, 25)
        };
        var close = new Button
        {
            Text = "×", Dock = DockStyle.Right, Width = 48, FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White, BackColor = Primary, Font = new Font("Segoe UI", 18F),
            Cursor = Cursors.Hand, TabStop = false
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(24, 91, 190);
        close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        header.Controls.Add(close); header.Controls.Add(title); header.Controls.Add(subtitle);
        header.MouseDown += BeginDrag; title.MouseDown += BeginDrag;
        header.MouseMove += ContinueDrag; title.MouseMove += ContinueDrag;
        return header;
    }

    private Control CreateContent()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Background };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.Controls.Add(new Label { Text = "请选择机器人要移动到的站点", ForeColor = Muted, AutoSize = true }, 0, 0);

        _positions.Dock = DockStyle.Fill;
        _positions.DropDownStyle = ComboBoxStyle.DropDownList;
        _positions.FlatStyle = FlatStyle.Flat;
        _positions.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        _positions.IntegralHeight = false;
        _positions.DropDownHeight = 220;
        root.Controls.Add(_positions, 0, 1);

        var poseCard = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 12), Padding = new Padding(18, 14, 18, 12),
            BackColor = Surface, ColumnCount = 4, RowCount = 2
        };
        for (var i = 0; i < 4; i++) poseCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        poseCard.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        poseCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        AddMetric(poseCard, 0, "X 坐标", _xValue);
        AddMetric(poseCard, 1, "Y 坐标", _yValue);
        AddMetric(poseCard, 2, "朝向", _yawValue);
        AddMetric(poseCard, 3, "地图", _mapValue);
        root.Controls.Add(poseCard, 0, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var confirm = CreateButton("确定发送", Primary, Color.White, 112);
        confirm.DialogResult = DialogResult.OK;
        var cancel = CreateButton("取消", Color.White, Color.FromArgb(51, 65, 85), 88);
        cancel.FlatAppearance.BorderSize = 1; cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(confirm); actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 3);
        AcceptButton = confirm; CancelButton = cancel;
        return root;
    }

    private static void AddMetric(TableLayoutPanel panel, int column, string caption, Label value)
    {
        panel.Controls.Add(new Label { Text = caption, ForeColor = Muted, AutoSize = true, Anchor = AnchorStyles.Left }, column, 0);
        panel.Controls.Add(value, column, 1);
    }

    private static Label ValueLabel() => new()
    {
        AutoSize = true, Anchor = AnchorStyles.Left,
        ForeColor = Color.FromArgb(15, 23, 42),
        Font = new Font("Segoe UI", 13F, FontStyle.Bold)
    };

    private static Button CreateButton(string text, Color background, Color foreground, int width)
    {
        var button = new Button
        {
            Text = text, Width = width, Height = 38, Margin = new Padding(10, 5, 0, 5),
            FlatStyle = FlatStyle.Flat, BackColor = background, ForeColor = foreground,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void RefreshDetails()
    {
        if (_positions.SelectedItem is not PositionItem item) return;
        _xValue.Text = $"{item.X:0.###} mm";
        _yValue.Text = $"{item.Y:0.###} mm";
        _yawValue.Text = $"{item.Yaw:0.###}°";
        _mapValue.Text = item.Map;
    }

    private void ApplyRoundedRegion()
    {
        using var path = new GraphicsPath();
        const int radius = 18;
        path.AddArc(0, 0, radius, radius, 180, 90);
        path.AddArc(Width - radius, 0, radius, radius, 270, 90);
        path.AddArc(Width - radius, Height - radius, radius, radius, 0, 90);
        path.AddArc(0, Height - radius, radius, radius, 90, 90);
        path.CloseFigure();
        Region = new Region(path);
    }

    private void BeginDrag(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _dragOrigin = e.Location; }
    private void ContinueDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - _dragOrigin.X, Top + e.Y - _dragOrigin.Y);
    }

    public static IReadOnlyList<PositionItem> LoadPositions(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到服务器站点配置文件。", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<PositionItem>();
        foreach (var position in document.RootElement.GetProperty("positions").EnumerateArray())
        {
            var name = position.GetProperty("pointName").GetString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            // 新结构中 pose 直接属于位置，不再经过 ports/defaultPort。
            if (!position.TryGetProperty("pose", out var pose) || pose.ValueKind != JsonValueKind.Object) continue;
            var arrival = position.TryGetProperty("arrival", out var arrivalElement)
                ? new PositionArrivalItem(
                    arrivalElement.TryGetProperty("positionToleranceMm", out var xy) ? xy.GetDouble() : 5,
                    arrivalElement.TryGetProperty("angleToleranceDeg", out var angle) ? angle.GetDouble() : 5,
                    arrivalElement.TryGetProperty("timeoutMs", out var timeout) ? timeout.GetInt32() : 30_000)
                : new PositionArrivalItem(5, 5, 30_000);
            result.Add(new(name, pose.GetProperty("x").GetDouble(), pose.GetProperty("y").GetDouble(),
                pose.GetProperty("yaw").GetDouble(), pose.TryGetProperty("map", out var map) ? map.GetString() ?? "" : "",
                arrival, position.TryGetProperty("speed", out var speed) && speed.TryGetDouble(out var value) ? value : 0.5));
        }
        if (result.Count == 0) throw new InvalidDataException("MOVE.Templates.json 中没有可用点位。");
        return result.OrderBy(x => NaturalRank(x.Name)).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int NaturalRank(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var rank) ? rank : int.MaxValue;
    }
}

internal sealed record PositionItem(string Name, double X, double Y, double Yaw, string Map,
    PositionArrivalItem Arrival, double Speed);
internal sealed record PositionArrivalItem(double PositionToleranceMm, double AngleToleranceDeg, int TimeoutMs);
