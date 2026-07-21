using System.Drawing;
using System.Windows.Forms;

namespace OpcDaToModbusGateway
{
    /// <summary>
    /// 全局视觉主题 — 统一配色、字体、间距常量
    /// 所有窗口共享此主题，确保视觉一致性
    /// </summary>
    internal static class Theme
    {
        // ---- 品牌色 ----
        public static readonly Color Primary       = Color.FromArgb(33, 150, 243);   // 主蓝
        public static readonly Color PrimaryDark    = Color.FromArgb(21, 101, 192);   // 深蓝（表头、强调）
        public static readonly Color PrimaryLight   = Color.FromArgb(232, 240, 254);  // 浅蓝（交替行）

        // ---- 功能色 ----
        public static readonly Color Success  = Color.FromArgb(76, 175, 80);    // 启动 / Good
        public static readonly Color Danger   = Color.FromArgb(244, 67, 54);    // 停止 / Bad
        public static readonly Color Warning  = Color.FromArgb(255, 152, 0);    // 警告 / 导入
        public static readonly Color Purple   = Color.FromArgb(156, 39, 176);   // 导出
        public static readonly Color Neutral  = Color.FromArgb(96, 125, 139);   // 中性按钮（关于、复制）

        // ---- 背景 / 表面 ----
        public static readonly Color FormBg        = Color.FromArgb(245, 245, 245);  // 窗体底色
        public static readonly Color Surface        = Color.White;                     // 卡片 / GroupBox
        public static readonly Color TerminalBg     = Color.FromArgb(30, 30, 30);     // 日志区底色

        // ---- 文字 ----
        public static readonly Color TextPrimary   = Color.FromArgb(33, 33, 33);
        public static readonly Color TextSecondary = Color.FromArgb(117, 117, 117);
        public static readonly Color TextOnDark    = Color.FromArgb(208, 208, 208);  // 日志区文字
        public static readonly Color TextOnPrimary = Color.White;

        // ---- 边框 ----
        public static readonly Color Border = Color.FromArgb(224, 224, 224);

        // ---- DataGridView 预设样式（直接应用到实例） ----
        /// <summary>将统一的表头 / 行 / 选中样式应用到 DataGridView</summary>
        public static void ApplyGridStyle(DataGridView dgv)
        {
            // 表头
            dgv.EnableHeadersVisualStyles = false;
            dgv.ColumnHeadersDefaultCellStyle.BackColor = PrimaryDark;
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = TextOnPrimary;
            dgv.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            dgv.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            dgv.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            dgv.ColumnHeadersHeight = 36;
            dgv.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

            // 行
            dgv.AlternatingRowsDefaultCellStyle.BackColor = PrimaryLight;
            dgv.DefaultCellStyle.SelectionBackColor = Primary;
            dgv.DefaultCellStyle.SelectionForeColor = TextOnPrimary;
            dgv.DefaultCellStyle.Padding = new Padding(2, 0, 2, 0);
            dgv.RowTemplate.Height = 26;

            // 网格线
            dgv.GridColor = Border;
            dgv.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        }
    }
}
