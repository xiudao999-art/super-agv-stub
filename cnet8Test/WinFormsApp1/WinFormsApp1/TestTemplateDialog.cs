using System.Drawing.Drawing2D;
using System.Text.Json;

namespace WinFormsApp1;

/// <summary>从服务器 TestTemplates 目录选择一个完整 MainAction 测试文件。</summary>
internal sealed class TestTemplateDialog : Form
{
    private static readonly Color Primary = Color.FromArgb(31, 111, 235);
    private static readonly Color Background = Color.FromArgb(246, 248, 251);
    private readonly ComboBox _files = new();
    private readonly Label _actionType = ValueLabel();
    private readonly Label _templateId = ValueLabel();
    private readonly Label _phaseCount = ValueLabel();
    private readonly string _directory;
    private readonly string? _actionTypeFilter;
    private Button? _confirmButton;
    private Button? _editButton;
    private Point _dragOrigin;

    public TestTemplateFile? SelectedTemplate => _files.SelectedItem as TestTemplateFile;

    public TestTemplateDialog(string directory, IReadOnlyList<TestTemplateFile> templates,
        string? actionTypeFilter = null)
    {
        _directory = directory;
        _actionTypeFilter = actionTypeFilter;
        Text = actionTypeFilter is null ? "选择测试 MainAction" : $"选择 {actionTypeFilter} MainAction";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Background;
        // 为长文件名和 TEST.* TemplateId 预留足够空间；在 125%/150% DPI 下也不截字。
        ClientSize = new Size(780, 470);
        MinimumSize = ClientSize;
        Font = new Font("Microsoft YaHei UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var header = CreateHeader();
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(30, 24, 30, 22), BackColor = Background };
        content.Controls.Add(CreateContent());
        Controls.Add(content);
        Controls.Add(header);

        BindTemplates(templates);
        _files.SelectedIndexChanged += (_, _) => RefreshDetails();
        RefreshDetails();
        Resize += (_, _) => ApplyRoundedRegion();
        Shown += (_, _) => ApplyRoundedRegion();
    }

    private Panel CreateHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Primary, Padding = new Padding(24, 0, 12, 0) };
        var titleText = _actionTypeFilter is null ? "选择测试 MainAction" : $"选择 {_actionTypeFilter} 模板";
        var title = new Label { Text = titleText, ForeColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold), AutoSize = true, Location = new Point(24, 20) };
        var close = new Button { Text = "×", Dock = DockStyle.Right, Width = 48, FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White, BackColor = Primary, Font = new Font("Segoe UI", 18F), Cursor = Cursors.Hand };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        header.Controls.Add(close);
        header.Controls.Add(title);
        header.MouseDown += BeginDrag;
        title.MouseDown += BeginDrag;
        header.MouseMove += ContinueDrag;
        title.MouseMove += ContinueDrag;
        return header;
    }

    private Control CreateContent()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Background };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        var prompt = _actionTypeFilter is null
            ? "请选择 TestTemplates 文件夹中的测试文件"
            : $"请选择 TestTemplates 中的 {_actionTypeFilter} 模板";
        root.Controls.Add(new Label { Text = prompt, ForeColor = Color.FromArgb(100, 116, 139), AutoSize = true }, 0, 0);

        _files.Dock = DockStyle.Fill;
        _files.DropDownStyle = ComboBoxStyle.DropDownList;
        _files.FlatStyle = FlatStyle.Flat;
        _files.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        _files.DropDownHeight = 220;
        root.Controls.Add(_files, 0, 1);

        var card = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 12),
            Padding = new Padding(18, 14, 18, 12), BackColor = Color.White, ColumnCount = 3, RowCount = 2 };
        for (var i = 0; i < 3; i++) card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33F));
        card.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        AddMetric(card, 0, "ActionType", _actionType);
        AddMetric(card, 1, "TemplateId", _templateId);
        AddMetric(card, 2, "Phase 数量", _phaseCount);
        root.Controls.Add(card, 0, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var confirm = CreateButton("确定发送", Primary, Color.White, 112); confirm.DialogResult = DialogResult.OK;
        _confirmButton = confirm;
        var cancel = CreateButton("取消", Color.White, Color.FromArgb(51, 65, 85), 88);
        cancel.FlatAppearance.BorderSize = 1; cancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
        cancel.DialogResult = DialogResult.Cancel;
        var create = CreateButton("新建模板", Color.FromArgb(16, 185, 129), Color.White, 112);
        create.Click += (_, _) => CreateTemplate();
        var edit = CreateButton("编辑模板", Color.FromArgb(245, 158, 11), Color.White, 112);
        _editButton = edit;
        edit.Click += (_, _) => EditTemplate();
        actions.Controls.Add(confirm); actions.Controls.Add(cancel); actions.Controls.Add(edit); actions.Controls.Add(create);
        root.Controls.Add(actions, 0, 3);
        AcceptButton = confirm; CancelButton = cancel;
        return root;
    }

    private void CreateTemplate()
    {
        using var dialog = new MainActionTemplateEditorDialog(_directory, initialActionType: _actionTypeFilter);
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SavedPath)) return;
        var templates = FilterTemplates(LoadFiles(_directory));
        BindTemplates(templates);
        _files.SelectedItem = templates.FirstOrDefault(x =>
            x.FullPath.Equals(dialog.SavedPath, StringComparison.OrdinalIgnoreCase));
        RefreshDetails();
    }

    private void EditTemplate()
    {
        if (SelectedTemplate is not { } selected)
        {
            MessageBox.Show("请先选择需要编辑的模板。", "编辑模板",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new MainActionTemplateEditorDialog(_directory, selected.FullPath);
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SavedPath)) return;
        var templates = FilterTemplates(LoadFiles(_directory));
        BindTemplates(templates);
        _files.SelectedItem = templates.FirstOrDefault(x =>
            x.FullPath.Equals(dialog.SavedPath, StringComparison.OrdinalIgnoreCase));
        RefreshDetails();
    }

    private IReadOnlyList<TestTemplateFile> FilterTemplates(IReadOnlyList<TestTemplateFile> templates) =>
        _actionTypeFilter is null
            ? templates
            : templates.Where(x => string.Equals(x.ActionType, _actionTypeFilter,
                StringComparison.OrdinalIgnoreCase)).ToArray();

    private void BindTemplates(IReadOnlyList<TestTemplateFile> templates)
    {
        _files.DataSource = null;
        _files.DisplayMember = nameof(TestTemplateFile.FileName);
        _files.DataSource = templates.ToArray();
        UpdateAvailability();
    }

    private void RefreshDetails()
    {
        UpdateAvailability();
        if (SelectedTemplate is not { } item)
        {
            _actionType.Text = "-";
            _templateId.Text = "-";
            _phaseCount.Text = "0";
            _phaseCount.ForeColor = Color.FromArgb(148, 163, 184);
            return;
        }
        _actionType.Text = item.ActionType;
        _templateId.Text = item.TemplateId;
        _phaseCount.Text = item.PhaseCount.ToString();
        _phaseCount.ForeColor = item.PhaseCount > 0 ? Color.FromArgb(15, 23, 42) : Color.Firebrick;
    }

    /// <summary>
    /// 空目录属于正常的首次使用状态。此时只能新建或取消，不能编辑和发送。
    /// 新建模板成功并重新绑定列表后，相关控件会自动恢复可用。
    /// </summary>
    private void UpdateAvailability()
    {
        var hasTemplate = SelectedTemplate is not null;
        _files.Enabled = hasTemplate;
        if (_editButton is not null) _editButton.Enabled = hasTemplate;
        if (_confirmButton is not null) _confirmButton.Enabled = hasTemplate;
        AcceptButton = hasTemplate ? _confirmButton : null;
    }

    public static IReadOnlyList<TestTemplateFile> LoadFiles(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"找不到测试模板目录：{directory}");
        var result = new List<TestTemplateFile>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var templates = document.RootElement.GetProperty("actionTemplates");
                if (templates.GetArrayLength() != 1) continue;
                var template = templates[0];
                result.Add(new(Path.GetFileName(path), path,
                    template.TryGetProperty("templateId", out var id) ? id.GetString() ?? "-" : "-",
                    template.TryGetProperty("actionType", out var type) ? type.GetString() ?? "-" : "-",
                    template.TryGetProperty("phases", out var phases) && phases.ValueKind == JsonValueKind.Array
                        ? phases.GetArrayLength() : 0));
            }
            catch (JsonException) { /* 无效 JSON 不进入下拉框。 */ }
        }
        return result.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddMetric(TableLayoutPanel panel, int column, string caption, Label value)
    {
        panel.Controls.Add(new Label { Text = caption, ForeColor = Color.FromArgb(100, 116, 139), AutoSize = true }, column, 0);
        panel.Controls.Add(value, column, 1);
    }

    private static Label ValueLabel() => new()
    {
        // 固定在详情单元格中并允许自动换行，长 TemplateId 不会被裁掉。
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.TopLeft,
        Padding = new Padding(0, 2, 8, 0),
        ForeColor = Color.FromArgb(15, 23, 42),
        Font = new Font("Segoe UI", 11F, FontStyle.Bold)
    };

    private static Button CreateButton(string text, Color background, Color foreground, int width)
    {
        var button = new Button { Text = text, Width = width, Height = 38, Margin = new Padding(10, 5, 0, 5),
            FlatStyle = FlatStyle.Flat, BackColor = background, ForeColor = foreground,
            Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    private void ApplyRoundedRegion()
    {
        using var path = new GraphicsPath();
        const int radius = 18;
        path.AddArc(0, 0, radius, radius, 180, 90); path.AddArc(Width - radius, 0, radius, radius, 270, 90);
        path.AddArc(Width - radius, Height - radius, radius, radius, 0, 90); path.AddArc(0, Height - radius, radius, radius, 90, 90);
        path.CloseFigure(); Region = new Region(path);
    }

    private void BeginDrag(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) _dragOrigin = e.Location; }
    private void ContinueDrag(object? sender, MouseEventArgs e)
    { if (e.Button == MouseButtons.Left) Location = new Point(Left + e.X - _dragOrigin.X, Top + e.Y - _dragOrigin.Y); }
}

internal sealed record TestTemplateFile(string FileName, string FullPath, string TemplateId,
    string ActionType, int PhaseCount);
