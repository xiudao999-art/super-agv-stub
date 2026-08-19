using System.Text.Json;
using System.Text.Json.Nodes;
using Kunling.RobotClient.Actions.ServerActions;
using Kunling.RobotClient.Core.Controller.Actions;
using Kunling.RobotClient.Core.Controller.Templates;
using Kunling.RobotClient.Core.Models;
using WinFormsApp1.Net;

namespace WinFormsApp1;

public partial class Form1 : Form
{
    private readonly TcpServer _server = new();
    private readonly System.Windows.Forms.Timer _stateRefreshTimer;

    public Form1()
    {
        InitializeComponent();
        // 服务端会持续接收机器人的PING状态；界面固定每3秒重新读取会话快照。
        // 使用WinForms Timer保证Tick运行在UI线程，不需要跨线程操作DataGridView。
        // 不传入 Designer 的 components 容器，避免设计器容器尚未创建或被重置时抛出
        // ArgumentNullException。该 Timer 由窗体在关闭时显式释放。
        _stateRefreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _stateRefreshTimer.Tick += (_, _) => RefreshRobots();
        _stateRefreshTimer.Start();
        _server.Log += (_, message) => AppendLog(message);
        _server.RobotsChanged += (_, _) => RefreshRobots();
        _server.ServerStopped += (_, _) => SetRunning(false);
        cboAction.Items.AddRange(["MOVE", "ARM.PICK", "ARM.PLACE", "ARM.PICK_BATCH", "ARM.PLACE_BATCH", "ARM.HOME", "VISION.CAPTURE"]);
        cboAction.SelectedIndexChanged += (_, _) => SetActionInputExample();
        cboAction.SelectedIndex = 0;
        Shown += (_, _) => btnStart.PerformClick();
    }

    private void btnStart_Click(object? sender, EventArgs e)
    {
        if (!int.TryParse(txtPort.Text, out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show("请输入 1-65535 范围内的端口。", "端口错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { _server.Start(port); SetRunning(true); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async void btnStop_Click(object? sender, EventArgs e) => await _server.StopAsync();

    private async void btnSend_Click(object? sender, EventArgs e)
    {
        if (cboRobot.SelectedItem is not RobotSessionInfo robot)
        {
            MessageBox.Show("请先选择在线机器人。");
            return;
        }

        try
        {
            // MOVE 必须先由用户从服务器站点档案中确认目标，取消窗口时不发送命令。
            if (cboAction.Text == "MOVE")
            {
                // MOVE 点位统一读取简化后的 MOVE.Templates.json，不再使用旧 position.json。
                var path = Path.Combine(AppContext.BaseDirectory, "Configs", "MOVE.Templates.json");
                var positions = MovePositionDialog.LoadPositions(path);
                using var dialog = new MovePositionDialog(positions);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var point = dialog.SelectedPositionItem
                    ?? throw new InvalidOperationException("没有选择 MOVE 点位。");
                var moveAction = new MoveAction(new MoveRequest(
                    point.Name,
                    point.Speed,
                    new RobotPose(point.X, point.Y, point.Yaw, point.Map),
                    new MoveArrivalRequest(point.Arrival.PositionToleranceMm,
                        point.Arrival.AngleToleranceDeg, point.Arrival.TimeoutMs)));
                // Input 中序列化 { MainAction: { actionType, phases } }；具体参数已经写入 phase.params。
                txtInput.Text = JsonSerializer.Serialize(
                    new MoveActionMessage(moveAction), ServerActionJson.Default);
            }
            else if (cboAction.Text == "ARM.PICK")
            {
                var path = Path.Combine(AppContext.BaseDirectory, "Configs", "ARM.PICK.Templates.json");
                var templates = ArmPickTemplateDialog.LoadTemplates(path);
                using var dialog = new ArmPickTemplateDialog(templates);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var selected = dialog.SelectedTemplate
                    ?? throw new InvalidOperationException("没有选择 ARM.PICK 模板。");
                txtInput.Text = BuildMainAction("ARM.PICK", "ARM.PICK.Templates.json", txtInput.Text,
                    selected.TemplateId);
            }
            else if (cboAction.Text == "ARM.PLACE")
            {
                txtInput.Text = BuildMainAction("ARM.PLACE", "ARM.PLACE.Templates.json", txtInput.Text);
            }
            else if (cboAction.Text == "ARM.HOME")
            {
                txtInput.Text = BuildMainAction("ARM.HOME", "ARM.HOME.Templates.json", txtInput.Text);
            }
            else if (cboAction.Text == "VISION.CAPTURE")
            {
                txtInput.Text = BuildMainAction("VISION.CAPTURE", "VISION.CAPTURE.Templates.json", txtInput.Text);
            }
            else if (cboAction.Text == "ARM.PICK_BATCH")
            {
                txtInput.Text = BuildBatchMainAction(pick: true, txtInput.Text);
            }
            else if (cboAction.Text == "ARM.PLACE_BATCH")
            {
                txtInput.Text = BuildBatchMainAction(pick: false, txtInput.Text);
            }

            using var _ = JsonDocument.Parse(txtInput.Text);
            var actionId = await _server.SendCommandAsync(robot.RobotId, cboAction.Text, "1.0", ExecutionMode.Package,
                txtInput.Text, (int)numTimeout.Value);
            AppendLog($"命令已发送，actionInstanceId={actionId}，JSON={txtInput.Text.Replace(Environment.NewLine, " ")}");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "下发失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    /// <summary>
    /// 读取 ARM.PICK.Templates.json，并把旧输入中的 station/point/graspProfile
    /// 写进每个 phase.params，最终只发送 { "MainAction": { actionType, phases } }。
    /// </summary>
    private static string BuildMainAction(string actionType, string templateFile, string requestJson,
        string? templateId = null)
    {
        using var request = JsonDocument.Parse(string.IsNullOrWhiteSpace(requestJson) ? "{}" : requestJson);
        var station = ReadString(request.RootElement, "station") ??
            (actionType == "ARM.PLACE" ? "PLACE_01" : actionType == "ARM.PICK" ? "PICK_01" : "GLOBAL");
        var point = ReadString(request.RootElement, "point");
        var graspProfile = ReadString(request.RootElement, "graspProfile") ?? "DEFAULT_PICK";
        var releaseProfile = ReadString(request.RootElement, "releaseProfile") ?? "DEFAULT_PLACE";
        var actionPolicy = ReadString(request.RootElement, "actionPolicy") ?? "SAFE_DEFAULT";
        var expectedMaterial = ReadString(request.RootElement, "expectedMaterial");

        var path = Path.Combine(AppContext.BaseDirectory, "Configs", templateFile);
        if (!File.Exists(path)) throw new FileNotFoundException($"找不到 {actionType} 动作模板配置。", path);
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
            ?? throw new InvalidDataException($"{templateFile} 内容为空。");
        var templates = root["actionTemplates"]?.AsArray()
            ?? throw new InvalidDataException($"{templateFile} 缺少 actionTemplates。");
        var mainAction = templates.OfType<JsonObject>().FirstOrDefault(x =>
            string.Equals(x["actionType"]?.GetValue<string>(), actionType, StringComparison.OrdinalIgnoreCase) &&
            (templateId is null || string.Equals(x["templateId"]?.GetValue<string>(), templateId,
                StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidDataException(templateId is null
                ? $"模板中未找到 {actionType}。"
                : $"模板中未找到 templateId={templateId} 的 {actionType}。");

        // 服务端先完成 StationProfile → graspProfile → actionPolicy → recipe 的组合解析，
        // 再应用本次请求覆盖，客户端只接收已经解析完成的 MainAction。
        var configuration = ActionConfigurationCatalog.Load(
            Path.Combine(AppContext.BaseDirectory, "Configs", "ActionConfiguration.json"));
        var typedTemplate = mainAction.Deserialize<MainActionTemplate>(ServerActionJson.Default)
            ?? throw new InvalidDataException($"{actionType} 模板反序列化失败。");
        var profileName = actionType == "ARM.PLACE" ? releaseProfile : graspProfile;
        var resolvedTemplate = ActionConfigurationResolver.Resolve(typedTemplate, configuration,
            station, profileName, actionPolicy);
        mainAction = JsonSerializer.SerializeToNode(resolvedTemplate, ServerActionJson.Default)?.AsObject()
            ?? throw new InvalidDataException($"{actionType} 组合解析失败。");

        foreach (var phase in mainAction["phases"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var parameters = phase["params"] as JsonObject ?? new JsonObject();
            phase["params"] = parameters;
            parameters["station"] = station;
            if (point is not null) parameters["point"] = point;
            var subAction = phase["subAction"]?.GetValue<string>();
            if (actionType == "ARM.PICK" && subAction is "GRIP.OPEN" or "GRIP.CLOSE" or "GRIP.VERIFY_LOAD")
                parameters["graspProfile"] = graspProfile;
            if (actionType == "ARM.PICK" && subAction == "VISION.VERIFY_MATERIAL" && expectedMaterial is not null)
                parameters["expectedMaterial"] = expectedMaterial;
            if (actionType == "ARM.PLACE" && subAction is "GRIP.OPEN" or "GRIP.VERIFY_LOAD")
                parameters["releaseProfile"] = releaseProfile;

            // 拍照请求允许覆盖模板的相机档案字段；未提供的字段继续使用模板默认值。
            if (actionType == "VISION.CAPTURE")
                foreach (var property in request.RootElement.EnumerateObject())
                    parameters[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        return new JsonObject { ["MainAction"] = mainAction.DeepClone() }
            .ToJsonString(ServerActionJson.Default);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// 服务端根据 slots 和单次 PICK/PLACE 模板展开批量 phases，发送端不再下发旧的裸请求。
    /// 展开结果保证 SAFE/APPROACH 一次、槽位核心序列 N 次、RETREAT 一次。
    /// </summary>
    private static string BuildBatchMainAction(bool pick, string requestJson)
    {
        var templateFile = pick ? "ARM.PICK.Templates.json" : "ARM.PLACE.Templates.json";
        var singleActionType = pick ? "ARM.PICK" : "ARM.PLACE";
        var path = Path.Combine(AppContext.BaseDirectory, "Configs", templateFile);
        var catalog = ActionTemplateLoader.LoadMany(path);
        var singleTemplate = catalog.Resolve(singleActionType);

        MainActionTemplate batchAction;
        if (pick)
        {
            var request = JsonSerializer.Deserialize<ArmPickBatchRequest>(requestJson, ServerActionJson.Default)
                ?? throw new InvalidDataException("ARM.PICK_BATCH 请求不能为空。");
            var configuration = ActionConfigurationCatalog.Load(
                Path.Combine(AppContext.BaseDirectory, "Configs", "ActionConfiguration.json"));
            singleTemplate = ActionConfigurationResolver.Resolve(singleTemplate, configuration,
                request.Station, "DEFAULT_PICK", "SAFE_DEFAULT");
            batchAction = new ArmPickBatchAction(singleTemplate, request);
        }
        else
        {
            var request = JsonSerializer.Deserialize<ArmPlaceBatchRequest>(requestJson, ServerActionJson.Default)
                ?? throw new InvalidDataException("ARM.PLACE_BATCH 请求不能为空。");
            var configuration = ActionConfigurationCatalog.Load(
                Path.Combine(AppContext.BaseDirectory, "Configs", "ActionConfiguration.json"));
            singleTemplate = ActionConfigurationResolver.Resolve(singleTemplate, configuration,
                request.Station, request.ReleaseProfile, "SAFE_DEFAULT");
            batchAction = new ArmPlaceBatchAction(singleTemplate, request);
        }

        if (batchAction.Phases.Count == 0)
            throw new InvalidDataException("批量动作 slots 不能为空，无法展开 phases。");
        return JsonSerializer.Serialize(new MainActionMessage(batchAction), ServerActionJson.Default);
    }

    private void SetActionInputExample()
    {
        txtInput.Text = cboAction.Text switch
        {
            "MOVE" => JsonSerializer.Serialize(
                new MoveActionMessage(new MoveAction(new MoveRequest("P01", 0.5))),
                ServerActionJson.Default),
            "ARM.PICK" => """
                          { "station": "PICK_01", "point": "P01", "graspProfile": "DEFAULT_PICK", "expectedMaterial": "MATERIAL_01", "actionPolicy": "SAFE_DEFAULT" }
                          """,
            "ARM.PLACE" => """
                           { "station": "PLACE_01", "point": "P01" }
                           """,
            "ARM.PICK_BATCH" => """
                                {
                                  "station": "PICK_01",
                                  "slots": [ { "slotId": "S01", "point": "P01" }, { "slotId": "S02", "point": "P02" } ],
                                  "orderPolicy": "RANK_ASC",
                                  "policy": { "maxRetries": 1, "retryMode": "VERIFY_BEFORE_RETRY", "onExhaust": "HOLD" }
                                }
                                """,
            "ARM.PLACE_BATCH" => """
                                 {
                                   "station": "PLACE_01",
                                   "slots": [ { "slotId": "S01", "point": "P01" }, { "slotId": "S02", "point": "P02" } ],
                                   "orderPolicy": "RANK_ASC",
                                   "policy": { "maxRetries": 1, "retryMode": "VERIFY_BEFORE_RETRY", "onExhaust": "HOLD" }
                                 }
                                 """,
            "VISION.CAPTURE" => """
                                { "station": "CAMERA_01", "recipe": "DEFAULT_CAPTURE" }
                                """,
            _ => "{}"
        };
    }

    private void SetRunning(bool running)
    {
        if (InvokeRequired) { BeginInvoke(() => SetRunning(running)); return; }
        lblStatus.Text = running ? "● 正在监听" : "● 已停止";
        lblStatus.ForeColor = running ? Color.Green : Color.DimGray;
        btnStart.Enabled = !running; btnStop.Enabled = running; txtPort.Enabled = !running;
    }

    private void RefreshRobots()
    {
        if (InvokeRequired) { BeginInvoke(RefreshRobots); return; }
        var selectedId = (cboRobot.SelectedItem as RobotSessionInfo)?.RobotId;
        var robots = _server.Robots.OrderBy(x => x.RobotId).ToArray();
        cboRobot.DataSource = robots; cboRobot.DisplayMember = nameof(RobotSessionInfo.RobotId);
        if (selectedId is not null)
        {
            var index = Array.FindIndex(robots, x => x.RobotId == selectedId);
            if (index >= 0) cboRobot.SelectedIndex = index;
        }
        gridRobots.DataSource = robots;
        lblCount.Text = $"在线机器人：{robots.Length}";
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(message)); return; }
        txtLog.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _stateRefreshTimer.Stop();
        _stateRefreshTimer.Dispose();
        _server.Dispose();
        base.OnFormClosing(e);
    }
}
