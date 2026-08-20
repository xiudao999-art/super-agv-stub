using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Core.Controller.Templates;

namespace WinFormsApp1;

/// <summary>编辑单个 Phase；选择 SubAction 后自动生成该设备动作的完整参数骨架。</summary>
internal sealed class PhaseEditorDialog : Form
{
    private readonly TextBox _phaseId = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _subAction = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _enabled = new() { Text = "启用", Checked = true, AutoSize = true };
    private readonly CheckBox _gate = new() { Text = "Gate（失败阻断）", AutoSize = true };
    private readonly ComboBox _onFail = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _parameters = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both,
        AcceptsTab = true, Font = new Font("Consolas", 11F), WordWrap = false };
    private bool _loading;
    public PhaseDraft? Phase { get; private set; }

    public PhaseEditorDialog(int sequence, PhaseDraft? existing = null)
    {
        Text = existing is null ? "新建 SubAction Phase" : "编辑 SubAction Phase";
        StartPosition = FormStartPosition.CenterParent;
        // 参数 JSON 通常包含 pose/arrival 等多层对象，使用更大的默认编辑区域。
        ClientSize = new Size(960, 800);
        MinimumSize = new Size(820, 700);
        Font = new Font("Microsoft YaHei UI", 10F);
        _subAction.DataSource = Enum.GetValues<SubAction>();
        _subAction.Format += (_, e) => { if (e.ListItem is SubAction value) e.Value = value.ToProtocolName(); };
        _onFail.DataSource = Enum.GetValues<PhaseFailAction>();
        _subAction.SelectedIndexChanged += (_, _) => ApplySubActionDefaults();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 2, RowCount = 7 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        Add(root, 0, "PhaseId", _phaseId);
        Add(root, 1, "SubAction", _subAction);
        var checks = new FlowLayoutPanel { Dock = DockStyle.Fill }; checks.Controls.Add(_enabled); checks.Controls.Add(_gate);
        Add(root, 2, "执行属性", checks);
        Add(root, 3, "onFail", _onFail);
        root.Controls.Add(new Label { Text = "params（对应设备参数）", AutoSize = true }, 0, 4);
        root.SetColumnSpan(root.GetControlFromPosition(0, 4)!, 2);
        root.Controls.Add(_parameters, 0, 5); root.SetColumnSpan(_parameters, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "确定添加", Width = 110, Height = 36 };
        var cancel = new Button { Text = "取消", Width = 90, Height = 36, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Confirm(); buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 6); root.SetColumnSpan(buttons, 2); Controls.Add(root);

        _loading = true;
        if (existing is null)
        {
            _phaseId.Text = $"phase{sequence}";
            _subAction.SelectedItem = SubAction.MOVE_TO_MAP_POINT;
            _onFail.SelectedItem = PhaseFailAction.ABORT;
            _parameters.Text = Defaults(SubAction.MOVE_TO_MAP_POINT).ToJsonString(JsonOptions());
        }
        else
        {
            _phaseId.Text = existing.PhaseId; _subAction.SelectedItem = existing.SubAction;
            _enabled.Checked = existing.Enabled; _gate.Checked = existing.Gate; _onFail.SelectedItem = existing.OnFail;
            _parameters.Text = existing.Parameters.ToJsonString(JsonOptions());
        }
        _loading = false;
    }

    private void ApplySubActionDefaults()
    {
        if (_loading || _subAction.SelectedItem is not SubAction action) return;
        _parameters.Text = Defaults(action).ToJsonString(JsonOptions());
        _phaseId.Text = ToCamel(action.ToProtocolName().Replace('.', '_'));
        _gate.Checked = action is SubAction.MOVE_TO_MAP_POINT or SubAction.GRIP_VERIFY_LOAD or
            SubAction.VISION_VERIFY_MATERIAL or SubAction.VISION_VERIFY_PLACEMENT or
            SubAction.CHASSIS_VERIFY_STOPPED or SubAction.ARM_VERIFY_HOME;
    }

    private void Confirm()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_phaseId.Text)) throw new InvalidDataException("PhaseId 不能为空。");
            var parameters = JsonNode.Parse(_parameters.Text)?.AsObject()
                ?? throw new InvalidDataException("params 必须是 JSON 对象。");
            Phase = new PhaseDraft(_phaseId.Text.Trim(), (SubAction)_subAction.SelectedItem!,
                _enabled.Checked, parameters, _gate.Checked, (PhaseFailAction)_onFail.SelectedItem!);
            DialogResult = DialogResult.OK; Close();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Phase 参数错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static JsonObject Defaults(SubAction action) => action switch
    {
        SubAction.MOVE_TO_MAP_POINT => JsonNode.Parse("""{"pointName":"P01","port":null,"speed":0.5,"pose":{"x":12480,"y":8220,"yaw":90,"map":"LAB"},"arrival":{"positionToleranceMm":5,"angleToleranceDeg":5,"timeoutMs":30000}}""")!.AsObject(),
        SubAction.MOVE_TO_POSE => JsonNode.Parse("""{"station":"TEST_STATION","poseRole":"SAFE","point":"P01","pose":{"x":300,"y":0,"z":450,"rx":180,"ry":0,"rz":0},"positionToleranceMm":2,"angleToleranceDeg":1,"settleMs":200,"timeoutMs":10000,"pollMs":50,"frame":"BASE","speedProfile":"NORMAL","collisionProfile":"SAFE"}""")!.AsObject(),
        SubAction.GRIP_OPEN => JsonNode.Parse("""{"graspProfile":"DEFAULT_PICK","targetWidthMm":80,"holdMs":100,"emptyDetectedMinWidth":70}""")!.AsObject(),
        SubAction.GRIP_CLOSE => JsonNode.Parse("""{"graspProfile":"DEFAULT_PICK","gripForce":35,"targetWidthMm":25,"holdMs":150,"minDetectedWidth":5,"maxDetectedWidth":65}""")!.AsObject(),
        SubAction.GRIP_VERIFY_LOAD => JsonNode.Parse("""{"graspProfile":"DEFAULT_PICK","minDetectedWidth":5,"maxDetectedWidth":65,"holdCheckMs":500,"pollMs":50,"requireForceFeedback":true,"minForce":1,"expectedDetected":true}""")!.AsObject(),
        SubAction.VISION_VERIFY_MATERIAL => Vision("MATERIAL", true),
        SubAction.VISION_VERIFY_PLACEMENT => Vision("PLACEMENT", true),
        SubAction.VISION_CAPTURE => Vision("CAPTURE", true),
        SubAction.CHASSIS_VERIFY_STOPPED or SubAction.ARM_VERIFY_HOME => new JsonObject(),
        _ => new JsonObject()
    };

    private static JsonObject Vision(string recipe, bool pass) => JsonNode.Parse(
        $$"""{"station":"TEST_STATION","recipe":"{{recipe}}","cameraId":"CAM01","exposureMs":10,"gain":1,"timeoutMs":5000,"outputFormat":"png","simulatedPass":{{pass.ToString().ToLowerInvariant()}}}""")!.AsObject();
    private static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };
    private static string ToCamel(string value) => char.ToLowerInvariant(value[0]) + value[1..].ToLowerInvariant();
    private static void Add(TableLayoutPanel root, int row, string label, Control control)
    { root.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row); root.Controls.Add(control, 1, row); }
}
