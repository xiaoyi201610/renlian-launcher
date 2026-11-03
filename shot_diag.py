# -*- coding: utf-8 -*-
"""端到端验证崩溃诊断页：点「一键收集」→ 等结果 → 截图"""

import base64
import json
import os
import subprocess
import sys
import time
import urllib.request

import websocket

EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
PORT = 9455
URL = "http://127.0.0.1:8799/#/diag"
HERE = os.path.dirname(os.path.abspath(__file__))


class CDP:
    def __init__(self, ws):
        self.ws = websocket.create_connection(ws, timeout=60)
        self.i = 0

    def call(self, m, p=None):
        self.i += 1
        self.ws.send(json.dumps({"id": self.i, "method": m, "params": p or {}}))
        while True:
            r = json.loads(self.ws.recv())
            if r.get("id") == self.i:
                return r

    def js(self, e, await_promise=False):
        r = self.call("Runtime.evaluate", {
            "expression": e, "returnByValue": True, "awaitPromise": await_promise,
        })
        return (r.get("result", {}).get("result") or {}).get("value")


def main():
    proc = subprocess.Popen([
        EDGE, "--headless=new", "--disable-gpu", "--no-first-run",
        f"--remote-debugging-port={PORT}",
        "--user-data-dir=" + os.path.join(os.environ["TEMP"], "_edge_diag"),
        "--window-size=1400,1000", URL,
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    ws = None
    for _ in range(40):
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{PORT}/json/list", timeout=2) as r:
                for pg in json.loads(r.read().decode()):
                    if pg.get("type") == "page" and "127.0.0.1:8799" in (pg.get("url") or ""):
                        ws = pg["webSocketDebuggerUrl"]
                        break
            if ws:
                break
        except Exception:
            pass
        time.sleep(0.6)
    if not ws:
        print("连不上")
        proc.terminate()
        return 1

    c = CDP(ws)
    c.call("Page.enable")
    c.call("Runtime.enable")

    for _ in range(30):
        if c.js("!!document.querySelector('#dgRun')"):
            break
        time.sleep(0.6)

    print("点击「一键收集」…")
    c.js("document.querySelector('#dgRun').click();'ok'")

    # 等结果渲染出来（最多 40 秒）
    done = False
    for _ in range(60):
        time.sleep(1)
        if c.js("!!document.querySelector('#dgResult .diag-row')"):
            done = True
            break

    if not done:
        print("✗ 40 秒内没出结果")
        proc.terminate()
        return 1

    rows = c.js("""Array.from(document.querySelectorAll('#dgResult .diag-row')).map(r=>{
        const ok=r.classList.contains('ok');
        const b=r.querySelector('b'); const s=r.querySelector('span:last-child');
        return (ok?'✓ ':'✗ ')+(b?b.textContent:'')+' | '+(s?s.textContent:'');
    })""")
    print("\n---- 采集结果 ----")
    for r in rows or []:
        print("  " + r)

    summary = c.js("""Array.from(document.querySelectorAll('#dgResult .diag-sum-l')).map(e=>e.textContent)""")
    print("\n---- 摘要 ----")
    for s in summary or []:
        print("  " + s)

    title = c.js("(document.querySelector('#dgResult .card-title')||{}).textContent||''")
    print("\n状态行:", (title or "").strip())

    # 截图前隐藏 toast，让配图干净
    c.js("var t=document.querySelector('#toast'); if(t) t.hidden=true; 'ok'")
    time.sleep(0.4)
    shot = c.call("Page.captureScreenshot", {"format": "png"})
    data = shot.get("result", {}).get("data")
    if data:
        with open(os.path.join(HERE, "diag-shot.png"), "wb") as f:
            f.write(base64.b64decode(data))
        print("✓ 已保存 diag-shot.png")

    try:
        proc.terminate()
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
