namespace WinFormsApp1;

/// <summary>
/// 动作异常人工处理窗口。使用明确的“重试/终止”按钮，避免系统 MessageBox
/// 把终止操作显示成含义不明确的“取消”。
/// </summary>
internal sealed class ActionAttentionDialog : Form
{
    public ActionAttentionDialog(string detail, bool canRetry,
        string retryButtonText = "重试失败 Phase", string terminateButtonText = "结束失败动作")
    {
        Text = "机器人动作需要处理";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 430);
        BackColor = Color.White;

        var icon = new PictureBox
        {
            Image = SystemIcons.Warning.ToBitmap(),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Location = new Point(28, 35),
            Size = new Size(52, 52)
        };
        var message = new TextBox
        {
            Text = detail,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            Font = new Font("Microsoft YaHei UI", 10F),
            Location = new Point(96, 30),
            Size = new Size(490, 315),
            TabStop = false
        };

        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 70,
            BackColor = Color.FromArgb(245, 245, 245)
        };
        var terminate = new Button
        {
            Text = terminateButtonText,
            DialogResult = DialogResult.Abort,
            Size = new Size(112, 38),
            Location = new Point(474, 16),
            Font = new Font("Microsoft YaHei UI", 9.5F)
        };
        footer.Controls.Add(terminate);

        if (canRetry)
        {
            var retry = new Button
            {
                Text = retryButtonText,
                DialogResult = DialogResult.Retry,
                Size = new Size(112, 38),
                Location = new Point(350, 16),
                Font = new Font("Microsoft YaHei UI", 9.5F)
            };
            footer.Controls.Add(retry);
            AcceptButton = retry;
        }

        CancelButton = terminate;
        Controls.Add(icon);
        Controls.Add(message);
        Controls.Add(footer);
    }
}
