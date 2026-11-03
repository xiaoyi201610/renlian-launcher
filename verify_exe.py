# -*- coding: utf-8 -*-
"""
对已打包的 exe 做端到端验证
==========================
通过 WebView2 远程调试端口连进 exe 内部页面，直接检查：
  1. 导航项是否带 href（说明打包进去的是修复后的 static）
  2. 模拟点击每个导航，路由是否真的跳转

用法：python verify_exe.py
"""

import os
import sys
import json
import time
import subprocess
import urllib.request

import websocket

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(HERE, "dist", "LibraLauncher.exe")
PORT = 9333
ROUTES = ["mods", "playsets", "dlc", "saves", "workshop", "tools", "settings", "home"]


def start_exe():
    env = dict(os.environ)
    env["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = f"--remote-debugging-port={PORT}"
    p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), env=env, close_fds=True)
    return p


def wait_page(timeout=40):
    start = time.time()
    while time.time() - start < timeout:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{PORT}/json/list", timeout=2) as r:
                pages = json.loads(r.read().decode("utf-8"))
            for pg in pages:
                if pg.get("type") == "page" and "127.0.0.1" in (pg.get("url") or ""):
                    return pg
        except Exception:
            pass
        time.sleep(0.8)
    return None


class CDP:
    def __init__(self, ws_url):
        self.ws = websocket.create_connection(ws_url, timeout=15)
        self.i = 0

    def call(self, method, params=None):
        self.i += 1
        self.ws.send(json.dumps({"id": self.i, "method": method, "params": params or {}}))
        while True:
            msg = json.loads(self.ws.recv())
            if msg.get("id") == self.i:
                return msg

    def js(self, expr):
        r = self.call("Runtime.evaluate", {
            "expression": expr, "returnByValue": True, "awaitPromise": True,
        })
        return (r.get("result", {}).get("result", {}) or {}).get("value")

    def close(self):
        try:
            self.ws.close()
        except Exception:
            pass


def main():
    if not os.path.isfile(EXE):
        print("✗ 找不到 exe")
        return 1

    proc = start_exe()
    print("已启动 exe，等待页面…")
    pg = wait_page()
    if not pg:
        print("✗ 连不上调试端口，exe 可能没起来")
        proc.terminate()
        return 1

    cdp = CDP(pg["webSocketDebuggerUrl"])
    cdp.call("Runtime.enable")

    # 等前端 boot 完成
    for _ in range(30):
        if cdp.js("!!document.querySelector('.nav-item')"):
            break
        time.sleep(0.6)

    hrefs = cdp.js(
        "Array.from(document.querySelectorAll('.nav-item')).map(a=>a.getAttribute('href'))"
    )
    print("\n==== 1. exe 内 HTML 的导航 href ====")
    print(hrefs)
    has_href = bool(hrefs) and all(h for h in hrefs)
    print("结论:", "✓ 是修复后的版本" if has_href else "✗ 仍是旧版（没有 href）")

    print("\n==== 2. 逐个点击导航 ====")
    ok = 0
    for r in ROUTES:
        cdp.js(f"document.querySelector('[data-route=\"{r}\"]').click()")
        time.sleep(0.85)
        h = cdp.js("location.hash")
        active = cdp.js(
            "(document.querySelector('.nav-item.active')||{dataset:{}}).dataset.route||''"
        )
        title = (cdp.js(
            "(document.querySelector('.page-title')||{}).textContent||''"
        ) or "").strip()[:20]
        good = (h == "#/" + r) and (active == r)
        ok += 1 if good else 0
        print("%s %-9s hash=%-13s 高亮=%-9s 标题=%s" % (
            "✓" if good else "✗", r, h, active or "(无)", title or "(空)"))

    print(f"---- 点击 {ok}/{len(ROUTES)} 通过 ----")
    print("---- 最终结论:", "✓ 全部正常" if (has_href and ok == len(ROUTES)) else "✗ 有问题", "----")

    cdp.close()
    time.sleep(0.5)
    try:
        proc.terminate()
    except Exception:
        pass
    subprocess.run(["taskkill", "/F", "/IM", "LibraLauncher.exe"],
                   capture_output=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
