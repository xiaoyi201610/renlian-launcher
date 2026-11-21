using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using RenlianLauncher.Api;
using RenlianLauncher.Core;

namespace RenlianLauncher;

public partial class App : Application
{
    private Mutex? _mutex;
    private ApiServer? _api;

    /// <summary>本地 API 跑在 Kestrel 线程，不能从那里访问 Application.MainWindow（DispatcherObject），
    /// 启动时保存一个静态引用给工坊浏览器等接口用。</summary>
    internal static MainWindow? LauncherWindow { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Paths.EnsureLayout();
        Log("=== 进程启动 " + Environment.ProcessId + " ===");

        _mutex = new Mutex(true, "RenlianLauncher_Native_v2_SingleInstance", out var created);
        if (!created)
        {
            MessageBox.Show("人联启动器已经在运行了，请查看任务栏。", "人联启动器",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log("界面线程异常：" + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("后台异常：" + args.ExceptionObject);

        try
        {
            Log($"启动 · exe={Paths.ExeDir} · data={Paths.DataDir} · portable={Paths.DataDirIsPortable}");

            var cfg = ConfigStore.Load();
            // 恢复用户手动关联的 Steam 目录（必须先于任何路径探测，否则缓存的是自动探测结果）
            Paths.SetManualSteamRoot(cfg.SteamDir);
            _api = new ApiServer(cfg);
            await _api.StartAsync();
            Log($"本地服务已就绪：{_api.BaseUrl}");

            var win = new MainWindow(_api.BaseUrl + "/");
            MainWindow = win;
            LauncherWindow = win;
            win.Show();

            // 后台预热 mod 索引 + 路径扫描，首屏不等它们
            _ = Task.Run(() =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var n = ModIndex.Build(cfg).Count;
                    Log($"mod 索引预热完成：{n} 个，用时 {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception ex) { Log("mod 索引预热失败：" + ex.Message); }
            });

            // 后台预热创意工坊第一页：等 mod 索引扫完再拉，避免抢带宽；
            // 拉完写入磁盘缓存，用户点开创意工坊即秒出（已有缓存时只是后台保鲜）
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(4000);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await WorkshopBrowse.Browse("trend", "", "", 1);
                    Log($"创意工坊首页预热完成，用时 {sw.ElapsedMilliseconds} ms");
                }
                catch (Exception ex) { Log("创意工坊预热失败：" + ex.Message); }
            });
        }
        catch (Exception ex)
        {
            Log("启动失败：" + ex);
            MessageBox.Show("启动器启动失败，详细信息已写入 RenlianData\\logs\\启动日志.log。",
                "人联启动器", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try { if (_api is not null) await _api.StopAsync(); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    internal static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Paths.LogDir);
            File.AppendAllText(Paths.LogPath,
                $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }
}
