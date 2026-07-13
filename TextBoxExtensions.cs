using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OpcDaToUaGateway
{
    /// <summary>
    /// P3 修复：提取共享的 TextBox 扩展方法，消除 AboutDialog/ServerSelectionDialog 中的重复定义
    /// 使用 Win32 EM_SETCUEBANNER 消息实现占位符文本（.NET 4.7.2 不支持 PlaceholderText 属性）
    /// </summary>
    public static class TextBoxExtensions
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>
        /// 设置 TextBox 的占位符提示文本
        /// </summary>
        public static void PlaceholderText(this TextBox textBox, string text)
        {
            if (textBox == null) return;
            SendMessage(textBox.Handle, EM_SETCUEBANNER, IntPtr.Zero, text ?? "");
        }
    }
}
