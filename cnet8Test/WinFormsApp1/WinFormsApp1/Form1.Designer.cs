namespace WinFormsApp1;

partial class Form1
{
    private System.ComponentModel.IContainer components = null!;
    private FlowLayoutPanel topPanel = null!;
    private Label lblPort = null!;
    private TextBox txtPort = null!;
    private Button btnStart = null!;
    private Button btnStop = null!;
    private Label lblStatus = null!;
    private Label lblCount = null!;
    private SplitContainer mainSplit = null!;
    private SplitContainer upperSplit = null!;
    private DataGridView gridRobots = null!;
    private TableLayoutPanel commandPanel = null!;
    private Label lblRobot = null!;
    private ComboBox cboRobot = null!;
    private Label lblAction = null!;
    private ComboBox cboAction = null!;
    private Label lblTimeout = null!;
    private NumericUpDown numTimeout = null!;
    private Label lblInput = null!;
    private TextBox txtInput = null!;
    private Button btnSend = null!;
    private GroupBox logGroup = null!;
    private TextBox txtLog = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components is not null) components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        topPanel = new FlowLayoutPanel();
        lblPort = new Label();
        txtPort = new TextBox();
        btnStart = new Button();
        btnStop = new Button();
        lblStatus = new Label();
        lblCount = new Label();
        mainSplit = new SplitContainer();
        upperSplit = new SplitContainer();
        gridRobots = new DataGridView();
        commandPanel = new TableLayoutPanel();
        lblRobot = new Label();
        cboRobot = new ComboBox();
        lblAction = new Label();
        cboAction = new ComboBox();
        lblTimeout = new Label();
        numTimeout = new NumericUpDown();
        lblInput = new Label();
        txtInput = new TextBox();
        btnSend = new Button();
        logGroup = new GroupBox();
        txtLog = new TextBox();
        topPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)mainSplit).BeginInit();
        mainSplit.Panel1.SuspendLayout();
        mainSplit.Panel2.SuspendLayout();
        mainSplit.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)upperSplit).BeginInit();
        upperSplit.Panel1.SuspendLayout();
        upperSplit.Panel2.SuspendLayout();
        upperSplit.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)gridRobots).BeginInit();
        commandPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)numTimeout).BeginInit();
        logGroup.SuspendLayout();
        SuspendLayout();
        // 
        // topPanel
        // 
        topPanel.Controls.Add(lblPort);
        topPanel.Controls.Add(txtPort);
        topPanel.Controls.Add(btnStart);
        topPanel.Controls.Add(btnStop);
        topPanel.Controls.Add(lblStatus);
        topPanel.Controls.Add(lblCount);
        topPanel.Dock = DockStyle.Top;
        topPanel.Location = new Point(0, 0);
        topPanel.Margin = new Padding(4);
        topPanel.Name = "topPanel";
        topPanel.Padding = new Padding(18);
        topPanel.Size = new Size(1770, 87);
        topPanel.TabIndex = 1;
        topPanel.WrapContents = false;
        // 
        // lblPort
        // 
        lblPort.AutoSize = true;
        lblPort.Location = new Point(18, 30);
        lblPort.Margin = new Padding(0, 12, 12, 0);
        lblPort.Name = "lblPort";
        lblPort.Size = new Size(82, 24);
        lblPort.TabIndex = 0;
        lblPort.Text = "监听端口";
        // 
        // txtPort
        // 
        txtPort.Location = new Point(116, 22);
        txtPort.Margin = new Padding(4);
        txtPort.Name = "txtPort";
        txtPort.Size = new Size(133, 30);
        txtPort.TabIndex = 1;
        txtPort.Text = "8080";
        // 
        // btnStart
        // 
        btnStart.Location = new Point(257, 22);
        btnStart.Margin = new Padding(4);
        btnStart.Name = "btnStart";
        btnStart.Size = new Size(165, 45);
        btnStart.TabIndex = 2;
        btnStart.Text = "启动服务器";
        btnStart.Click += btnStart_Click;
        // 
        // btnStop
        // 
        btnStop.Enabled = false;
        btnStop.Location = new Point(430, 22);
        btnStop.Margin = new Padding(4);
        btnStop.Name = "btnStop";
        btnStop.Size = new Size(120, 45);
        btnStop.TabIndex = 3;
        btnStop.Text = "停止";
        btnStop.Click += btnStop_Click;
        // 
        // lblStatus
        // 
        lblStatus.AutoSize = true;
        lblStatus.ForeColor = Color.DimGray;
        lblStatus.Location = new Point(581, 30);
        lblStatus.Margin = new Padding(27, 12, 0, 0);
        lblStatus.Name = "lblStatus";
        lblStatus.Size = new Size(80, 24);
        lblStatus.TabIndex = 4;
        lblStatus.Text = "○ 已停止";
        // 
        // lblCount
        // 
        lblCount.AutoSize = true;
        lblCount.Location = new Point(706, 30);
        lblCount.Margin = new Padding(45, 12, 0, 0);
        lblCount.Name = "lblCount";
        lblCount.Size = new Size(129, 24);
        lblCount.TabIndex = 5;
        lblCount.Text = "在线机器人：0";
        // 
        // mainSplit
        // 
        mainSplit.Dock = DockStyle.Fill;
        mainSplit.Location = new Point(0, 87);
        mainSplit.Margin = new Padding(4);
        mainSplit.Name = "mainSplit";
        mainSplit.Orientation = Orientation.Horizontal;
        // 
        // mainSplit.Panel1
        // 
        mainSplit.Panel1.Controls.Add(upperSplit);
        // 
        // mainSplit.Panel2
        // 
        mainSplit.Panel2.Controls.Add(logGroup);
        mainSplit.Size = new Size(1770, 1053);
        mainSplit.SplitterDistance = 747;
        mainSplit.SplitterWidth = 6;
        mainSplit.TabIndex = 0;
        // 
        // upperSplit
        // 
        upperSplit.Dock = DockStyle.Fill;
        upperSplit.Location = new Point(0, 0);
        upperSplit.Margin = new Padding(4);
        upperSplit.Name = "upperSplit";
        // 
        // upperSplit.Panel1
        // 
        upperSplit.Panel1.Controls.Add(gridRobots);
        // 
        // upperSplit.Panel2
        // 
        upperSplit.Panel2.Controls.Add(commandPanel);
        upperSplit.Size = new Size(1770, 747);
        upperSplit.SplitterDistance = 1427;
        upperSplit.SplitterWidth = 6;
        upperSplit.TabIndex = 0;
        // 
        // gridRobots
        // 
        gridRobots.AllowUserToAddRows = false;
        gridRobots.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        gridRobots.ColumnHeadersHeight = 34;
        gridRobots.Dock = DockStyle.Fill;
        gridRobots.Location = new Point(0, 0);
        gridRobots.Margin = new Padding(4);
        gridRobots.Name = "gridRobots";
        gridRobots.ReadOnly = true;
        gridRobots.RowHeadersWidth = 62;
        gridRobots.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        gridRobots.Size = new Size(1427, 747);
        gridRobots.TabIndex = 0;
        // 
        // commandPanel
        // 
        commandPanel.ColumnCount = 2;
        commandPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128F));
        commandPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        commandPanel.Controls.Add(lblRobot, 0, 0);
        commandPanel.Controls.Add(cboRobot, 1, 0);
        commandPanel.Controls.Add(lblAction, 0, 1);
        commandPanel.Controls.Add(cboAction, 1, 1);
        commandPanel.Controls.Add(lblTimeout, 0, 2);
        commandPanel.Controls.Add(numTimeout, 1, 2);
        commandPanel.Controls.Add(lblInput, 0, 3);
        commandPanel.Controls.Add(txtInput, 0, 4);
        commandPanel.Controls.Add(btnSend, 0, 5);
        commandPanel.Dock = DockStyle.Fill;
        commandPanel.Location = new Point(0, 0);
        commandPanel.Margin = new Padding(4);
        commandPanel.Name = "commandPanel";
        commandPanel.Padding = new Padding(18);
        commandPanel.RowCount = 6;
        commandPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 51F));
        commandPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 51F));
        commandPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 51F));
        commandPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 39F));
        commandPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        commandPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 66F));
        commandPanel.Size = new Size(337, 747);
        commandPanel.TabIndex = 0;
        // 
        // lblRobot
        // 
        lblRobot.AutoSize = true;
        lblRobot.Location = new Point(22, 30);
        lblRobot.Margin = new Padding(4, 12, 4, 0);
        lblRobot.Name = "lblRobot";
        lblRobot.Size = new Size(64, 24);
        lblRobot.TabIndex = 0;
        lblRobot.Text = "机器人";
        // 
        // cboRobot
        // 
        cboRobot.Dock = DockStyle.Fill;
        cboRobot.DropDownStyle = ComboBoxStyle.DropDownList;
        cboRobot.Location = new Point(150, 22);
        cboRobot.Margin = new Padding(4);
        cboRobot.Name = "cboRobot";
        cboRobot.Size = new Size(165, 32);
        cboRobot.TabIndex = 1;
        // 
        // lblAction
        // 
        lblAction.AutoSize = true;
        lblAction.Location = new Point(22, 81);
        lblAction.Margin = new Padding(4, 12, 4, 0);
        lblAction.Name = "lblAction";
        lblAction.Size = new Size(66, 24);
        lblAction.TabIndex = 2;
        lblAction.Text = "Action";
        // 
        // cboAction
        // 
        cboAction.Dock = DockStyle.Fill;
        cboAction.DropDownStyle = ComboBoxStyle.DropDownList;
        cboAction.Location = new Point(150, 73);
        cboAction.Margin = new Padding(4);
        cboAction.Name = "cboAction";
        cboAction.Size = new Size(165, 32);
        cboAction.TabIndex = 3;
        // 
        // lblTimeout
        // 
        lblTimeout.AutoSize = true;
        lblTimeout.Location = new Point(22, 132);
        lblTimeout.Margin = new Padding(4, 12, 4, 0);
        lblTimeout.Name = "lblTimeout";
        lblTimeout.Size = new Size(83, 24);
        lblTimeout.TabIndex = 4;
        lblTimeout.Text = "超时(ms)";
        // 
        // numTimeout
        // 
        numTimeout.Dock = DockStyle.Fill;
        numTimeout.Increment = new decimal(new int[] { 1000, 0, 0, 0 });
        numTimeout.Location = new Point(150, 124);
        numTimeout.Margin = new Padding(4);
        numTimeout.Maximum = new decimal(new int[] { 600000, 0, 0, 0 });
        numTimeout.Minimum = new decimal(new int[] { 1000, 0, 0, 0 });
        numTimeout.Name = "numTimeout";
        numTimeout.Size = new Size(165, 30);
        numTimeout.TabIndex = 5;
        numTimeout.Value = new decimal(new int[] { 60000, 0, 0, 0 });
        // 
        // lblInput
        // 
        lblInput.AutoSize = true;
        lblInput.Location = new Point(22, 171);
        lblInput.Margin = new Padding(4, 0, 4, 0);
        lblInput.Name = "lblInput";
        lblInput.Size = new Size(108, 24);
        lblInput.TabIndex = 6;
        lblInput.Text = "Input JSON";
        // 
        // txtInput
        // 
        commandPanel.SetColumnSpan(txtInput, 2);
        txtInput.Dock = DockStyle.Fill;
        txtInput.Location = new Point(22, 214);
        txtInput.Margin = new Padding(4);
        txtInput.Multiline = true;
        txtInput.Name = "txtInput";
        txtInput.ScrollBars = ScrollBars.Vertical;
        txtInput.Size = new Size(293, 445);
        txtInput.TabIndex = 7;
        txtInput.Text = "{}";
        // 
        // btnSend
        // 
        commandPanel.SetColumnSpan(btnSend, 2);
        btnSend.Dock = DockStyle.Fill;
        btnSend.Location = new Point(22, 667);
        btnSend.Margin = new Padding(4);
        btnSend.Name = "btnSend";
        btnSend.Size = new Size(293, 58);
        btnSend.TabIndex = 8;
        btnSend.Text = "下发 Action";
        btnSend.Click += btnSend_Click;
        // 
        // logGroup
        // 
        logGroup.Controls.Add(txtLog);
        logGroup.Dock = DockStyle.Fill;
        logGroup.Location = new Point(0, 0);
        logGroup.Margin = new Padding(4);
        logGroup.Name = "logGroup";
        logGroup.Padding = new Padding(12);
        logGroup.Size = new Size(1770, 300);
        logGroup.TabIndex = 0;
        logGroup.TabStop = false;
        logGroup.Text = "运行日志";
        // 
        // txtLog
        // 
        txtLog.BackColor = Color.FromArgb(30, 30, 30);
        txtLog.Dock = DockStyle.Fill;
        txtLog.Font = new Font("Consolas", 10F);
        txtLog.ForeColor = Color.Gainsboro;
        txtLog.Location = new Point(12, 35);
        txtLog.Margin = new Padding(4);
        txtLog.Multiline = true;
        txtLog.Name = "txtLog";
        txtLog.ReadOnly = true;
        txtLog.ScrollBars = ScrollBars.Both;
        txtLog.Size = new Size(1746, 253);
        txtLog.TabIndex = 0;
        // 
        // Form1
        // 
        AutoScaleDimensions = new SizeF(144F, 144F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1770, 1140);
        Controls.Add(mainSplit);
        Controls.Add(topPanel);
        Margin = new Padding(4);
        MinimumSize = new Size(1489, 947);
        Name = "Form1";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "坤灵机器人 Action 调度测试服务端";
        topPanel.ResumeLayout(false);
        topPanel.PerformLayout();
        mainSplit.Panel1.ResumeLayout(false);
        mainSplit.Panel2.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)mainSplit).EndInit();
        mainSplit.ResumeLayout(false);
        upperSplit.Panel1.ResumeLayout(false);
        upperSplit.Panel2.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)upperSplit).EndInit();
        upperSplit.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)gridRobots).EndInit();
        commandPanel.ResumeLayout(false);
        commandPanel.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)numTimeout).EndInit();
        logGroup.ResumeLayout(false);
        logGroup.PerformLayout();
        ResumeLayout(false);
    }
}
