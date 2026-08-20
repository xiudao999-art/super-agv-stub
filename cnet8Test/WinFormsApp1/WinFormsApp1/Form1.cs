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
        _server.ActionAttentionRequired += Server_ActionAttentionRequired;
        cboAction.Items.AddRange(["MOVE", "ARM.PICK", "ARM.PLACE", "ARM.PICK_BATCH", "ARM.PLACE_BATCH", "ARM.HOME", "VISION.CAPTURE", "TEST"]);
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

    private void Server_ActionAttentionRequired(object? sender, ActionAttentionEventArgs e)
    {
        if (InvokeRequired) { BeginInvoke(() => Server_ActionAttentionRequired(sender, e)); return; }
        var actionEvent = e.ActionEvent;
        var error = actionEvent.Error;
        var context = error?.Context;
        var report = actionEvent.ReportState;
        var isBusy = actionEvent.State == MainActionState.Busy;
        var canRetry = context?.UserChoices?.Any(x =>
            x.StartsWith("RETRY", StringComparison.OrdinalIgnoreCase)) == true;
        var robotState = report?.RobotState ??
                         (isBusy ? "EXECUTING" : actionEvent.State.ToString().ToUpperInvariant());
        // 使用反射读取本次新增的可选字段，使界面在调试期间误加载旧版 Actions DLL 时
        // 也只回退显示状态，而不会因 MissingMethodException 导致整个 TCP 会话断开。
        var reportedMainActionState = GetOptionalContextProperty(context, "MainActionState");
        var reportedSubActionState = GetOptionalContextProperty(context, "SubActionState");
        var mainActionState = report?.MainAction.State.ToString().ToUpperInvariant()
                              ?? reportedMainActionState?.ToUpperInvariant()
                              ?? (isBusy ? "RUNNING" : actionEvent.State.ToString().ToUpperInvariant());
        var activeActionId = report?.ActionInstanceId ?? context?.ActionInstanceId ?? actionEvent.ActionInstanceId;
        var subActionName = report?.SubAction?.Name ?? context?.SubAction ?? "-";
        var subActionState = report?.SubAction?.State ?? reportedSubActionState ?? "-";
        var phaseId = report?.SubAction?.PhaseId ?? context?.PhaseId ?? "-";
        var onFail = report?.SubAction?.OnFail ?? context?.OnFail?.ToString() ?? "-";
        var unifiedError = report?.SubAction?.Error ?? error?.Detail;
        var code = unifiedError?.Code.ToString() ?? error?.Code.ToString() ?? "-";
        var msg = unifiedError?.Message ?? report?.SubAction?.Msg ?? error?.Message ?? "-";
        var physical = unifiedError?.PhysicalDevice;
        var detail =
            $"机器人名称：{report?.RobotName ?? e.RobotId}\r\n" +
            $"机器人状态：{robotState}\r\n\r\n" +
            $"正在执行的 ActionInstanceId：{activeActionId}\r\n\r\n" +
            $"MainAction 名称：{report?.MainAction.Name ?? context?.ActionType ?? "UNKNOWN"}\r\n" +
            $"MainAction 状态：{mainActionState}\r\n\r\n" +
            $"正在执行的 SubAction 名称：{subActionName}\r\n" +
            $"正在执行的 SubAction 状态：{subActionState}\r\n" +
            $"PhaseId：{phaseId}\r\n" +
            $"onFail：{onFail}\r\n" +
            $"平台异常 code：{code}\r\n" +
            $"平台异常 msg：{msg}\r\n" +
            (unifiedError is null ? string.Empty :
                $"异常等级：{unifiedError.Severity}\r\n" +
                $"异常分类：{unifiedError.Category}\r\n" +
                $"实际设备：{physical?.DeviceType}/{physical?.Vendor}/{physical?.Model}\r\n" +
                $"设备异常 code：{physical?.Code ?? "-"}\r\n" +
                $"设备异常 msg：{physical?.Message ?? "-"}\r\n" +
                $"可恢复：{unifiedError.Recoverable}\r\n" +
                $"允许自动重试：{unifiedError.Retryable}\r\n" +
                $"责任方：{unifiedError.Owner}\r\n" +
                $"恢复策略：{unifiedError.RecoveryStrategy}\r\n" +
                $"Phase 失败策略：{unifiedError.FailureStrategy}\r\n" +
                $"处理建议：{unifiedError.HandlingAdvice ?? "-"}\r\n") +
            (isBusy ? $"本次被拒绝的 ActionInstanceId：{actionEvent.ActionInstanceId}\r\n" : string.Empty) +
            "\r\n" +
                     (isBusy
                         ? "重新下发只针对本次被拒绝的请求；放弃本次请求不会停止正在执行的动作。"
                         : canRetry
                             ? "重试将从失败 Phase 开始，并继续后续 Phase；已经成功的前置 Phase 不会重复执行。"
                             : "当前 onFail 不允许重试，失败动作已结束。 ");
        using var dialog = new ActionAttentionDialog(detail, canRetry,
            isBusy ? "重新下发本次请求" : "重试失败 Phase",
            isBusy ? "放弃本次请求" : "结束失败动作");
        var choice = dialog.ShowDialog(this);
        if (choice == DialogResult.Retry)
            _ = RetryFromAttentionAsync(actionEvent.ActionInstanceId, phaseId, isBusy);
        else if (isBusy)
            AppendLog($"[{e.RobotId}] 放弃本次被拒绝的请求 actionInstanceId={actionEvent.ActionInstanceId}，当前动作继续执行");
        else
            _ = TerminateFromAttentionAsync(actionEvent.ActionInstanceId, e.RobotId);
    }

    private async Task TerminateFromAttentionAsync(string actionInstanceId, string robotId)
    {
        try
        {
            await _server.TerminateActionAsync(actionInstanceId);
            AppendLog($"[{robotId}] 结束失败动作且不再重试 actionInstanceId={actionInstanceId}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "结束动作失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// 调试期间服务器进程可能仍占用旧版协议程序集。可选字段通过反射读取，旧程序集
    /// 没有该属性时返回 null；重启并加载新版 DLL 后会自动得到真实值。
    /// </summary>
    private static string? GetOptionalContextProperty(ActionFailureContext? context, string propertyName)
    {
        if (context is null) return null;
        try { return context.GetType().GetProperty(propertyName)?.GetValue(context)?.ToString(); }
        catch (MissingMethodException) { return null; }
    }

    private async Task RetryFromAttentionAsync(string actionInstanceId, string phaseId, bool isBusy)
    {
        try
        {
            var newActionId = isBusy
                ? await _server.RetryRejectedCommandAsync(actionInstanceId)
                : await _server.RetryCommandAsync(actionInstanceId, phaseId);
            AppendLog(isBusy
                ? $"重新下发被拒绝请求，原 actionInstanceId={actionInstanceId}，新 actionInstanceId={newActionId}"
                : $"从 phase={phaseId} 断点重试，actionInstanceId 保持不变：{newActionId}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "重试失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void btnSend_Click(object? sender, EventArgs e)
    {
        if (cboRobot.SelectedItem is not RobotSessionInfo robot)
        {
            MessageBox.Show("请先选择在线机器人。");
            return;
        }

        try
        {
            // 所有入口统一从新生成的 TestTemplates JSON 选择完整 MainAction。
            // 下拉框只用于筛选 actionType；TEST 显示全部类型。旧 MOVE 点位、ARM.PICK 专用
            // 对话框及固定 ARM.*.Templates.json 组合解析不再参与下发流程。
            var selectedAction = cboAction.Text;
            var directory = Path.Combine(AppContext.BaseDirectory, "Configs", "TestTemplates");
            var allTemplates = TestTemplateDialog.LoadFiles(directory);
            var actionTypeFilter = selectedAction == "TEST" ? null : selectedAction;
            var visibleTemplates = actionTypeFilter is null
                ? allTemplates
                : allTemplates.Where(x => string.Equals(x.ActionType, actionTypeFilter,
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            using var dialog = new TestTemplateDialog(directory, visibleTemplates, actionTypeFilter);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var selected = dialog.SelectedTemplate
                ?? throw new InvalidOperationException("没有选择 MainAction 模板文件。");
            txtInput.Text = BuildTestMainAction(selected.FullPath, out var actionTypeToSend);

            using var _ = JsonDocument.Parse(txtInput.Text);
            var actionId = await _server.SendCommandAsync(robot.RobotId, actionTypeToSend, "1.0", ExecutionMode.Package,
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
    /// TEST 是服务端测试入口而不是协议动作。读取文件中的完整 MainAction，并返回其真实
    /// actionType 用于能力检查；客户端收到 templateId=TEST.* 后通用执行全部 phases。
    /// </summary>
    private static string BuildTestMainAction(string path, out string actionType)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到 TEST 模板文件。", path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("actionTemplates", out var templates) ||
            templates.ValueKind != JsonValueKind.Array || templates.GetArrayLength() != 1)
            throw new InvalidDataException("Test.Templates.json 必须且只能包含一个待测试 MainAction。");
        // TEST 的发送端只做 JSON 可解析检查，不校验 phases；空 Phase、错误引用等场景
        // 必须真正发给机器人，由客户端统一校验并通过 ActionEvent 上报。
        var template = templates[0].Deserialize<MainActionTemplate>(ServerActionJson.Default)
            ?? throw new InvalidDataException("TEST MainAction 无法反序列化。");
        actionType = template.ActionType.ToActionType();
        return JsonSerializer.Serialize(new MainActionMessage(template), ServerActionJson.Default);
    }

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
        txtInput.Text = new JsonObject
        {
            ["templateSource"] = "Configs/TestTemplates",
            ["actionTypeFilter"] = cboAction.Text == "TEST" ? "ALL" : cboAction.Text,
            ["说明"] = "点击下发后从弹窗选择新模板 JSON，并发送其中的完整 MainAction"
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
