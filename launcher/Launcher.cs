using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace WuwatappoNative.Launcher
{
    /// <summary>
    /// 外层只有一个可见 exe；真正的自包含程序在 app\ 下，与运行时 DLL 放在一起。
    /// 启动器只负责定位并启动 app\WuwatappoNative.exe，不加载任何运行时依赖。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string root = AppDomain.CurrentDomain.BaseDirectory;
            string target = Path.Combine(root, "app", "WuwatappoNative.exe");
            if (!File.Exists(target))
            {
                MessageBox.Show(
                    "找不到内部程序：\r\n" + target + "\r\n\r\n请确认 app 文件夹与启动器在同一目录。",
                    "Wuwatappo Native",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = target,
                    WorkingDirectory = Path.GetDirectoryName(target),
                    UseShellExecute = false,
                    Arguments = BuildArguments(args)
                };
                Process.Start(start);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "启动 Wuwatappo Native 失败：\r\n" + ex.Message,
                    "Wuwatappo Native",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return 1;
            }
        }

        static string BuildArguments(string[] args)
        {
            if (args == null || args.Length == 0) return "";
            var result = new StringBuilder();
            foreach (string arg in args)
            {
                if (result.Length > 0) result.Append(' ');
                result.Append(Quote(arg));
            }
            return result.ToString();
        }

        /// <summary>按 Windows CommandLineToArgvW 的规则转义，保留带空格/引号的参数。</summary>
        static string Quote(string value)
        {
            if (value == null) return "\"\"";
            if (value.Length == 0) return "\"\"";
            if (value.IndexOfAny(new char[] { ' ', '\t', '"', '\\' }) < 0) return value;

            var result = new StringBuilder();
            result.Append('"');
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }
                if (backslashes > 0)
                {
                    result.Append('\\', backslashes);
                    backslashes = 0;
                }
                result.Append(c);
            }
            if (backslashes > 0) result.Append('\\', backslashes * 2);
            result.Append('"');
            return result.ToString();
        }
    }
}
