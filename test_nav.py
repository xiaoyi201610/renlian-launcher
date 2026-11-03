# -*- coding: utf-8 -*-
"""自动化验证：在真实 WebView 窗口里逐个点击左侧导航，检查路由切换。"""

import sys
import os
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "backend"))

import server as srv          # noqa: E402
import webview                # noqa: E402

ROUTES = [
    ("mods", "模组管理"),
    ("playsets", "播放集"),
    ("dlc", "DLC 管理"),
    ("saves", "存档管理"),
    ("workshop", "创意工坊"),
    ("gamedata", "游戏文件"),
    ("tools", "文件目录"),
    ("memory", "内存管理"),
    ("diag", "崩溃诊断"),
    ("settings", "启动器设置"),
    ("home", "概览"),
]

results = {}


def worker(win):
    time.sleep(4)          # 等首屏 + mod 索引
    for route, expect in ROUTES:
        try:
            win.evaluate_js(
                "document.querySelector('[data-route=\"%s\"]').click()" % route
            )
            time.sleep(0.9)
            h = win.evaluate_js("location.hash") or ""
            title = win.evaluate_js(
                "(document.querySelector('.page-title')||{}).textContent||''"
            ) or ""
            active = win.evaluate_js(
                "(document.querySelector('.nav-item.active')||{}).dataset?"
                ".route||''"
            ) or ""
            results[route] = {
                "hash": h,
                "title": title.strip()[:24],
                "active": active,
                "expect": expect,
            }
        except Exception as ex:
            results[route] = {"error": repr(ex)}
    win.destroy()


def main():
    httpd, port = srv.serve(0)
    url = "http://127.0.0.1:%d/" % port
    win = webview.create_window("navtest", url, width=1200, height=820, hidden=True)
    webview.start(worker, win)

    print("\n==== 导航点击测试 ====")
    ok = 0
    for route, expect in ROUTES:
        r = results.get(route, {})
        if "error" in r:
            print("✗ %-9s 异常: %s" % (route, r["error"]))
            continue
        good = (r["hash"] == "#/" + route) and (expect in r["title"]) and (r["active"] == route)
        ok += 1 if good else 0
        print("%s %-9s hash=%-13s 标题=%-14s 高亮=%-9s 期望=%s" % (
            "✓" if good else "✗",
            route, r["hash"], r["title"] or "(空)", r["active"] or "(无)", expect))
    print("---- %d/%d 通过 ----" % (ok, len(ROUTES)))


if __name__ == "__main__":
    main()
