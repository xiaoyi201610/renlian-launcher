// 人联启动器外壳：只是一层薄壳，真正的程序在 runtime\人联启动器.exe
// 这样根目录只保留 人联启动器.exe + runtime\ + wwwroot 之外的运行数据，看起来干净。
// 编译方式见 deploy.ps1（csc.exe /target:winexe）。
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var runtimeDir = Path.Combine(baseDir, "runtime");
        var core = Path.Combine(runtimeDir, "人联启动器.exe");

        if (!File.Exists(core))
        {
            MessageBox.Show(
                "找不到 runtime\\人联启动器.exe。\r\n请确认解压时没有漏掉 runtime 文件夹。",
                "人联启动器", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var sb = new StringBuilder();
        foreach (var a in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
        }

        try
        {
            var psi = new ProcessStartInfo(core)
            {
                UseShellExecute = false,
                WorkingDirectory = runtimeDir,
                Arguments = sb.ToString(),
            };
            Process.Start(psi);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败：" + ex.Message, "人联启动器",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
