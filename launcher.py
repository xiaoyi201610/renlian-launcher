# -*- coding: utf-8 -*-
"""
CK3 Launcher - 程序入口
=======================
启动本地 API 服务，然后打开原生窗口（pywebview 优先，回退 Edge App 模式）。
"""

import os
import sys
import time
import ctypes
import threading
import subprocess

HERE = os.path.dirname(os.path.abspath(__file__))
if not getattr(sys, "frozen", False):
    sys.path.insert(0, os.path.join(HERE, "backend"))

import server as srv          # noqa: E402
import ck3core as core        # noqa: E402

APP_NAME = "CK3 Launcher"
APP_TITLE = "人联启动器"
ICON = os.path.join(core.RES_DIR, "static", "icon.ico")

_EDGE_PATHS = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]


def single_instance() -> bool:
    """命名互斥体防多开。"""
    try:
        h = ctypes.windll.kernel32.CreateMutexW(None, False, "CK3Launcher_SingleInstance_v1")
        if ctypes.windll.kernel32.GetLastError() == 183:  # ERROR_ALREADY_EXISTS
            return False
        return True
    except Exception:
        return True


def find_edge() -> str | None:
    for p in _EDGE_PATHS:
        if os.path.isfile(p):
            return p
    return None


def enable_dpi_awareness() -> None:
    """让窗口在高分屏上清晰、尺寸准确（否则 150% 缩放下会偏小/模糊）。"""
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)  # SYSTEM_DPI_AWARE
        return
    except Exception:
        pass
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass


def window_geometry() -> dict:
    """按屏幕大小算一个居中的合适窗口。"""
    try:
        sw = ctypes.windll.user32.GetSystemMetrics(0)
        sh = ctypes.windll.user32.GetSystemMetrics(1)
    except Exception:
        sw, sh = 1920, 1080
    w = max(1120, min(1520, int(sw * 0.84)))
    h = max(700, min(960, int(sh * 0.86)))
    return {"width": w, "height": h, "x": max(0, (sw - w) // 2), "y": max(0, (sh - h) // 2)}


def _log(msg: str) -> None:
    """把诊断信息写到程序目录，打包后无控制台时用于排查。"""
    try:
        base = os.path.dirname(os.path.abspath(sys.executable)) \
            if getattr(sys, "frozen", False) else HERE
        with open(os.path.join(base, "启动日志.log"), "a", encoding="utf-8") as f:
            f.write(time.strftime("[%H:%M:%S] ") + msg + "\n")
    except Exception:
        pass


def run_with_webview(url: str) -> bool:
    try:
        import webview
    except Exception as ex:
        _log("导入 webview 失败: %r" % (ex,))
        return False
    try:
        geo = window_geometry()
        kwargs = dict(
            title=APP_TITLE,
            url=url,
            width=geo["width"],
            height=geo["height"],
            x=geo["x"],
            y=geo["y"],
            min_size=(1080, 680),
            background_color="#0a0e13",
            text_select=False,
        )
        # 注意：create_window 不支持 private_mode / icon，二者都属于 start()
        win = webview.create_window(**kwargs)
        _log("webview 窗口已创建，进入主循环")

        # 空白看门狗：极少数情况（WebView2 被残留进程抢占资源）窗口能起来但页面渲染不出来，
        # 用户看到的就是一片黑。这里等页面稳定后探一次 DOM，没内容就关窗口让上层回退 Edge 模式。
        blank = {"v": False}

        def _watchdog():
            time.sleep(8)
            try:
                # 只判断「页面框架是否真的加载了」。不能要求 #view 里已有内容：
                # 首次启动要扫模组、读数据库，boot() 可能要十几秒才渲染出内容，
                # 按内容判断会把正常窗口误判成黑屏（1.7.0.5 就是这样误回退到 Edge 的）。
                ok = win.evaluate_js(
                    "(document.readyState==='complete')"
                    " && !!document.querySelector('#view')"
                )
            except Exception as wex:
                # 探测本身失败通常意味着 WebView2 根本没起来（窗口是死的黑底），
                # 这种也算黑屏，一并回退，别让用户对着空窗口干瞪眼。
                _log("看门狗探测失败，按黑屏处理: %r" % (wex,))
                ok = False
            if not ok:
                blank["v"] = True
                _log("探测到窗口空白，关闭并回退 Edge 模式")
                try:
                    win.destroy()
                except Exception:
                    pass

        threading.Thread(target=_watchdog, daemon=True).start()

        start_kw = {"private_mode": True}
        if os.path.isfile(ICON):
            start_kw["icon"] = ICON
        try:
            webview.start(**start_kw)
        except TypeError:
            webview.start()
        _log("webview 主循环结束")
        return not blank["v"]
    except Exception as ex:
        import traceback
        _log("webview 启动失败: %r\n%s" % (ex, traceback.format_exc(limit=6)))
        print("webview 失败，回退 Edge：", ex)
        return False


def _window_titles() -> list:
    """枚举当前所有可见窗口的标题（用于判断 Edge 窗口是否还开着）。"""
    titles = []
    try:
        from ctypes import wintypes
        user32 = ctypes.windll.user32
        cb_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        def _cb(hwnd, _):
            try:
                if user32.IsWindowVisible(hwnd):
                    n = user32.GetWindowTextLengthW(hwnd)
                    if n > 0:
                        buf = ctypes.create_unicode_buffer(n + 1)
                        user32.GetWindowTextW(hwnd, buf, n + 1)
                        if buf.value:
                            titles.append(buf.value)
            except Exception:
                pass
            return True

        user32.EnumWindows(cb_type(_cb), 0)
    except Exception:
        pass
    return titles


def _app_window_alive(port: int) -> bool:
    needle = f"127.0.0.1:{port}"
    for t in _window_titles():
        if needle in t or APP_TITLE in t:
            return True
    return False


def run_with_edge(url: str, port: int) -> bool:
    edge = find_edge()
    if not edge:
        return False
    profile = os.path.join(core.APP_DIR, ".edge-profile")
    os.makedirs(profile, exist_ok=True)
    geo = window_geometry()
    args = [
        edge,
        f"--app={url}",
        f"--window-size={geo['width']},{geo['height']}",
        f"--user-data-dir={profile}",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-features=Translate,ImportDataPrompt",
    ]
    try:
        started = time.time()
        proc = subprocess.Popen(args, close_fds=True)
        proc.wait()
        # Edge 用同一个 user-data-dir 时，如果已经有 Edge（比如上次回退留下的
        # 启动器窗口）在跑，新进程只是把 URL 交给老进程，自己立刻退出。
        # 这时如果跟着收工、关掉本地服务，用户看到的就是
        # “127.0.0.1 拒绝连接”——1.7.0.5 的故障就是这个。
        # 所以：进程秒退说明窗口由别的进程托管，继续守着窗口，直到它关掉。
        if time.time() - started < 3:
            _log("Edge 启动被现有实例接管，继续为窗口提供本地服务")
            last_seen = time.time()
            while time.time() - last_seen <= 40:
                time.sleep(2)
                if _app_window_alive(port):
                    last_seen = time.time()
            _log("已检测不到启动器窗口，关闭本地服务")
        return True
    except Exception:
        return False


def _run_diag_selftest() -> None:
    """`人联启动器.exe --diag-test`：不开窗口，直接跑一次诊断收集并写结果文件。
    用于确认打包后的诊断模块是否正常，也方便排障。"""
    import traceback
    base = os.path.dirname(os.path.abspath(sys.executable)) \
        if getattr(sys, "frozen", False) else HERE
    out = os.path.join(base, "诊断自检.txt")
    lines = []
    try:
        import diagnose
        cfg = core.load_config("ck3")
        r = diagnose.collect(cfg, include_dxdiag=False)
        if r.get("ok"):
            lines.append("诊断自检：通过")
            lines.append("压缩包：%s" % r.get("name"))
            lines.append("大小：%.2f MB" % (r.get("size", 0) / 1048576))
            lines.append("")
            for it in r.get("items", []):
                lines.append(("✓ " if it["ok"] else "✗ ") + it["name"] + "  |  " + it["note"])
            lines.append("")
            lines.append("摘要：")
            lines.extend("  " + s for s in r.get("summary", []))
        else:
            lines.append("诊断自检：失败")
            lines.append(str(r.get("error")))
    except Exception:
        lines.append("诊断自检：异常")
        lines.append(traceback.format_exc())
    try:
        with open(out, "w", encoding="utf-8") as f:
            f.write("\n".join(lines))
    except Exception:
        pass


def _run_dlc_selftest() -> None:
    """`人联启动器.exe --dlc-test`：不开窗口，打印 DLC 判定结果并写文件。

    用来确认「补丁检测 / Steam 校验 / 分类」在打包后依然正确。
    """
    import traceback
    base = os.path.dirname(os.path.abspath(sys.executable)) \
        if getattr(sys, "frozen", False) else HERE
    out = os.path.join(base, "DLC自检.txt")
    lines = []
    try:
        cfg = core.load_config("ck3")
        items = core.list_dlcs(cfg)
        patch = core.detect_dlc_patch(cfg.get("gameRoot") or "", cfg.get("currentGame") or "ck3")
        lines.append("DLC 自检：通过")
        lines.append("游戏目录：%s" % (cfg.get("gameRoot") or "（未设置）"))
        lines.append("补丁检测：%s" % (
            "%s · 解锁 %d 个%s" % (patch["type"], patch["unlockedCount"],
                                  " · 疑似失效" if patch["suspicious"] else "")
            if patch["present"] else "未检测到"))
        if patch["present"]:
            own, no = items, []
            lines.append("（检测到补丁 → 全部按可用，不锁定）")
        else:
            sids = [x["steamId"] for x in items if x.get("steamId")]
            import steamdlc
            sr = steamdlc.query(sids, cfg.get("gameRoot") or "")
            lines.append("Steam 校验：%s%s" % ("成功" if sr.get("ok") else "失败",
                                               ("（%s）" % sr["msg"]) if sr.get("msg") else ""))
            if sr.get("ok"):
                own = [x for x in items if sr["owned"].get(int(x["steamId"]), True)]
                no = [x for x in items if not sr["owned"].get(int(x["steamId"]), True)]
            else:
                own, no = items, []
        lines.append("共 %d 个 · 已拥有 %d 个 · 未拥有 %d 个"
                     % (len(items), len(own), len(no)))
        _order = ["ep", "ce", "fp", "bp", "cp", "sp", "mp"]
        _cn = {"ep": "大型扩展包", "ce": "核心扩展包", "fp": "风味包", "bp": "事件包",
               "cp": "内容创作者包", "sp": "装饰包", "mp": "音乐包"}
        parts = []
        for c in _order:
            n = sum(1 for x in items if x["dlcType"] == c)
            if n:
                parts.append("%s %d" % (_cn[c], n))
        lines.append("分类：" + " · ".join(parts))
        lines.append("")
        lines.append("===== 已拥有（可启用）=====")
        for x in own:
            lines.append("  %-18s %-4s %s" % (x["dir"], x["typeCn"], x["name"]))
        lines.append("")
        lines.append("===== 未拥有（锁定）=====")
        for x in no:
            lines.append("  %-18s %-4s %s" % (x["dir"], x["typeCn"], x["name"]))
    except Exception:
        lines.append("DLC 自检：异常")
        lines.append(traceback.format_exc())
    text = "\n".join(lines)
    try:
        with open(out, "w", encoding="utf-8") as f:
            f.write(text)
    except Exception:
        pass
    try:
        sys.stdout.write(text + "\n")
    except Exception:
        pass


def main():
    enable_dpi_awareness()

    if "--diag-test" in sys.argv:
        _run_diag_selftest()
        return

    if "--dlc-test" in sys.argv:
        _run_dlc_selftest()
        return

    if not single_instance():
        # 已有实例：把窗口带到前台（简单起见直接退出并提示）
        try:
            ctypes.windll.user32.MessageBoxW(
                None, "人联启动器已经在运行了，请查看任务栏。", APP_NAME, 0x40
            )
        except Exception:
            pass
        return

    httpd, port = srv.serve(0)
    url = f"http://127.0.0.1:{port}/"

    # 后台预热 mod 索引，窗口一打开就是热的
    threading.Thread(target=lambda: srv.CTX.mods(), daemon=True).start()

    if not run_with_webview(url):
        if not run_with_edge(url, port):
            import webbrowser
            webbrowser.open(url)
            print("已用默认浏览器打开：", url)
            try:
                while True:
                    time.sleep(1)
            except KeyboardInterrupt:
                pass
    try:
        httpd.shutdown()
    except Exception:
        pass


if __name__ == "__main__":
    _exit_code = 1
    try:
        main()
    except Exception:
        # 打包后是 windowed 模式，没有控制台，崩溃信息写到 exe 旁边
        import traceback
        try:
            base = os.path.dirname(os.path.abspath(sys.executable)) \
                if getattr(sys, "frozen", False) else HERE
            with open(os.path.join(base, "启动失败.log"), "w", encoding="utf-8") as f:
                f.write(traceback.format_exc())
        except Exception:
            pass
        try:
            ctypes.windll.user32.MessageBoxW(
                None,
                "启动器启动失败，详细信息已写入程序目录下的「启动失败.log」。",
                APP_NAME,
                0x10,
            )
        except Exception:
            pass
        _exit_code = 1
    else:
        _exit_code = 0
    finally:
        # 打包版退出时必须硬停：WebView2 退场后仍留着后台句柄，
        # Python 正常析构会崩（每次留一个 20MB crash dump + 一个杀不掉的僵尸进程，
        # 还会连带 PyInstaller 的 _MEI 临时目录清不掉，久了把 C 盘塞满、下次启动黑屏）。
        try:
            sys.stdout.flush()
            sys.stderr.flush()
        except Exception:
            pass
        os._exit(_exit_code)
