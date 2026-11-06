# -*- coding: utf-8 -*-
"""端到端验证：群星百科。
把启动器切到群星 → 打开百科页 → 检查 tab / 条目 / 展开详情 → 截图。
用法：
  python shot_sl.py                       # 只体检 + 存 sl-shot.png
  python shot_sl.py 徭役制 sl-civic.png    # 指定要展开并截图的条目
"""

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
PORT = 9469
WEB = 8802
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
    srv.CTX.switch_game("stellaris")
    print("已切到群星，gameRoot =", srv.CTX.cfg.get("gameRoot"))
    httpd, port = srv.serve(WEB)
    print("服务已起:", port)

    proc = subprocess.Popen([
        EDGE, "--headless=new", "--disable-gpu", "--no-first-run",
        "--remote-debugging-port=%d" % PORT,
        "--user-data-dir=" + os.path.join(os.environ["TEMP"], "_edge_sl"),
        "--window-size=1400,1150", URL,
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

    ok = False
    for _ in range(120):
        if c.js("!!document.querySelector('.wk-tab')"):
            ok = True
            break
        time.sleep(1)
    if not ok:
        err = c.js("(document.querySelector('.empty')||{}).textContent || 'no-tab'")
        print("✗ 百科页没渲染出来:", err)
        proc.terminate()
        httpd.shutdown()
        return 1

    print("\n=== tab 列表 ===")
    print(c.js("""(() => Array.from(document.querySelectorAll('.wk-tab'))
        .map(b => b.textContent.trim().replace(/\\s+/g,' ')).join(' | '))()"""))

    # 逐 tab 体检
    tabs = c.js("""(() => Array.from(document.querySelectorAll('.wk-tab')).map(b => b.dataset.tab))()""")
    print("\n=== 各 tab 条目数 ===")
    for tid in tabs:
        c.js("document.querySelector('.wk-tab[data-tab=\"%s\"]').click(); 'ok'" % tid)
        time.sleep(0.35)
        n = c.js("document.querySelectorAll('.wk-row').length")
        first = c.js("""(() => {
            const r = document.querySelector('.wk-row');
            return r ? (r.querySelector('.wk-name')||{}).textContent : '';
        })()""")
        cats = c.js("""(() => Array.from(document.querySelectorAll('.wk-cat'))
            .map(b => b.textContent.trim().replace(/\\s+/g,'')).slice(0,8).join(' / '))()""")
        print("  %-11s %4d 条  首条=%s" % (tid, int(n or 0), first))
        if cats:
            print("            分类: " + cats)

    # 展开几个代表条目，dump 详情
    probe = [("civics", "徭役制"), ("origins", "繁荣一统"), ("traditions", "适应传统"),
             ("relics", "模块精炼仪"), ("tech", "奥术解密")]
    for tid, name in probe:
        c.js("document.querySelector('.wk-tab[data-tab=\"%s\"]').click(); 'ok'" % tid)
        time.sleep(0.4)
        hit = c.js("""(() => {
            const q = document.querySelector('#wkQ');
            if (q) { q.value=''; q.dispatchEvent(new Event('input',{bubbles:true})); }
            const rows = Array.from(document.querySelectorAll('.wk-row'));
            const r = rows.find(x => (x.querySelector('.wk-name')||{}).textContent?.startsWith(%s));
            if (!r) return 'notfound';
            r.querySelector('.wk-line').click();
            return 'clicked';
        })()""" % json.dumps(name, ensure_ascii=False))
        if hit != "clicked":
            print("\n✗ [%s] 找不到 %s" % (tid, name))
            continue
        time.sleep(0.45)
        txt = c.js("""(() => {
            const r = document.querySelector('.wk-row.open');
            if (!r) return 'no-open';
            const nm = (r.querySelector('.wk-name')||{}).textContent||'';
            const stats = Array.from(r.querySelectorAll('.wk-stat'))
                .map(s => s.textContent.trim().replace(/\\s+/g,' '));
            const secs = Array.from(r.querySelectorAll('.wk-sec')).map(s => {
                const h = (s.querySelector('.wk-h')||{}).textContent||'';
                const li = Array.from(s.querySelectorAll('.wk-eff li')).map(x=>x.textContent);
                const d = (s.querySelector('.wk-desc')||{}).textContent||'';
                return h + ' => ' + (li.length ? li.join(' | ') : d);
            });
            return JSON.stringify({name:nm, stats:stats, secs:secs});
        })()""")
        print("\n===== [%s] %s =====" % (tid, name))
        print(txt)
        c.js("(() => { const r=document.querySelector('.wk-row.open .wk-line'); if(r) r.click(); return 'ok'; })()")
        time.sleep(0.25)

    # 截图
    only_tab = sys.argv[1] if len(sys.argv) > 1 else "civics"
    out = sys.argv[2] if len(sys.argv) > 2 else "sl-shot.png"
    only_txt = sys.argv[3] if len(sys.argv) > 3 else ""
    c.js("document.querySelector('.wk-tab[data-tab=\"%s\"]').click(); 'ok'" % only_tab)
    time.sleep(0.6)
    if only_txt:
        c.js("""(() => {
            const q = document.querySelector('#wkQ');
            if (q) { q.value = %s; q.dispatchEvent(new Event('input',{bubbles:true})); }
            return 'ok';
        })()""" % json.dumps(only_txt, ensure_ascii=False))
        time.sleep(0.9)
        c.js("""(() => {
            const rows = Array.from(document.querySelectorAll('.wk-row'));
            const r = rows.find(x => (x.querySelector('.wk-name')||{}).textContent?.startsWith(%s));
            if (r) r.querySelector('.wk-line').click();
            const t = document.querySelector('#toast'); if (t) t.hidden = true;
            return 'ok';
        })()""" % json.dumps(only_txt, ensure_ascii=False))
        time.sleep(0.9)
    shot = c.call("Page.captureScreenshot", {"format": "png"})
    data = shot.get("result", {}).get("data")
    if data:
        with open(os.path.join(HERE, out), "wb") as f:
            f.write(base64.b64decode(data))
        print("\n✓ 已保存 " + out)

    try:
        proc.terminate()
        httpd.shutdown()
        srv.CTX.switch_game("ck3")     # 测完切回 CK3，别改用户默认
        print("已切回 CK3")
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
