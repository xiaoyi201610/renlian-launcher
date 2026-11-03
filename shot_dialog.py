# -*- coding: utf-8 -*-
"""截图验证应用内对话框样式（headless Edge + CDP）"""

import base64
import json
import os
import subprocess
import sys
import time
import urllib.request

import websocket

EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
PORT = 9444
URL = "http://127.0.0.1:8799/#/settings"


class CDP:
    def __init__(self, ws):
        self.ws = websocket.create_connection(ws, timeout=20)
        self.i = 0

    def call(self, m, p=None):
        self.i += 1
        self.ws.send(json.dumps({"id": self.i, "method": m, "params": p or {}}))
        while True:
            r = json.loads(self.ws.recv())
            if r.get("id") == self.i:
                return r

    def js(self, e):
        r = self.call("Runtime.evaluate", {"expression": e, "returnByValue": True,
                                           "awaitPromise": True})
        return (r.get("result", {}).get("result") or {}).get("value")


def main():
    proc = subprocess.Popen([
        EDGE, "--headless=new", "--disable-gpu", "--no-first-run",
        f"--remote-debugging-port={PORT}",
        "--user-data-dir=" + os.path.join(os.environ["TEMP"], "_edge_dlg"),
        "--window-size=1400,980", URL,
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    ws = None
    for _ in range(40):
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{PORT}/json/list", timeout=2) as r:
                pages = json.loads(r.read().decode())
            for pg in pages:
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
        if c.js("!!document.querySelector('.card')"):
            break
        time.sleep(0.6)

    # 触发一个危险操作的确认框（不要 await，否则会一直等用户点击）
    c.js("""(()=>{ui.confirm('确定删除播放集「人联懒人包 171mod」？\\n\\n删除前会自动备份 launcher-v2.sqlite，可从备份目录恢复。',{title:'删除播放集',okText:'删除',danger:true});return 'shown';})()""")
    time.sleep(1.2)

    shot = c.call("Page.captureScreenshot", {"format": "png"})
    data = shot.get("result", {}).get("data")
    if data:
        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "dialog-shot.png"), "wb") as f:
            f.write(base64.b64decode(data))
        print("✓ 已保存 dialog-shot.png")
    else:
        print("✗ 截图失败", json.dumps(shot)[:300])

    try:
        proc.terminate()
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
