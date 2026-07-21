using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace OpcDaToModbusGateway
{
    /// <summary>
    /// OPC DA 服务器选择对话框
    /// 扫描本机上已安装的 OPC DA 服务器，允许用户可视化浏览和选择
    /// </summary>
    public class ServerSelectionDialog : Form
    {
        private DataGridView _dgvServers;
        private Button _btnRefresh;
        private Button _btnSelect;
        private Button _btnCancel;
        private TextBox _txtFilter;
        private Label _lblFilter;
        private Label _lblStatus;
        private ProgressBar _progressBar;

        private List<OpcServerInfo> _allServers = new List<OpcServerInfo>();

        /// <summary>
        /// 用户选中的服务器 ProgId
        /// </summary>
        public string SelectedProgId { get; private set; }

        /// <summary>
        /// 用户选中的服务器完整信息
        /// </summary>
        public OpcServerInfo SelectedServer { get; private set; }

        public ServerSelectionDialog(string currentProgId = null)
        {
            BuildUI();
            SelectedProgId = currentProgId;

            // 窗体加载后自动扫描
            this.Load += (s, e) =>
            {
                ScanServers();
                // 如果有当前选中的服务器，高亮它
                if (!string.IsNullOrEmpty(currentProgId))
                {
                    HighlightServer(currentProgId);
                }
            };
        }

        // ================================================================
        //  UI 构建
        // ================================================================

        private void BuildUI()
        {
            Text = "选择 OPC DA 服务器";
            Size = new Size(750, 520);
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(600, 400);
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Theme.FormBg;

            int y = 10;

            // ---- 提示信息 ----
            var lblTip = new Label
            {
                Text = "以下是在本机上检测到的 OPC DA 服务器。双击或点击\"选择\"按钮确认。",
                Location = new Point(12, y),
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80)
            };
            Controls.Add(lblTip);
            y += 25;

            // ---- 筛选栏 ----
            _lblFilter = new Label
            {
                Text = "筛选:",
                Location = new Point(12, y + 3),
                AutoSize = true
            };
            Controls.Add(_lblFilter);

            _txtFilter = new TextBox
            {
                Location = new Point(50, y),
                Size = new Size(300, 25)
            };
            // PlaceholderText 是扩展方法，不能在对象初始化器中使用，需单独调用
            _txtFilter.PlaceholderText("输入关键字筛选（如 Matrikon、KEP）...");
            _txtFilter.TextChanged += (s, e) => ApplyFilter();
            Controls.Add(_txtFilter);

            _btnRefresh = new Button
            {
                Text = "重新扫描",
                Location = new Point(370, y - 1),
                Size = new Size(100, 28),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _btnRefresh.FlatAppearance.BorderColor = Theme.Border;
            _btnRefresh.Click += (s, e) => ScanServers();
            Controls.Add(_btnRefresh);

            y += 35;

            // ---- 服务器列表 ----
            _dgvServers = new DataGridView
            {
                Location = new Point(12, y),
                Size = new Size(710, 340),
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                RowHeadersVisible = false,
                Font = new Font("Consolas", 9f),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            _dgvServers.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colProgId",
                HeaderText = "ProgId",
                FillWeight = 35
            });
            _dgvServers.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colDesc",
                HeaderText = "描述 / 名称",
                FillWeight = 30
            });
            _dgvServers.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colClsid",
                HeaderText = "CLSID",
                FillWeight = 20
            });
            _dgvServers.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "colPath",
                HeaderText = "服务器路径",
                FillWeight = 30
            });

            // 双击选中
            _dgvServers.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0) ConfirmSelection();
            };

            // 应用统一视觉主题
            Theme.ApplyGridStyle(_dgvServers);

            Controls.Add(_dgvServers);
            y += 350;

            // ---- 状态栏 ----
            _lblStatus = new Label
            {
                Text = "就绪",
                Location = new Point(12, y + 5),
                AutoSize = true,
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            Controls.Add(_lblStatus);

            _progressBar = new ProgressBar
            {
                Location = new Point(200, y + 3),
                Size = new Size(150, 18),
                Style = ProgressBarStyle.Marquee,
                Visible = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            Controls.Add(_progressBar);

            // ---- 底部按钮 ----
            _btnSelect = new Button
            {
                Text = "选择",
                DialogResult = DialogResult.None,
                Location = new Point(530, y),
                Size = new Size(90, 32),
                BackColor = Theme.Primary,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnSelect.FlatAppearance.BorderSize = 0;
            _btnSelect.Click += (s, e) => ConfirmSelection();
            Controls.Add(_btnSelect);

            _btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Location = new Point(630, y),
                Size = new Size(90, 32),
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnCancel.FlatAppearance.BorderColor = Theme.Border;
            Controls.Add(_btnCancel);

            this.AcceptButton = _btnSelect;
            this.CancelButton = _btnCancel;
        }

        // ================================================================
        //  扫描服务器
        // ================================================================

        private void ScanServers()
        {
            _dgvServers.Rows.Clear();
            _lblStatus.Text = "正在扫描 OPC DA 服务器...";
            _lblStatus.ForeColor = Color.Blue;
            _progressBar.Visible = true;
            _btnRefresh.Enabled = false;
            this.Cursor = Cursors.WaitCursor;

            // 使用 BackgroundWorker 避免阻塞 UI
            var worker = new System.ComponentModel.BackgroundWorker();
            worker.DoWork += (s, e) =>
            {
                e.Result = OpcServerScanner.ScanServers();
            };
            worker.RunWorkerCompleted += (s, e) =>
            {
                // H5 修复：BackgroundWorker 使用完毕后释放资源
                (s as System.ComponentModel.BackgroundWorker)?.Dispose();

                this.Cursor = Cursors.Default;
                _progressBar.Visible = false;
                _btnRefresh.Enabled = true;

                if (e.Error != null)
                {
                    _lblStatus.Text = $"扫描出错: {e.Error.Message}";
                    _lblStatus.ForeColor = Color.Red;
                    return;
                }

                _allServers = (List<OpcServerInfo>)e.Result;
                PopulateGrid(_allServers);

                if (_allServers.Count == 0)
                {
                    _lblStatus.Text = "未找到 OPC DA 服务器。可点击[重新扫描]或在主界面直接手动输入 ProgId。";
                    _lblStatus.ForeColor = Color.DarkOrange;

                    // 显示诊断日志
                    var diagLog = OpcServerScanner.GetDiagnosticLog();
                    if (diagLog.Count > 0)
                    {
                        var diagMsg = string.Join("\r\n", diagLog);
                        MessageBox.Show(
                            "扫描诊断信息（可据此排查问题）：\r\n\r\n" + diagMsg +
                            "\r\n\r\n常见原因：\r\n" +
                            "1. 未安装 OPC DA 服务器软件\r\n" +
                            "2. 未安装 OPC 核心组件（OPC Core Components）\r\n" +
                            "3. OPC DA 服务器安装在远程机器上",
                            "扫描诊断",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                else
                {
                    _lblStatus.Text = $"扫描完成，找到 {_allServers.Count} 个 OPC DA 服务器";
                    _lblStatus.ForeColor = Color.Green;
                }
            };
            worker.RunWorkerAsync();
        }

        private void PopulateGrid(List<OpcServerInfo> servers)
        {
            _dgvServers.Rows.Clear();
            foreach (var server in servers)
            {
                int rowIndex = _dgvServers.Rows.Add(
                    server.ProgId,
                    server.Description ?? "(未命名)",
                    server.Clsid ?? "",
                    server.ServerPath ?? ""
                );

                // 根据是否有有效路径设置行样式
                var row = _dgvServers.Rows[rowIndex];
                if (string.IsNullOrEmpty(server.ServerPath))
                {
                    row.DefaultCellStyle.ForeColor = Color.Gray;
                }
            }
        }

        // ================================================================
        //  筛选
        // ================================================================

        private void ApplyFilter()
        {
            // P1 修复：使用 IndexOf + OrdinalIgnoreCase 避免 ToLowerInvariant 分配
            string filter = _txtFilter.Text.Trim();

            if (string.IsNullOrEmpty(filter))
            {
                PopulateGrid(_allServers);
                return;
            }

            var filtered = _allServers.FindAll(s =>
                (s.ProgId?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (s.Description?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (s.ServerPath?.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            );

            PopulateGrid(filtered);
        }

        // ================================================================
        //  选中 / 高亮
        // ================================================================

        private void HighlightServer(string progId)
        {
            for (int i = 0; i < _dgvServers.Rows.Count; i++)
            {
                string rowProgId = _dgvServers.Rows[i].Cells["colProgId"].Value?.ToString();
                if (string.Equals(rowProgId, progId, StringComparison.OrdinalIgnoreCase))
                {
                    _dgvServers.ClearSelection();
                    _dgvServers.Rows[i].Selected = true;
                    _dgvServers.CurrentCell = _dgvServers.Rows[i].Cells[0];
                    _dgvServers.FirstDisplayedScrollingRowIndex = Math.Max(0, i - 2);
                    break;
                }
            }
        }

        private void ConfirmSelection()
        {
            if (_dgvServers.SelectedRows.Count == 0)
            {
                MessageBox.Show("请先选择一个 OPC DA 服务器", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string progId = _dgvServers.SelectedRows[0].Cells["colProgId"].Value?.ToString();
            if (string.IsNullOrEmpty(progId))
            {
                MessageBox.Show("无法获取所选服务器的 ProgId", "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SelectedProgId = progId;

            // 查找完整的服务器信息
            SelectedServer = _allServers.Find(s =>
                string.Equals(s.ProgId, progId, StringComparison.OrdinalIgnoreCase));

            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
