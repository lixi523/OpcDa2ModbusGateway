using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace OpcDaToModbusGateway
{
    /// <summary>
    /// 关于对话框 - 显示程序版本、版权、技术信息、PCID 和授权码输入
    /// </summary>
    public class AboutDialog : Form
    {
        // P5 修复：版本号统一从程序集获取，避免多处硬编码
        private static readonly string AppVersion =
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        private readonly string _pcid;
        private readonly bool _isLicensed;

        // H6 修复：追踪所有创建的 Font 对象，在关闭时统一释放 GDI 句柄
        private readonly List<Font> _ownedFonts = new List<Font>();

        /// <summary>创建字体并加入跟踪列表，关闭时自动释放</summary>
        private Font OwnedFont(string family, float size, FontStyle style = FontStyle.Regular)
        {
            var f = new Font(family, size, style);
            _ownedFonts.Add(f);
            return f;
        }

        /// <summary>
        /// 用户输入的授权码（DialogResult.OK 时返回，为空表示未提交）
        /// </summary>
        public string AuthorizationCode { get; private set; }

        public AboutDialog(string pcid, bool isLicensed)
        {
            _pcid = pcid ?? "UNKNOWN";
            _isLicensed = isLicensed;
            BuildUI();
        }

        private void BuildUI()
        {
            Text = "关于 OPC DA → Modbus TCP 网关";
            Size = new Size(480, 580);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = OwnedFont("Microsoft YaHei UI", 9f);
            BackColor = Color.White;

            // 顶部渐变标题区
            var headerPanel = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(ClientSize.Width, 90),
                BackColor = Color.FromArgb(33, 150, 243)
            };

            // 加载图标（P2 修复：Icon 使用后 Dispose）
            Image iconImage = null;
            string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(iconPath))
            {
                try
                {
                    using (var ico = new Icon(iconPath, 48, 48))
                        iconImage = ico.ToBitmap();
                }
                catch { }
            }

            if (iconImage != null)
            {
                var picIcon = new PictureBox
                {
                    Image = iconImage,
                    Location = new Point(16, 20),
                    Size = new Size(48, 48),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    BackColor = Color.Transparent
                };
                headerPanel.Controls.Add(picIcon);
            }

            var lblTitle = new Label
            {
                Text = "OPC DA → Modbus TCP 网关",
                Location = new Point(iconImage != null ? 76 : 16, 18),
                AutoSize = true,
                Font = OwnedFont("Microsoft YaHei UI", 14f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent
            };

            var lblVersion = new Label
            {
                Text = $"版本 {AppVersion}",
                Location = new Point(iconImage != null ? 76 : 16, 50),
                AutoSize = true,
                Font = OwnedFont("Microsoft YaHei UI", 10f),
                ForeColor = Color.FromArgb(220, 255, 255, 255),
                BackColor = Color.Transparent
            };

            headerPanel.Controls.AddRange(new Control[] { lblTitle, lblVersion });
            Controls.Add(headerPanel);

            // 信息区域
            int infoY = 105;

            var infoPanel = new Panel
            {
                Location = new Point(16, infoY),
                Size = new Size(432, 180)
            };

            string[] infoLines = new string[]
            {
                "OPC DA 到 Modbus TCP 协议转换网关",
                "将传统 OPC DA(COM 协议)数据实时桥接到 Modbus TCP(TCP 协议)。",
                "遇到bug请联系18510086469,408738480@qq.com",
                "技术栈:",
                "  .NET Framework 4.7.2 (x86)",
                "  NModbus4 (Modbus TCP slave library)",
                "  TitaniumAS.Opc.Client 1.0.2",
                "编译信息:",
                $"  框架版本: {Environment.Version}",
                $"  操作系统: {Environment.OSVersion.VersionString}",
                $"  CLR 位数: {IntPtr.Size * 8} 位",
            };

            int lineY = 0;
            foreach (string line in infoLines)
            {
                bool isHeader = line.EndsWith(":") && !line.StartsWith(" ");
                var lbl = new Label
                {
                    Text = line,
                    Location = new Point(0, lineY),
                    Size = new Size(432, isHeader ? 20 : 18),
                    Font = isHeader
                        ? OwnedFont("Microsoft YaHei UI", 9f, FontStyle.Bold)
                        : OwnedFont("Consolas", 9f),
                    ForeColor = isHeader ? Color.FromArgb(33, 33, 33) : Color.FromArgb(80, 80, 80)
                };
                infoPanel.Controls.Add(lbl);
                lineY += lbl.Height;
            }

            Controls.Add(infoPanel);

            // ================================================================
            //  授权信息区域
            // ================================================================
            int authY = 300;

            var grpAuth = new GroupBox
            {
                Text = "授权信息",
                Location = new Point(16, authY),
                Size = new Size(432, 160)
            };

            // 授权状态
            var lblAuthState = new Label
            {
                Text = "当前状态:",
                Location = new Point(15, 25),
                AutoSize = true,
                Font = OwnedFont("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };

            var lblAuthValue = new Label
            {
                Text = _isLicensed ? "已授权" : "未授权（试用中）",
                Location = new Point(85, 25),
                AutoSize = true,
                ForeColor = _isLicensed ? Color.Green : Color.OrangeRed
            };

            // PCID 显示
            var lblPcidLabel = new Label
            {
                Text = "机器码 (PCID):",
                Location = new Point(15, 55),
                AutoSize = true,
                Font = OwnedFont("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };

            var txtPcid = new TextBox
            {
                Text = _pcid,
                Location = new Point(120, 52),
                Size = new Size(200, 25),
                ReadOnly = true,
                BackColor = Color.FromArgb(245, 245, 245),
                Font = OwnedFont("Consolas", 10f),
                BorderStyle = BorderStyle.FixedSingle
            };

            var btnCopyPcid = new Button
            {
                Text = "复制",
                Location = new Point(330, 51),
                Size = new Size(55, 27),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Neutral,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnCopyPcid.FlatAppearance.BorderSize = 0;
            btnCopyPcid.Click += (s, ev) =>
            {
                try
                {
                    Clipboard.SetText(_pcid);
                    MessageBox.Show("PCID 已复制到剪贴板", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
            };

            // 授权码输入
            var lblAuthCode = new Label
            {
                Text = "授权码:",
                Location = new Point(15, 90),
                AutoSize = true,
                Font = OwnedFont("Microsoft YaHei UI", 9f, FontStyle.Bold)
            };

            var txtAuthCode = new TextBox
            {
                Location = new Point(120, 87),
                Size = new Size(200, 25),
                Font = OwnedFont("Consolas", 9.5f),
                BorderStyle = BorderStyle.FixedSingle
            };
            txtAuthCode.PlaceholderText("XXXX-XXXX-XXXX-XXXX-XXXX");

            var btnVerify = new Button
            {
                Text = "验证授权",
                Location = new Point(330, 86),
                Size = new Size(80, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Success,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnVerify.FlatAppearance.BorderSize = 0;
            btnVerify.Click += (s, ev) =>
            {
                string code = txtAuthCode.Text.Trim();
                if (string.IsNullOrEmpty(code))
                {
                    MessageBox.Show("请输入授权码", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                AuthorizationCode = code;
                DialogResult = DialogResult.OK;
                Close();
            };

            // 授权码格式提示
            var lblFormatHint = new Label
            {
                Text = "格式: XXXX-XXXX-XXXX-XXXX-XXXX（不区分大小写）",
                Location = new Point(120, 118),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = OwnedFont("Microsoft YaHei UI", 8f)
            };

            grpAuth.Controls.AddRange(new Control[] {
                lblAuthState, lblAuthValue,
                lblPcidLabel, txtPcid, btnCopyPcid,
                lblAuthCode, txtAuthCode, btnVerify,
                lblFormatHint
            });
            Controls.Add(grpAuth);

            // 底部版权
            var lblCopyright = new Label
            {
                Text = "Copyright © 2026  OPC DA to Modbus TCP Gateway",
                Location = new Point(16, 478),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = OwnedFont("Microsoft YaHei UI", 8.5f)
            };
            Controls.Add(lblCopyright);

            // 确定按钮（关闭，不提交授权码）
            var btnOk = new Button
            {
                Text = "关闭",
                Location = new Point(ClientSize.Width - 95, ClientSize.Height - 40),
                Size = new Size(80, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Theme.Neutral,
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnOk.FlatAppearance.BorderSize = 0;
            btnOk.Click += (s, e) =>
            {
                AuthorizationCode = null; // 不提交授权码
                DialogResult = DialogResult.Cancel;
                Close();
            };
            Controls.Add(btnOk);

            CancelButton = btnOk;
        }

        /// <summary>
        /// H6 修复：窗口关闭时统一释放所有 Font GDI 句柄
        /// </summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            foreach (var font in _ownedFonts)
            {
                try { font.Dispose(); } catch { }
            }
            _ownedFonts.Clear();
        }
    }
}
