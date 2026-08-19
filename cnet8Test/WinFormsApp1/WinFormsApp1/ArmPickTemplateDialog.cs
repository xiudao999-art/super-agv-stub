using System.Drawing.Drawing2D;
using System.Text.Json;

namespace WinFormsApp1;

/// <summary>ARM.PICK 模板选择窗口。模板列表直接来自 ARM.PICK.Templates.json。</summary>
internal sealed class ArmPickTemplateDialog : Form
{
    private static readonly Color Primary = Color.FromArgb(31, 111, 235);
    private static readonly Color Background = Color.FromArgb(246, 248, 251);
    private static readonly Color Muted = Color.FromArgb(100, 116, 139);
    private readonly ComboBox _templates = new();
    private readonly Label _actionValue = ValueLabel();
    private readonly Label _phaseValue = ValueLabel();
    private readonly Label _sequenceValue = new()
    {
        AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(51, 65, 85),
        Font = new Font("Microsoft YaHei UI", 9.5F), TextAlign = ContentAlignment.MiddleLeft
    };
    private Point _dragOrigin;

    public ArmPickTemplateItem? SelectedTemplate => _templates.SelectedItem as ArmPickTemplateItem;

    public ArmPickTemplateDialog(IReadOnlyList<ArmPickTemplateItem> templates)
    {
        Text = "选择 ARM.PICK 动作模板";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Background;
        ClientSize = new Size(620, 400);
        MinimumSize = ClientSize;
        Font = new Font("Microsoft YaHei UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = CreateHeader();
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 24, 28, 22), BackColor = Background };
        content.Controls.Add(CreateContent());
        Controls.Add(content);
        Controls.Add(header);

        _templates.DataSource = templates.ToArray();
        _templates.DisplayMember = nameof(ArmPickTemplateItem.TemplateId);
        _templates.SelectedIndexChanged += (_, _) => RefreshDetails();
        RefreshDetails();
        Resize += (_, _) => ApplyRoundedRegion();
        Shown += (_, _) => ApplyRoundedRegion();
    }

    private Panel CreateHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Primary, Padding = new Padding(24, 0, 12, 0) };
        var title = new Label
        {
            Text = "选择取料动作模板", ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold), AutoSize = true, Location = new Point(24, 20)
        };
        var subtitle = new Label
        {
            Text = "ARM.PICK", ForeColor = Color.FromArgb(210, 229, 255),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold), AutoSize = true, Location = new Point(210, 25)
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
        root.Controls.Add(new Label { Text = "请选择服务器要下发给机器人的取料模板", ForeColor = Muted, AutoSize = true }, 0, 0);

        _templates.Dock = DockStyle.Fill;
        _templates.DropDownStyle = ComboBoxStyle.DropDownList;
        _templates.FlatStyle = FlatStyle.Flat;
        _templates.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        _templates.IntegralHeight = false;
        _templates.DropDownHeight = 220;
        root.Controls.Add(_templates, 0, 1);

        var card = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 12), Padding = new Padding(18, 14, 18, 12),
            BackColor = Color.White, ColumnCount = 2, RowCount = 3
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        card.Controls.Add(new Label { Text = "Action 类型", ForeColor = Muted, AutoSize = true }, 0, 0);
        card.Controls.Add(new Label { Text = "Phase 数量", ForeColor = Muted, AutoSize = true }, 1, 0);
        card.Controls.Add(_actionValue, 0, 1);
        card.Controls.Add(_phaseValue, 1, 1);
        card.Controls.Add(_sequenceValue, 0, 2);
        card.SetColumnSpan(_sequenceValue, 2);
        root.Controls.Add(card, 0, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var confirm = CreateButton("确定发送", Primary, Color.White, 112); confirm.DialogResult = DialogResult.OK;
        var cancel = CreateButton("取消", Color.White, Color.FromArgb(51, 65, 85), 88);
        cancel.FlatAppearance.BorderSize = 1; cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(confirm); actions.Controls.Add(cancel);
        root.Controls.Add(actions, 0, 3);
        AcceptButton = confirm; CancelButton = cancel;
        return root;
    }

    private void RefreshDetails()
    {
        if (SelectedTemplate is not { } item) return;
        _actionValue.Text = item.ActionType;
        _phaseValue.Text = item.PhaseCount.ToString();
        _sequenceValue.Text = "执行顺序：" + item.PhaseSequence;
    }

    public static IReadOnlyList<ArmPickTemplateItem> LoadTemplates(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 ARM.PICK 模板配置文件。", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var result = new List<ArmPickTemplateItem>();
        foreach (var template in document.RootElement.GetProperty("actionTemplates").EnumerateArray())
        {
            var actionType = template.TryGetProperty("actionType", out var action) ? action.GetString() : null;
            if (!string.Equals(actionType, "ARM.PICK", StringComparison.OrdinalIgnoreCase)) continue;
            var templateId = template.TryGetProperty("templateId", out var id) ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(templateId))
                throw new InvalidDataException("ARM.PICK 模板的 templateId 不能为空。");
            var phases = template.GetProperty("phases").EnumerateArray().ToArray();
            var sequence = string.Join(" → ", phases.Select(x => x.GetProperty("phaseId").GetString()));
            result.Add(new(templateId, actionType!, phases.Length, sequence));
        }
        if (result.Count == 0) throw new InvalidDataException("配置中没有可用的 ARM.PICK 模板。");
        var duplicate = result.GroupBy(x => x.TemplateId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"templateId 重复：{duplicate.Key}");
        return result.OrderBy(x => x.TemplateId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Label ValueLabel() => new()
    {
        AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Color.FromArgb(15, 23, 42),
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

    private void ApplyRoundedRegion()
    {
        using var path = new GraphicsPath();
        const int radius = 18;
        path.AddArc(0, 0, radius, radius, 180, 90);
        path.AddArc(Width - radius, 0, radius, radius, 270, 90);
        path.AddArc(Width - radius, Height - radius, radius, radius, 0, 90);
        path.AddArc(0, Height - radius, radius, radius, 90, 90);
        path.CloseFigure(); Region = new Region(path);
    }

    private void BeginDrag(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _dragOrigin = e.Location; }
    private void ContinueDrag(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - _dragOrigin.X, Top + e.Y - _dragOrigin.Y);
    }
}

internal sealed record ArmPickTemplateItem(string TemplateId, string ActionType, int PhaseCount, string PhaseSequence);
