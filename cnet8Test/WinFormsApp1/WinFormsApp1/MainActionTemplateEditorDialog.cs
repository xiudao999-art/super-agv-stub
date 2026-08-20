using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Actions.ServerActions;
using Kunling.RobotClient.Core.Controller.Templates;

namespace WinFormsApp1;

/// <summary>可视化创建由任意有序 Phase 组成的测试 MainAction。</summary>
internal sealed class MainActionTemplateEditorDialog : Form
{
    private readonly string _directory;
    private readonly string? _editingPath;
    private readonly ComboBox _actionType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly TextBox _templateName = new() { Width = 300, Text = "mainaction" };
    private readonly ListBox _phases = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private readonly List<PhaseDraft> _items = [];
    public string? SavedPath { get; private set; }

    public MainActionTemplateEditorDialog(string directory, string? editingPath = null,
        string? initialActionType = null)
    {
        _directory = directory;
        _editingPath = editingPath;
        Text = editingPath is null ? "新建 MainAction 测试模板" : "编辑 MainAction 测试模板";
        StartPosition = FormStartPosition.CenterParent;
        // 放大主模板编辑区，长 SubAction 名称、gate/onFail 信息和多 Phase 列表可完整显示。
        ClientSize = new Size(1040, 760);
        MinimumSize = new Size(900, 660);
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(246, 248, 251);

        _actionType.DataSource = MainActionCatalog.All.Select(x => x.ActionType).ToArray();
        if (editingPath is null && !string.IsNullOrWhiteSpace(initialActionType))
            _actionType.SelectedItem = initialActionType;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 5 };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        root.Controls.Add(FieldRow("MainAction 类型", _actionType), 0, 0);
        root.Controls.Add(FieldRow("模板名称", _templateName), 0, 1);
        root.Controls.Add(BuildPhaseToolbar(), 0, 2);
        root.Controls.Add(_phases, 0, 3);
        root.Controls.Add(BuildButtons(), 0, 4);
        Controls.Add(root);

        if (editingPath is not null) LoadTemplate(editingPath);
    }

    private void LoadTemplate(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到待编辑模板。", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var templates = document.RootElement.GetProperty("actionTemplates");
        if (templates.GetArrayLength() != 1)
            throw new InvalidDataException("待编辑文件必须且只能包含一个 MainAction。");
        var template = templates[0].Deserialize<MainActionTemplate>(ServerActionJson.Default)
            ?? throw new InvalidDataException("待编辑 MainAction 无法解析。");
        _actionType.SelectedItem = template.ActionType.ToActionType();
        _templateName.Text = ReadTemplateName(template.TemplateId);
        _items.Clear();
        _items.AddRange(template.Phases.Select(x => new PhaseDraft(
            x.PhaseId, x.SubAction, x.Enabled,
            x.Parameters?.DeepClone().AsObject() ?? new JsonObject(), x.Gate, x.OnFail)));
        RefreshPhases(_items.Count > 0 ? 0 : -1);
    }

    private Control BuildPhaseToolbar()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        bar.Controls.Add(Button("添加 Phase", (_, _) => AddPhase()));
        bar.Controls.Add(Button("编辑", (_, _) => EditPhase()));
        bar.Controls.Add(Button("删除", (_, _) => RemovePhase()));
        bar.Controls.Add(Button("上移", (_, _) => MovePhase(-1)));
        bar.Controls.Add(Button("下移", (_, _) => MovePhase(1)));
        return bar;
    }

    private Control BuildButtons()
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = Button("保存模板", (_, _) => SaveTemplate()); save.Width = 120;
        var cancel = Button("取消", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        bar.Controls.Add(save); bar.Controls.Add(cancel);
        return bar;
    }

    private void AddPhase()
    {
        using var dialog = new PhaseEditorDialog(_items.Count + 1);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Phase is null) return;
        _items.Add(dialog.Phase);
        RefreshPhases(_items.Count - 1);
    }

    private void EditPhase()
    {
        if (_phases.SelectedIndex < 0) return;
        var index = _phases.SelectedIndex;
        using var dialog = new PhaseEditorDialog(index + 1, _items[index]);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Phase is null) return;
        _items[index] = dialog.Phase;
        RefreshPhases(index);
    }

    private void RemovePhase()
    {
        if (_phases.SelectedIndex < 0) return;
        var index = _phases.SelectedIndex;
        _items.RemoveAt(index);
        RefreshPhases(Math.Min(index, _items.Count - 1));
    }

    private void MovePhase(int offset)
    {
        var source = _phases.SelectedIndex;
        var target = source + offset;
        if (source < 0 || target < 0 || target >= _items.Count) return;
        (_items[source], _items[target]) = (_items[target], _items[source]);
        RefreshPhases(target);
    }

    private void RefreshPhases(int selected)
    {
        _phases.DataSource = null;
        _phases.DataSource = _items.Select((x, i) =>
            $"{i + 1:00}  {x.PhaseId}  |  {x.SubAction.ToProtocolName()}  |  gate={x.Gate}  |  onFail={x.OnFail}").ToArray();
        if (selected >= 0 && selected < _items.Count) _phases.SelectedIndex = selected;
    }

    private void SaveTemplate()
    {
        try
        {
            var duplicated = _items.GroupBy(x => x.PhaseId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
            if (duplicated is not null) throw new InvalidDataException($"PhaseId 重复：{duplicated.Key}");
            var actionType = _actionType.Text;
            var name = Sanitize(string.IsNullOrWhiteSpace(_templateName.Text) ? "mainaction" : _templateName.Text);
            var timestamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
            var templateId = $"TEST.{actionType.Replace('.', '_')}.{name}.{timestamp}";
            var template = new MainActionTemplate
            {
                TemplateId = templateId,
                ActionType = MainActionCatalog.Parse(actionType),
                Phases = _items.Select(x => x.ToTemplate()).ToList()
            };
            // TEST 模板允许 phases=[]，用于验证机器人客户端对空 MainAction 的拒绝、
            // 状态上报和错误码处理。存在 Phase 时仍执行正常的 Phase 结构校验。
            if (template.Phases.Count > 0)
                MainActionTemplateValidator.EnsureValid(template);
            var document = new JsonObject
            {
                ["actionTemplates"] = new JsonArray(JsonSerializer.SerializeToNode(template, ServerActionJson.Default))
            };
            Directory.CreateDirectory(_directory);
            // 测试模板文件名统一由 MainAction 类型决定，不再使用用户填写的模板名称。
            // 示例：MOVE -> test.move.20260820134359025.json
            var actionFilePart = SanitizeActionType(actionType);
            var fileName = $"test.{actionFilePart}.{timestamp}.json";
            // 编辑模式覆盖当前文件；新建模式才按 MainAction + 时间戳产生新文件。
            SavedPath = _editingPath ?? Path.Combine(_directory, fileName);
            File.WriteAllText(SavedPath, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray();
        return new string(chars).Trim('_').ToLowerInvariant() is { Length: > 0 } result ? result : "mainaction";
    }

    private static string SanitizeActionType(string actionType)
    {
        // 保留动作协议名中的点，例如 ARM.PICK -> arm.pick；只替换文件系统非法字符。
        var invalid = Path.GetInvalidFileNameChars();
        var chars = actionType.Trim().Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray();
        return new string(chars).Trim('_').ToLowerInvariant();
    }

    private static string ReadTemplateName(string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return "mainaction";
        var parts = templateId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        // TEST.<ACTION>.<NAME>.<TIMESTAMP> 中优先取时间戳前一段；旧格式同样可编辑。
        return parts.Length >= 2 ? parts[^2].ToLowerInvariant() : "mainaction";
    }

    private static Control FieldRow(string label, Control control)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        row.Controls.Add(new Label { Text = label, Width = 150, TextAlign = ContentAlignment.MiddleLeft, Height = 30 });
        row.Controls.Add(control); return row;
    }

    private static Button Button(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 34, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 3, 8, 3) };
        button.Click += click; return button;
    }
}

internal sealed record PhaseDraft(string PhaseId, SubAction SubAction, bool Enabled,
    JsonObject Parameters, bool Gate, PhaseFailAction OnFail)
{
    public PhaseActionTemplate ToTemplate() => new()
    {
        PhaseId = PhaseId, SubAction = SubAction, Enabled = Enabled,
        Parameters = Parameters.DeepClone().AsObject(), Gate = Gate, OnFail = OnFail
    };
}
