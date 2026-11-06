# -*- coding: utf-8 -*-
"""端到端验证：百科「信条」页展开条目，检查「核心效果 / 个人教义效果」两块渲染是否正确。"""

import base64
import json
import os
import subprocess
import sys
import time
import urllib.request

import websocket

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "backend"))

EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
PORT = 9466
WEB = 8799
URL = "http://127.0.0.1:%d/#/wiki" % WEB


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
    import server as srv
    httpd, port = srv.serve(WEB)
    print("服务已起:", port)

    proc = subprocess.Popen([
        EDGE, "--headless=new", "--disable-gpu", "--no-first-run",
        "--remote-debugging-port=%d" % PORT,
        "--user-data-dir=" + os.path.join(os.environ["TEMP"], "_edge_tenet"),
        "--window-size=1400,1100", URL,
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    ws = None
    for _ in range(60):
        try:
            with urllib.request.urlopen("http://127.0.0.1:%d/json/list" % PORT, timeout=2) as r:
                for pg in json.loads(r.read().decode()):
                    if pg.get("type") == "page" and ("127.0.0.1:%d" % WEB) in (pg.get("url") or ""):
                        ws = pg["webSocketDebuggerUrl"]
                        break
            if ws:
                break
        except Exception:
            pass
        time.sleep(0.6)
    if not ws:
        print("连不上调试端口")
        proc.terminate()
        return 1

    c = CDP(ws)
    c.call("Page.enable")
    c.call("Runtime.enable")

    # 等百科标签渲染（首次构建缓存要十几秒）
    ok = False
    for _ in range(90):
        if c.js("!!document.querySelector('.wk-tab')"):
            ok = True
            break
        time.sleep(1)
    if not ok:
        print("✗ 百科页没渲染出来")
        proc.terminate()
        return 1

    print("切到「信条」…")
    c.js("document.querySelector('.wk-tab[data-tab=\"tenets\"]').click(); 'ok'")
    time.sleep(1.2)

    total = c.js("document.querySelectorAll('.wk-row').length")
    print("信条条目数:", total)

    # 展开「一性论」「顺应」「家族」相关条目，逐个抓渲染文本
    targets = ["一性论", "顺应", "祖先崇拜"]
    for name in targets:
        hit = c.js("""(() => {
            const rows = Array.from(document.querySelectorAll('.wk-row'));
            const r = rows.find(x => (x.querySelector('.wk-name')||{}).textContent?.startsWith(%s));
            if (!r) return 'notfound';
            r.querySelector('.wk-line').click();
            return 'clicked';
        })()""" % json.dumps(name, ensure_ascii=False))
        if hit != "clicked":
            print("✗ 找不到条目:", name, hit)
            continue
        time.sleep(0.5)
        txt = c.js("""(() => {
            const r = document.querySelector('.wk-row.open');
            if (!r) return 'no-open';
            const secs = Array.from(r.querySelectorAll('.wk-sec')).map(s => {
                const h = (s.querySelector('.wk-h')||{}).textContent || '';
                const li = Array.from(s.querySelectorAll('.wk-eff li')).map(x => x.textContent);
                const pd = (s.querySelector('.wk-pd')||{}).textContent || '';
                return h + (li.length ? ' => ' + li.join(' | ') : (pd ? ' => ' + pd : ''));
            });
            const stats = Array.from(r.querySelectorAll('.wk-stat')).map(x => x.textContent.trim());
            const nm = (r.querySelector('.wk-name')||{}).textContent || '';
            return JSON.stringify({name: nm, stats: stats, secs: secs}, null, 1);
        })()""")
        print("\n===== %s =====" % name)
        print(txt)
        c.js("(() => { const r=document.querySelector('.wk-row.open .wk-line'); if(r) r.click(); return 'ok'; })()")
        time.sleep(0.3)

    # 截图（展开一条留着）
    c.js("""(() => {
        const rows = Array.from(document.querySelectorAll('.wk-row'));
        const r = rows.find(x => (x.querySelector('.wk-name')||{}).textContent?.startsWith('一性论'));
        if (r) { r.querySelector('.wk-line').click(); r.scrollIntoView({block:'center'}); }
        const t = document.querySelector('#toast'); if (t) t.hidden = true;
        return 'ok';
    })()""")
    time.sleep(0.8)
    shot = c.call("Page.captureScreenshot", {"format": "png"})
    data = shot.get("result", {}).get("data")
    if data:
        with open(os.path.join(HERE, "tenet-shot.png"), "wb") as f:
            f.write(base64.b64decode(data))
        print("\n✓ 已保存 tenet-shot.png")

    try:
        proc.terminate()
        httpd.shutdown()
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
