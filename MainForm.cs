using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpcDaToModbusGateway.Models;
using OpcDaToModbusGateway.Services;
using OpcDaToModbusGateway.Services.Interfaces;

namespace OpcDaToModbusGateway
{
    /// <summary>
    /// 主窗体 - OPC DA 到 Modbus TCP 网关的控制界面
    /// 职责：UI 构建、用户交互、协调各 Manager
    /// </summary>
    public class MainForm : Form
    {
        // P3 修复 + R-6：统一使用 AppConstants.WindowTitle，消除硬编码重复
        internal const string WindowTitle = AppConstants.WindowTitle;

        // ---- UI 控件 ----
        private Label _lblCurrentServer;
        private TextBox _txtProgId;
        private ComboBox _cmbDaMode;
        private Button _btnBrowse;
        private Button _btnFetchTags;
        private ComboBox _cmbListenAddress;
        private NumericUpDown _nudUaPort;
        private NumericUpDown _nudSlaveId;
        private Label _lblEndpointUrl;
        private Button _btnStart;
        private Button _btnStop;
        private Button _btnExportTags;
        private CheckBox _chkAutoConnectDa;
        private CheckBox _chkAutoStartModbus;
        private CheckBox _chkAutoStartWin;
        private CheckBox _chkEnableWatchdog;
        private Label _lblDaStatus;
        private Label _lblUaStatus;
        private Label _lblStats;
        private Label _lblWatchdogStatus;
        private DataGridView _dgvTags;
        private List<TagConfig> _gridTags; // 虚拟模式下的标签数据源
        private IReadOnlyList<TagSnapshot> _cachedSnapshots; // 缓存的快照引用，避免每次 CellValueNeeded 都重建（接口 IDataBridge.GetSnapshots 返回类型）
        private TextBox _txtLog;
        private Timer _refreshTimer;

        /// <summary>P1-4: 自适应刷新 — 缓存上次快照的特征哈希，无变化时降低刷新频率</summary>
        private int _lastSnapshotHash;
        private Timer _healthTimer;
        private Timer _autoStartTimer;  // P2 修复：存为字段以便 Dispose

        // ---- 管理器 ----
        private LogManager _log;
        private ConfigManager _configMgr;
        private WatchdogManager _watchdogMgr;
        private GatewayManager _gatewayMgr;
        private IHealthSnapshot _healthSnapshot;
        private LicenseManager _licenseMgr;

        // ---- 授权 ----
        private Label _lblLicenseStatus;

        // ---- 系统托盘 ----
        private NotifyIcon _notifyIcon;
        private readonly bool _startMinimized;
        private bool _forceClose;
        private bool _isShuttingDown;
        private volatile bool _closeInProgress; // M3 修复：防止 async void 重入
        private bool _isShuttingDone; // C-15 修复：标记异步关闭已完成，允许第二次 Close 执行资源释放

        /// <summary>当前配置（便捷属性，代理到 ConfigManager）</summary>
        private AppConfig Config => _configMgr?.Config;

        /// <summary>是否正在加载配置（防止触发自动保存）</summary>
        private bool _isLoadingConfig;

        public MainForm(bool startMinimized = false)
        {
            _startMinimized = startMinimized;
            BuildUI();
            LoadConfiguration();
        }

        // ================================================================
        //  UI 构建
        // ================================================================

        private void BuildUI()
        {
            Text = WindowTitle;
            Size = new Size(960, 900);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(800, 720);
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Theme.FormBg;

            string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(iconPath))
            {
                try { Icon = new Icon(iconPath); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"加载图标失败: {ex.Message}"); }
            }

            int y = 10;

            // ---- 区域 1：OPC DA 服务器选择 ----
            var grpServer = new GroupBox
            {
                Text = "OPC DA 服务器设置",
                Location = new Point(10, y),
                Size = new Size(920, 130),
                BackColor = Theme.Surface
            };

            var lblPrompt = new Label { Text = "服务器 ProgId:", Location = new Point(15, 30), AutoSize = true };
            _txtProgId = new TextBox { Location = new Point(110, 27), Size = new Size(420, 25) };
            _txtProgId.TextChanged += (s, ev) => UpdateDaButtonsState();

            _btnBrowse = new Button
            {
                Text = "浏览...", Location = new Point(540, 25), Size = new Size(80, 28),
                FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand
            };
            _btnBrowse.FlatAppearance.BorderColor = Theme.Border;
            _btnBrowse.Click += BtnBrowse_Click;

            _lblCurrentServer = new Label { Text = "", Location = new Point(640, 30), AutoSize = true, ForeColor = Color.Gray };

            _btnFetchTags = new Button
            {
                Text = "获取点位...", Location = new Point(110, 65), Size = new Size(110, 30),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Primary, ForeColor = Color.White,
                Cursor = Cursors.Hand, Enabled = false  // V1.9.0: 未选择服务器时禁用
            };
            _btnFetchTags.FlatAppearance.BorderSize = 0;
            _btnFetchTags.Click += BtnFetchTags_Click;

            var lblFetchHint = new Label
            {
                Text = "选择服务器后，点击此按钮自动获取所有点位",
                Location = new Point(230, 72), AutoSize = true, ForeColor = Color.Gray
            };

            // 数据获取方式：异步订阅（服务器主动推送）/ 同步轮询（网关定时主动读取）
            // 与「获取点位」按钮同行对齐（y ≈ 65-70）
            var lblDaMode = new Label { Text = "数据获取:", Location = new Point(540, 70), AutoSize = true, ForeColor = Color.Gray };
            _cmbDaMode = new ComboBox
            {
                Location = new Point(610, 66), Size = new Size(150, 25), DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbDaMode.Items.AddRange(new object[] { "异步订阅", "同步轮询" });
            _cmbDaMode.SelectedIndex = 0;
            _cmbDaMode.SelectedIndexChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.OpcDa.Mode = _cmbDaMode.SelectedIndex == 1 ? "Sync" : "Async";
                    _configMgr.Save();
                }
            };

            grpServer.Controls.AddRange(new Control[] { lblPrompt, _txtProgId, _btnBrowse, _lblCurrentServer, _btnFetchTags, lblFetchHint, lblDaMode, _cmbDaMode });
            Controls.Add(grpServer);
            y += 140;

            // ---- 区域 2：Modbus TCP 服务器设置 ----
            var grpModbusSettings = new GroupBox
            {
                Text = "Modbus TCP 服务器设置",
                Location = new Point(10, y),
                Size = new Size(920, 120),
                BackColor = Theme.Surface
            };

            // 第 1 行：监听地址、端口号、从站 ID
            var lblListen = new Label { Text = "监听地址:", Location = new Point(15, 28), AutoSize = true };
            _cmbListenAddress = new ComboBox
            {
                Location = new Point(85, 25), Size = new Size(140, 25), DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbListenAddress.Items.AddRange(new object[] { "localhost", "0.0.0.0" });
            _cmbListenAddress.SelectedIndex = 0;
            _cmbListenAddress.SelectedIndexChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.ModbusTcp.ListenAddress = _cmbListenAddress.SelectedItem.ToString();
                    UpdateEndpointUrlLabel();
                    _configMgr.Save();
                }
            };

            var lblPort = new Label { Text = "端口号:", Location = new Point(250, 28), AutoSize = true };
            _nudUaPort = new NumericUpDown
            {
                Location = new Point(310, 25), Size = new Size(70, 25), Minimum = 1, Maximum = 65535, Value = 502
            };
            _nudUaPort.ValueChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.ModbusTcp.Port = (int)_nudUaPort.Value;
                    UpdateEndpointUrlLabel();
                    _configMgr.Save();
                }
            };

            var lblSlaveId = new Label { Text = "从站 ID:", Location = new Point(410, 28), AutoSize = true };
            _nudSlaveId = new NumericUpDown
            {
                Location = new Point(470, 25), Size = new Size(60, 25), Minimum = 1, Maximum = 247, Value = 1
            };
            _nudSlaveId.ValueChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.ModbusTcp.SlaveId = (byte)_nudSlaveId.Value;
                    _configMgr.Save();
                }
            };

            _lblEndpointUrl = new Label
            {
                Text = "", Location = new Point(15, 85), AutoSize = true,
                ForeColor = Color.DodgerBlue, Font = new Font("Consolas", 9f)
            };

            grpModbusSettings.Controls.AddRange(new Control[] {
                lblListen, _cmbListenAddress, lblPort, _nudUaPort, lblSlaveId, _nudSlaveId, _lblEndpointUrl
            });
            Controls.Add(grpModbusSettings);
            y += 130;

            // ---- 区域 3：控制面板 ----
            var grpControl = new GroupBox
            {
                Text = "控制面板",
                Location = new Point(10, y),
                Size = new Size(920, 130),
                BackColor = Theme.Surface
            };

            _btnStart = CreateButton("启动网关", Color.FromArgb(76, 175, 80), new Point(15, 25));
            _btnStart.Click += BtnStart_Click;

            _btnStop = CreateButton("停止网关", Color.FromArgb(244, 67, 54), new Point(15, 55));
            _btnStop.Enabled = false;
            _btnStop.Click += BtnStop_Click;

            _btnExportTags = new Button
            {
                Text = "导出点表...", Location = new Point(15, 85), Size = new Size(105, 33),
                BackColor = Theme.Purple, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _btnExportTags.FlatAppearance.BorderSize = 0;
            _btnExportTags.Click += BtnExportTags_Click;

            _chkAutoConnectDa = new CheckBox
            {
                Text = "自动连接 DA", Location = new Point(235, 25), AutoSize = true
            };
            _chkAutoConnectDa.CheckedChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null) { Config.AutoConnectDa = _chkAutoConnectDa.Checked; _configMgr.Save(); }
            };

            _chkAutoStartModbus = new CheckBox
            {
                Text = "自动启动网关", Location = new Point(235, 50), AutoSize = true
            };
            _chkAutoStartModbus.CheckedChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null) { Config.AutoStartModbus = _chkAutoStartModbus.Checked; _configMgr.Save(); }
            };

            _chkEnableWatchdog = new CheckBox
            {
                Text = "进程守护", Location = new Point(235, 75), AutoSize = true
            };
            _chkEnableWatchdog.CheckedChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.EnableWatchdog = _chkEnableWatchdog.Checked;
                    _configMgr.Save();
                    if (_chkEnableWatchdog.Checked)
                    {
                        _watchdogMgr.Start();
                        _log.Append("[守护] 已开启进程守护");
                    }
                    else
                    {
                        _watchdogMgr.Stop();
                        _log.Append("[守护] 已关闭进程守护");
                    }
                }
            };

            _chkAutoStartWin = new CheckBox
            {
                Text = "开机启动", Location = new Point(235, 100), AutoSize = true
            };
            _chkAutoStartWin.CheckedChanged += (s, ev) =>
            {
                if (!_isLoadingConfig && Config != null)
                {
                    Config.AutoStartWithWindows = _chkAutoStartWin.Checked;
                    _configMgr.SetAutoStart(_chkAutoStartWin.Checked);
                    _configMgr.Save();
                }
            };

            _lblDaStatus = new Label { Text = "● DA: 未连接", Location = new Point(410, 25), AutoSize = true, ForeColor = Color.Gray };
            _lblUaStatus = new Label { Text = "● Modbus: 未启动", Location = new Point(410, 50), AutoSize = true, ForeColor = Color.Gray };
            _lblStats = new Label { Text = "更新: 0 | 错误: 0", Location = new Point(570, 50), AutoSize = true };
            _lblWatchdogStatus = new Label { Text = "● 守护: 未启动", Location = new Point(410, 75), AutoSize = true, ForeColor = Color.Gray };
            _lblLicenseStatus = new Label { Text = "● 授权: 检测中...", Location = new Point(410, 100), AutoSize = true, ForeColor = Color.Gray };

            var btnAbout = new Button
            {
                Text = "关于", Location = new Point(840, 90), Size = new Size(65, 28),
                FlatStyle = FlatStyle.Flat, BackColor = Theme.Neutral, ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnAbout.FlatAppearance.BorderSize = 0;
            btnAbout.Click += (s, ev) =>
            {
                using (var about = new AboutDialog(_licenseMgr.PCID, _licenseMgr.IsLicensed))
                {
                    var result = about.ShowDialog(this);
                    if (result == DialogResult.OK && !string.IsNullOrEmpty(about.AuthorizationCode))
                        _licenseMgr.ApplyAuthorizationCode(about.AuthorizationCode);
                }
            };

            grpControl.Controls.AddRange(new Control[] {
                _btnStart, _btnStop, _btnExportTags, _chkAutoConnectDa, _chkAutoStartModbus,
                _chkAutoStartWin, _chkEnableWatchdog, _lblDaStatus, _lblUaStatus, _lblStats,
                _lblWatchdogStatus, _lblLicenseStatus, btnAbout
            });
            Controls.Add(grpControl);
            y += 140;

            // ---- 区域 4：标签数据监控 ----
            var grpMonitor = new GroupBox
            {
                Text = "标签数据监控（实时）",
                Location = new Point(10, y),
                Size = new Size(920, 220),
                BackColor = Theme.Surface
            };

            _dgvTags = new DataGridView
            {
                Location = new Point(10, 22),
                Size = new Size(900, 190),
                ReadOnly = true,
                VirtualMode = true, // 虚拟模式：不创建实际行对象，按需提供单元格数据，支持 50000+ 行
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                RowHeadersVisible = false,
                Font = new Font("Consolas", 9.5f),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            // 启用双缓冲减少大量标签行刷新时的屏幕闪烁（50000 行场景）
            typeof(DataGridView).InvokeMember("DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.SetProperty,
                null, _dgvTags, new object[] { true });

            _dgvTags.Columns.Add(new DataGridViewTextBoxColumn { Name = "colName", HeaderText = "标签名称", FillWeight = 25, SortMode = DataGridViewColumnSortMode.NotSortable });
            _dgvTags.Columns.Add(new DataGridViewTextBoxColumn { Name = "colItemId", HeaderText = "ItemId", FillWeight = 30, SortMode = DataGridViewColumnSortMode.NotSortable });
            _dgvTags.Columns.Add(new DataGridViewTextBoxColumn { Name = "colValue", HeaderText = "当前值", FillWeight = 20, SortMode = DataGridViewColumnSortMode.NotSortable });
            _dgvTags.Columns.Add(new DataGridViewTextBoxColumn { Name = "colQuality", HeaderText = "质量", FillWeight = 10, SortMode = DataGridViewColumnSortMode.NotSortable });
            _dgvTags.Columns.Add(new DataGridViewTextBoxColumn { Name = "colTime", HeaderText = "时间戳", FillWeight = 25, SortMode = DataGridViewColumnSortMode.NotSortable });

            // 应用统一视觉主题：深蓝表头、交替行色、选中高亮、细网格线
            Theme.ApplyGridStyle(_dgvTags);

            // 虚拟模式：按需提供单元格数据，只在渲染可见行时触发
            _dgvTags.CellValueNeeded += DgvTags_CellValueNeeded;
            _dgvTags.CellFormatting += DgvTags_CellFormatting;

            // 滚动时立即触发重绘，确保新可见行的数据及时显示
            _dgvTags.Scroll += (s, ev) =>
            {
                if (ev.Type == ScrollEventType.ThumbTrack || ev.Type == ScrollEventType.ThumbPosition
                    || ev.Type == ScrollEventType.SmallIncrement || ev.Type == ScrollEventType.SmallDecrement
                    || ev.Type == ScrollEventType.LargeIncrement || ev.Type == ScrollEventType.LargeDecrement)
                {
                    _dgvTags.Invalidate();
                }
            };

            grpMonitor.Controls.Add(_dgvTags);
            Controls.Add(grpMonitor);
            y += 230;

            // ---- 区域 5：运行日志 ----
            var grpLog = new GroupBox
            {
                Text = "运行日志",
                Location = new Point(10, y),
                Size = new Size(920, 200),
                BackColor = Theme.TerminalBg,
                ForeColor = Theme.TextOnDark,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            _txtLog = new TextBox
            {
                Location = new Point(10, 22),
                Size = new Size(900, 170),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.TerminalBg,
                ForeColor = Theme.TextOnDark,
                Font = new Font("Consolas", 9f),
                BorderStyle = BorderStyle.None,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            grpLog.Controls.Add(_txtLog);
            Controls.Add(grpLog);

            // 初始化 LogManager（绑定日志文本框）
            _log = new LogManager(_txtLog);

            // 初始化 ConfigManager
            _configMgr = new ConfigManager(_log);

            // 初始化 WatchdogManager（在网关管理器之前创建）
            _watchdogMgr = new WatchdogManager(_log);

            // 定时刷新（P1-4: 自适应间隔，初始 1000ms）
            _refreshTimer = new Timer { Interval = AppConstants.UiDefaultRefreshMs };
            _refreshTimer.Tick += (s, e) => RefreshStats();

            // ---- 系统托盘图标 ----
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("显示主窗口", null, (s, e) => ShowMainWindow());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出", null, (s, e) => ExitApplication());

            _notifyIcon = new NotifyIcon
            {
                Text = WindowTitle,
                ContextMenuStrip = trayMenu,
                Visible = true
            };

            _notifyIcon.Icon = Icon ?? SystemIcons.Application;
            _notifyIcon.DoubleClick += (s, e) => ShowMainWindow();

            if (_startMinimized)
            {
                WindowState = FormWindowState.Minimized;
                ShowInTaskbar = false;
                Hide();
            }
        }

        // ================================================================
        //  UI 辅助方法
        // ================================================================

        private void UpdateEndpointUrlLabel()
        {
            if (Config?.ModbusTcp == null) return;
            _lblEndpointUrl.Text = Config.ModbusTcp.GetEndpointUrl();
        }

        private void SetModbusSettingsEnabled(bool enabled)
        {
            _cmbListenAddress.Enabled = enabled;
            _nudUaPort.Enabled = enabled;
            _nudSlaveId.Enabled = enabled;
        }

        /// <summary>
        /// N-2 优化：统一设置网关运行期间 UI 控件的启用/禁用状态。
        /// 替代原先在 3 处（启动时、启动失败、停止后）的重复代码块。
        /// </summary>
        /// <param name="isRunning">true=运行中禁用设置控件，false=停止后恢复控件</param>
        private void SetUiRunningState(bool isRunning)
        {
            _btnStart.Enabled = !isRunning;
            _btnBrowse.Enabled = !isRunning;
            _btnFetchTags.Enabled = !isRunning;
            _btnExportTags.Enabled = true; // 导出在运行中也可用，启动失败和停止后也恢复
            _txtProgId.ReadOnly = isRunning;
            SetModbusSettingsEnabled(!isRunning);
            // V1.9.0: 网关停止后，根据 ProgId 是否非空重新校准按钮状态
            if (!isRunning)
            {
                UpdateDaButtonsState();
            }
        }

        /// <summary>
        /// PLAN 3.5: 显示内存增长率告警到状态栏。severity: 0=清除 1=黄色 2=红色。
        /// 不弹窗，避免现场操作员误关。
        /// </summary>
        private void ShowMemoryAlert(string message, int severity)
        {
            // severity 0 不直接处理（视为正常清除），保留 1/2 红色/黄色提示
            if (severity <= 0) return;
            _lblWatchdogStatus.Text = message;   // 复用看门狗状态栏显示
            _lblWatchdogStatus.ForeColor = severity >= 2 ? Color.Red : Color.OrangeRed;
        }

        private Button CreateButton(string text, Color bgColor, Point location)
        {
            var btn = new Button
            {
                Text = text, Location = location, Size = new Size(105, 33),
                BackColor = bgColor, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = ControlPaint.Light(bgColor, 0.15f);
            btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(bgColor, 0.1f);
            return btn;
        }

        private void UpdateTagGrid(List<TagConfig> tags)
        {
            _gridTags = tags ?? new List<TagConfig>();

            // 虚拟模式：只设置行数，不创建实际行对象。
            // DataGridView 仅在渲染可见行时通过 CellValueNeeded 事件按需获取数据。
            _dgvTags.RowCount = _gridTags.Count;
            _dgvTags.Invalidate();
        }

        /// <summary>
        /// N-10: 根据当前 ProgId 是否非空，统一控制「获取点位」「启动网关」按钮的可用状态。
        /// 未选择 OPC DA 服务器时，这两个按钮不可操作。
        /// </summary>
        private void UpdateDaButtonsState()
        {
            bool hasServer = !string.IsNullOrEmpty(_txtProgId?.Text?.Trim());
            _btnFetchTags.Enabled = hasServer;
            _btnStart.Enabled = hasServer;
        }

        // ================================================================
        //  服务器选择 & 点位获取
        // ================================================================

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (var dialog = new ServerSelectionDialog(_txtProgId.Text.Trim()))
            {
                var result = dialog.ShowDialog(this);
                if (result == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedProgId))
                {
                    _txtProgId.Text = dialog.SelectedProgId;
                    if (dialog.SelectedServer != null)
                    {
                        _lblCurrentServer.Text = dialog.SelectedServer.Description ?? "";
                        _lblCurrentServer.ForeColor = Color.DarkGreen;
                    }
                    _configMgr.SaveProgId(dialog.SelectedProgId);
                    // V1.9.0: 选择服务器后启用「获取点位」「启动网关」
                    UpdateDaButtonsState();
                }
            }
        }

        private async void BtnFetchTags_Click(object sender, EventArgs e)
        {
            string progId = _txtProgId.Text.Trim();
            if (string.IsNullOrEmpty(progId))
            {
                MessageBox.Show("请先选择 OPC DA 服务器！\n点击\"浏览...\"按钮扫描本机服务器。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 立即打开浏览窗口，浏览操作在窗口内后台执行
            _btnFetchTags.Enabled = false;
            _lblCurrentServer.Text = "正在获取点位...";
            _lblCurrentServer.ForeColor = Color.Blue;
            _log.Append($"正在从 [{progId}] 获取所有点位...");

            try
            {
                // 立即创建并显示对话框，浏览操作在对话框内后台执行
                using (var dialog = new ItemSelectionDialog(progId, _log.Append))
                {
                    _btnFetchTags.Enabled = true;  // 重新启用，对话框内有自己的UI控制
                    
                    var result = dialog.ShowDialog(this);
                    
                    if (result == DialogResult.OK && dialog.SelectedTags != null && dialog.SelectedTags.Count > 0)
                    {
                        foreach (var tag in dialog.SelectedTags)
                            tag.DisplayName = $"{progId}_{tag.ItemId}";

                        Config.OpcDa.Tags = dialog.SelectedTags;
                        // TagKeys 已在对话框 BtnOK_Click 中分配（Task #36），无需重复调用
                        UpdateTagGrid(dialog.SelectedTags);
                        // P1-1: 标签数据写入独立的 tags.json，网关配置写入 config.json
                        _configMgr.SaveTagsImmediate();
                        _configMgr.Save();

                        _lblCurrentServer.Text = $"已获取 {dialog.SelectedTags.Count} 个点位";
                        _lblCurrentServer.ForeColor = Color.DarkGreen;
                        _log.Append($"已保存 {dialog.SelectedTags.Count} 个点位到配置");
                    }
                    else
                    {
                        _lblCurrentServer.Text = "获取点位已取消";
                        _lblCurrentServer.ForeColor = Color.Gray;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Append($"获取点位失败: {ex.Message}");
                MessageBox.Show($"获取点位失败:\n{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _lblCurrentServer.Text = "获取点位失败";
                _lblCurrentServer.ForeColor = Color.Red;
            }
            finally
            {
                _btnFetchTags.Enabled = true;
            }
        }

        // ================================================================
        //  配置加载
        // ================================================================

        private void LoadConfiguration()
        {
            if (!_configMgr.Load())
            {
                // C-14 修复：配置加载失败时禁用所有交互控件，防止 null 引用
                _btnStart.Enabled = false;
                _btnStop.Enabled = false;
                _btnExportTags.Enabled = false;
                _chkAutoConnectDa.Enabled = false;
                _chkAutoStartModbus.Enabled = false;
                _chkEnableWatchdog.Enabled = false;
                _chkAutoStartWin.Enabled = false;
                return;
            }

            // 显示当前服务器 ProgId
            _txtProgId.Text = Config.OpcDa.ServerProgId ?? "";

            _log.Append("配置加载成功");
            _log.CleanupOldFiles();
            _log.Append($"  OPC DA 服务器: {Config.OpcDa.ServerProgId}");
            _log.Append($"  刷新频率: {Config.OpcDa.UpdateRateMs} ms");
            _log.Append($"  数据获取: {Config.OpcDa.GetEffectiveMode()}");
            _log.Append($"  标签数量: {Config.OpcDa.Tags?.Count ?? 0}");
            _log.Append($"  Modbus TCP 端口: {Config.ModbusTcp.Port}");
            _log.Append($"  Modbus TCP 监听: {Config.ModbusTcp.GetEffectiveListenAddress()}");
            _log.Append($"  Modbus TCP 安全: 无");
            _log.Append($"  Modbus TCP 端点: {Config.ModbusTcp.GetEndpointUrl()}");
            _log.Append($"  上次连接: {Config.LastConnectedProgId ?? "无"}");
            _log.Append($"  自动连接 DA: {Config.AutoConnectDa}");
            _log.Append($"  自动启动网关: {Config.AutoStartModbus}");
            _log.Append($"  开机启动: {Config.AutoStartWithWindows}");
            _log.Append($"  进程守护: {Config.EnableWatchdog}");

            // 加载自动选项的复选框状态（用标志位防止触发保存）
            _isLoadingConfig = true;
            _cmbDaMode.SelectedItem = Config.OpcDa.GetEffectiveMode() == DaAcquisitionMode.Sync ? "同步轮询" : "异步订阅";
            _chkAutoConnectDa.Checked = Config.AutoConnectDa;
            _chkAutoStartModbus.Checked = Config.AutoStartModbus;
            _chkAutoStartWin.Checked = Config.AutoStartWithWindows;
            _chkEnableWatchdog.Checked = Config.EnableWatchdog;

            string listenAddr = Config.ModbusTcp.GetEffectiveListenAddress();
            _cmbListenAddress.SelectedItem = _cmbListenAddress.Items.Contains(listenAddr) ? (object)listenAddr : "localhost";
            _nudUaPort.Value = (Config.ModbusTcp?.Port > 0) ? Config.ModbusTcp.Port : 502;
            _nudSlaveId.Value = (Config.ModbusTcp?.SlaveId > 0) ? Config.ModbusTcp.SlaveId : 1;
            UpdateEndpointUrlLabel();

            _isLoadingConfig = false;

            // 初始化网关管理器
            _gatewayMgr = new GatewayManager(_log, Config);
            _healthSnapshot = new HealthSnapshot(_gatewayMgr, _log);
            _healthSnapshot.OnAlert += (msg, severity) =>
            {
                SafeInvoke(() => ShowMemoryAlert(msg, severity));
            };

            _gatewayMgr.DaStatusChanged += (text, color) =>
            {
                SafeInvoke(() => { _lblDaStatus.Text = text; _lblDaStatus.ForeColor = color; });
            };
            _gatewayMgr.UaStatusChanged += (text, color) =>
            {
                SafeInvoke(() => { _lblUaStatus.Text = text; _lblUaStatus.ForeColor = color; });
            };
            _watchdogMgr.StatusChanged += (text, color) =>
            {
                SafeInvoke(() => { _lblWatchdogStatus.Text = text; _lblWatchdogStatus.ForeColor = color; });
            };

            _gatewayMgr.ConfigDirty += () => _configMgr?.Save();

            // H-40: 订阅配置文件外部修改事件
            _configMgr.ConfigFileChanged += () =>
            {
                if (_gatewayMgr?.IsRunning == true)
                {
                    _log.Append("[配置] 检测到外部修改，请重启网关以应用新配置");
                    SafeInvoke(() =>
                    {
                        _lblCurrentServer.Text = "(配置已变更，需重启)";
                        _lblCurrentServer.ForeColor = Color.Orange;
                    });
                }
                else
                {
                    _log.Append("[配置] 网关未运行，自动应用外部修改");
                    SafeInvoke(() =>
                    {
                        _isLoadingConfig = true;
                        if (_configMgr.Load())
                        {
                            string listenAddr = Config.ModbusTcp?.ListenAddress ?? "localhost";
                            _cmbListenAddress.SelectedItem = _cmbListenAddress.Items.Contains(listenAddr) ? (object)listenAddr : "localhost";
                            _nudUaPort.Value = (Config.ModbusTcp?.Port > 0) ? Config.ModbusTcp.Port : 502;
                            _nudSlaveId.Value = (Config.ModbusTcp?.SlaveId > 0) ? Config.ModbusTcp.SlaveId : 1;
                            UpdateEndpointUrlLabel();
                            if (Config.OpcDa.Tags != null)
                                UpdateTagGrid(Config.OpcDa.Tags);
                        }
                        _isLoadingConfig = false;
                    });
                }
            };

            _gatewayMgr.RunningStateChanged += (isRunning) =>
            {
                // P2 修复：该事件由 GatewayManager 后台线程（StartAsync 经 ConfigureAwait(false) 后的延续）
                // 触发，直接操作控件会抛 Cross-thread 异常。统一经 SafeInvoke 封送回 UI 线程。
                SafeInvoke(() => SetUiRunningState(isRunning));
            };

            // 恢复上次连接的 ProgId
            if (string.IsNullOrEmpty(_txtProgId.Text) && !string.IsNullOrEmpty(Config.LastConnectedProgId))
            {
                _txtProgId.Text = Config.LastConnectedProgId;
                _lblCurrentServer.Text = "(上次连接)";
                _lblCurrentServer.ForeColor = Color.DarkGreen;
            }

            // V1.9.0: 配置加载后，根据 ProgId 是否非空启用按钮
            UpdateDaButtonsState();

            if (Config.OpcDa.Tags != null)
                UpdateTagGrid(Config.OpcDa.Tags);

            if (Config.EnableWatchdog)
            {
                _watchdogMgr.Start();
                _log.Append("[守护] 已开启进程守护");
            }

            if (Config.AutoStartModbus && Config.OpcDa.Tags?.Count > 0)
            {
                _log.Append("[自动启动] 检测到自动启动选项已启用，1 秒后启动网关...");
                _autoStartTimer = new Timer { Interval = 1000 };
                _autoStartTimer.Tick += (s, ev) =>
                {
                    _autoStartTimer.Stop();
                    _autoStartTimer.Dispose();
                    _autoStartTimer = null;
                    HideToTray();
                    BtnStart_Click(null, EventArgs.Empty);
                };
                _autoStartTimer.Start();
            }
            else if (Config.AutoConnectDa && !string.IsNullOrEmpty(Config.OpcDa.ServerProgId))
            {
                _log.Append($"[自动连接] 已恢复到上次连接的服务器: {Config.LastConnectedProgId}");
            }

            // 初始化授权管理器
            _licenseMgr = new LicenseManager(_log, _configMgr, () => { });
            _licenseMgr.StatusChanged += (text, color) =>
            {
                SafeInvoke(() => { _lblLicenseStatus.Text = text; _lblLicenseStatus.ForeColor = color; });
            };
            _licenseMgr.GatewayStopRequested += async () =>
            {
                // 试用到期，停止网关 (在 UI 线程上执行)
                _log.Append("[授权] 正在停止网关...");
                _btnStop.Enabled = false;
                try
                {
                    if (_gatewayMgr?.IsRunning == true)
                        await _gatewayMgr.StopAsync();
                }
                catch (Exception ex)
                {
                    _log.Append($"[授权] 停止网关失败: {ex.Message}");
                }

                _btnStart.Enabled = false;
                _btnBrowse.Enabled = false;
                _btnFetchTags.Enabled = false;

                MessageBox.Show(
                    "软件试用期（30 分钟）已到，网关已自动停止。\n\n" +
                    "请获取授权码后在\"关于\"窗口中输入以继续使用。\n" +
                    "关闭此提示后程序将退出。",
                    "试用到期", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                _forceClose = true;
                _notifyIcon.Visible = false;
                Close();
            };
        }

        // ================================================================
        //  启动 / 停止网关
        // ================================================================

        private async void BtnStart_Click(object sender, EventArgs e)
        {
            if (Config == null) return;

            if (_licenseMgr.IsTrialExpired)
            {
                MessageBox.Show(
                    "软件试用期已到（30 分钟），网关无法继续运行。\n\n请获取授权码后在\"关于\"窗口中输入以继续使用。",
                    "授权限制", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string progId = _txtProgId.Text.Trim();

            try
            {
                if (string.IsNullOrEmpty(progId))
                    throw new InvalidOperationException("请先选择 OPC DA 服务器！\n点击\"浏览...\"按钮扫描本机已安装的 OPC DA 服务器。");

                Config.OpcDa.ServerProgId = progId;

                if (Config.OpcDa?.Tags == null || Config.OpcDa.Tags.Count == 0)
                    throw new InvalidOperationException("尚未获取 OPC DA 服务器的点位！\n请先点击\"获取点位...\"按钮浏览并选择要桥接的标签。");

                _log.Append("========================================");
                _log.Append("正在启动网关...");

                // V1.8.1: 进度回调 — 通过 SynchronizationContext.Post 将进度报告安全地投递到 UI 线程，
                // 确保日志框实时更新而不会阻塞 UI。
                var syncCtx = System.Threading.SynchronizationContext.Current;
                Action<string> report = null;
                report = msg =>
                {
                    if (syncCtx != null)
                        syncCtx.Post(_ => _log.Append(msg), null);
                    else
                        _log.Append(msg);
                };

                // 启动网关
                await _gatewayMgr.StartAsync(report);

                Config.LastConnectedProgId = progId;
                _configMgr.Save();
                _log.Append("========================================");

                _refreshTimer.Start();

                _healthTimer = new Timer { Interval = AppConstants.HealthCheckIntervalMs };
                _healthTimer.Tick += (s2, e2) => _gatewayMgr?.CheckHealth();
                _healthTimer.Start();

                _btnStop.Enabled = true;
                _btnExportTags.Enabled = true;
            }
            catch (InvalidOperationException ex)
            {
                // 预条件验证失败（如无 ProgId、无标签）
                MessageBox.Show(ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _log.Append($"启动失败: {ex.Message}");
                MessageBox.Show($"启动失败:\n\n{ex.Message}", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _refreshTimer?.Stop();
            }
        }

        private async void BtnStop_Click(object sender, EventArgs e)
        {
            _btnStop.Enabled = false;
            _refreshTimer.Stop();
            _healthTimer?.Stop();
            _healthTimer?.Dispose();
            _healthTimer = null;

            try
            {
                await _gatewayMgr.StopAsync();
            }
            catch (Exception ex)
            {
                _log.Append($"停止网关失败: {ex.Message}");
            }
        }

        // ================================================================
        //  导出 Modbus TCP 点表完整信息
        // ================================================================

        private async void BtnExportTags_Click(object sender, EventArgs e)
        {
            if (Config?.OpcDa?.Tags == null || Config.OpcDa.Tags.Count == 0)
            {
                MessageBox.Show("当前没有点位可导出。\n请先获取点位。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var sfd = new SaveFileDialog())
            {
                sfd.Title = "导出 Modbus TCP 点表";
                sfd.Filter = "CSV 文件 (*.csv)|*.csv";
                sfd.FileName = $"Modbus点表_{Config.OpcDa.ServerProgId}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                sfd.DefaultExt = "csv";

                if (sfd.ShowDialog(this) == DialogResult.OK)
                {
                    _btnExportTags.Enabled = false;
                    try
                    {
                        int tagCount = Config.OpcDa.Tags.Count;
                        var modbusServer = _gatewayMgr?.ModbusServer;
                        ushort slaveId = (modbusServer != null && modbusServer.IsRunning && modbusServer.SlaveId > 0)
                            ? modbusServer.SlaveId
                            : ((ushort)1);
                        string endpointUrl = Config.ModbusTcp?.GetEndpointUrl() ?? "";
                        var tags = Config.OpcDa.Tags;
                        string progId = Config.OpcDa.ServerProgId ?? "";

                        _log.Append($"正在生成 Modbus TCP 点表 ({tagCount} 个点位)...");

                        await Task.Run(() =>
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine("# Modbus TCP 点表完整信息");
                            sb.AppendLine($"# 导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                            sb.AppendLine($"# DA 服务器: {progId}");
                            sb.AppendLine($"# Modbus 端点: {endpointUrl}");
                            sb.AppendLine($"# Modbus 从站 ID: {slaveId}");
                            sb.AppendLine($"# 点位数: {tagCount}");
                            sb.AppendLine();
                            sb.AppendLine("序号,DA_ItemId,DisplayName,BrowseName,DA_DataType,ModbusAddress,RegisterType,EndpointUrl,ModbusPath");

                            int idx = 1;
                            foreach (var tag in tags)
                            {
                                string itemId = tag.ItemId ?? "";
                                string displayName = tag.DisplayName ?? itemId;
                                string tagKey = tag.TagKey ?? itemId;
                                string dataType = tag.DataType ?? "Variant";
                                string modbusAddress = tag.ModbusAddress.ToString();
                                string registerType = tag.GetEffectiveRegisterType().ToString();
                                string browsePath = EscapeCsv(GatewayModbusTcpServer.ComputeModbusPath(itemId, displayName));

                                sb.Append(idx); sb.Append(',');
                                sb.Append(EscapeCsv(itemId)); sb.Append(',');
                                sb.Append(EscapeCsv(displayName)); sb.Append(',');
                                sb.Append(EscapeCsv(itemId)); sb.Append(',');
                                sb.Append(EscapeCsv(dataType)); sb.Append(',');
                                sb.Append(modbusAddress); sb.Append(',');
                                sb.Append(registerType); sb.Append(',');
                                sb.Append(EscapeCsv(endpointUrl)); sb.Append(',');
                                sb.Append(browsePath);
                                sb.AppendLine();
                                idx++;
                            }

                            System.IO.File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true));
                        });

                        _log.Append($"Modbus TCP 点表已导出: {sfd.FileName} ({tagCount} 个点位)");

                        MessageBox.Show(
                            $"Modbus TCP 点表导出成功！\n" +
                            $"文件: {sfd.FileName}\n" +
                            $"共 {tagCount} 个点位\n" +
                            $"从站 ID: {slaveId}",
                            "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"导出失败:\n{ex.Message}", "错误",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    finally
                    {
                        _btnExportTags.Enabled = true;
                    }
                }
            }
        }

        private void RefreshStats()
        {
            if (_gatewayMgr?.Bridge == null) return;

            var bridge = _gatewayMgr.Bridge;
            _lblStats.Text = $"更新: {bridge.TotalUpdates} | 错误: {bridge.ErrorCount}";

            // P1-4 自适应刷新: 比较快照哈希，无变化时放宽到 3s，有变化时恢复 1s
            _cachedSnapshots = bridge.GetSnapshots();
            int hash = _cachedSnapshots?.Count ?? 0;
            if (_cachedSnapshots != null && _cachedSnapshots.Count > 0)
            {
                // H-39: 聚合所有快照的 Value 哈希，而非仅第 0 行。
                //       确保任何一行数据变化都能触发快速刷新（1s 间隔）。
                //       使用 XOR 聚合避免遍历中溢出，50000 行约 0.5ms。
                foreach (var snap in _cachedSnapshots)
                    hash ^= (snap.Value?.GetHashCode() ?? 0);
            }

            if (hash != _lastSnapshotHash)
            {
                _lastSnapshotHash = hash;
                if (_refreshTimer != null && _refreshTimer.Interval != AppConstants.UiDefaultRefreshMs)
                    _refreshTimer.Interval = AppConstants.UiDefaultRefreshMs;
            }
            else
            {
                if (_refreshTimer != null && _refreshTimer.Interval != AppConstants.UiSlowRefreshMs)
                    _refreshTimer.Interval = AppConstants.UiSlowRefreshMs;
            }

            // 虚拟模式：不需要逐行设置 Cell.Value。
            // 只需 Invalidate 可见区域，DataGridView 会通过 CellValueNeeded 事件按需获取最新数据。
            if (_dgvTags.RowCount > 0)
            {
                int firstVisible;
                try { firstVisible = _dgvTags.FirstDisplayedScrollingRowIndex; }
                catch { firstVisible = 0; }
                if (firstVisible < 0) firstVisible = 0;

                int rowHeight = _dgvTags.RowTemplate.Height > 0 ? _dgvTags.RowTemplate.Height : 22;
                int visibleCount = (_dgvTags.DisplayRectangle.Height / rowHeight) + 2;
                int lastVisible = Math.Min(firstVisible + visibleCount, _dgvTags.RowCount);

                // 仅使可见行失效，触发 CellValueNeeded 重新获取数据
                for (int i = firstVisible; i < lastVisible; i++)
                    _dgvTags.InvalidateRow(i);
            }
        }

        /// <summary>
        /// 虚拟模式回调：DataGridView 渲染每个可见单元格时调用，按需提供数据。
        /// 这是 50000 行场景下唯一的数据供给路径，避免了预先创建所有行对象。
        /// </summary>
        private void DgvTags_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (_gridTags == null || e.RowIndex >= _gridTags.Count) return;

            var tag = _gridTags[e.RowIndex];

            switch (e.ColumnIndex)
            {
                case 0: // 标签名称
                    e.Value = tag.DisplayName;
                    break;
                case 1: // ItemId
                    e.Value = tag.ItemId;
                    break;
                case 2: // 当前值
                case 3: // 质量
                case 4: // 时间戳
                    // 从缓存的快照引用获取实时数据（每个刷新周期只构建一次）
                    if (_cachedSnapshots != null && e.RowIndex < _cachedSnapshots.Count)
                    {
                        var snap = _cachedSnapshots[e.RowIndex];
                        switch (e.ColumnIndex)
                        {
                            case 2: e.Value = snap.Value; break;
                            case 3: e.Value = snap.Quality; break;
                            case 4: e.Value = snap.Timestamp; break;
                        }
                    }
                    else
                    {
                        e.Value = "-";
                    }
                    break;
            }
        }

        /// <summary>
        /// 虚拟模式回调：设置单元格显示样式（质量列颜色）。
        /// </summary>
        private void DgvTags_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.ColumnIndex == 3 && e.Value is string quality) // 质量列
            {
                e.CellStyle.ForeColor = quality == "Good" ? Color.Green : Color.Red;
            }
        }

        // ================================================================
        //  系统托盘
        // ================================================================

        private void ShowMainWindow()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        private void HideToTray()
        {
            Hide();
            ShowInTaskbar = false;
            _notifyIcon.ShowBalloonTip(2000, "OPC DA → Modbus TCP 网关",
                "程序已最小化到系统托盘，双击图标可打开主窗口。", ToolTipIcon.Info);
        }

        private void ExitApplication()
        {
            _forceClose = true;
            _notifyIcon.Visible = false;
            Close();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
                ShowInTaskbar = false;
            }
        }

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_forceClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            // C-15 修复：第二次 Close() 时 _isShuttingDown 已为 true，
            // 跳过异步关闭逻辑但必须执行资源释放
            if (_closeInProgress && !_isShuttingDone)
                return;

            if (!_isShuttingDown)
            {
                e.Cancel = true;
                _isShuttingDown = true;
                _closeInProgress = true;

                // R-2 修复：移除重复的 Timer Stop/Dispose（H-29 引入的冗余代码）。
                // 仅保留捕获局部引用 → 置 null → Dispose 的单一路径，
                // 避免对已释放的 Timer 重复调用 Stop/Dispose。
                var refreshTimer = _refreshTimer;
                var healthTimer = _healthTimer;
                var autoStartTimer = _autoStartTimer;
                _refreshTimer = null;
                _healthTimer = null;
                _autoStartTimer = null;

                refreshTimer?.Stop();
                refreshTimer?.Dispose();
                healthTimer?.Stop();
                healthTimer?.Dispose();
                autoStartTimer?.Stop();
                autoStartTimer?.Dispose();

                // 主程序退出时发送优雅退出信号，看门狗保持运行但不重启主进程
                // 只有取消勾选"进程守护"复选框时才会真正停止看门狗
                try { _watchdogMgr?.SignalGracefulExit(); }
                catch (Exception ex) { _log?.Append($"关闭时发送退出信号异常: {ex.Message}"); }

                // P0 修复：try-catch 包裹 StopAsync，防止异常导致窗口永远无法关闭
                try
                {
                    if (_gatewayMgr != null)
                        await _gatewayMgr.StopAsync();
                }
                catch (Exception ex)
                {
                    _log?.Append($"关闭时停止网关异常: {ex.Message}");
                }

                _forceClose = true;
                _isShuttingDone = true;
                Close();
                return;
            }

            // C-15 修复：确保资源释放在第二次 Close() 时执行
            _log?.Dispose();
            _notifyIcon?.Dispose();
            _licenseMgr?.Dispose();
            try { _healthSnapshot?.Dispose(); } catch { } // H-36
            try { _configMgr?.Dispose(); } catch { } // H-40 + V1.9.0: ConfigManager 现实现 IDisposable
            base.OnFormClosing(e);
        }

        // ================================================================
        //  辅助方法
        // ================================================================

        /// <summary>
        /// 线程安全地执行 UI 操作。
        /// 增加句柄防护：窗体未创建句柄或已释放时直接跳过，避免关闭/初始化期间
        /// 后台事件（如 HealthSnapshot 定时器、LicenseManager 试用到期）触发 Invoke 抛
        /// ObjectDisposedException 或跨线程异常。
        /// </summary>
        private void SafeInvoke(Action a)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) Invoke(a); else a();
        }

        /// <summary>
        /// CSV 字段转义：处理逗号、双引号和换行符。
        /// </summary>
        private static string EscapeCsv(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n"))
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }
    }
}
