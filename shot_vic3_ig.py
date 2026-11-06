# -*- coding: utf-8 -*-
"""端到端验证：维多利亚 3 百科（利益集团 + 集团特质）。

用法：python shot_vic3_ig.py
产出：vic3-ig.png（集团页） / vic3-igtrait.png（特质页）
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
PORT = 9475
WEB = 8811
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

    def js(self, e):
        r = self.call("Runtime.evaluate", {"expression": e, "returnByValue": True})
        res = r.get("result", {})
        if res.get("exceptionDetails"):
            return {"__err": str(res["exceptionDetails"].get("text"))}
        return (res.get("result") or {}).get("value")


def shot(c, name):
    d = c.call("Page.captureScreenshot", {"format": "png"}).get("result", {}).get("data")
    if d:
        with open(os.path.join(HERE, name), "wb") as f:
            f.write(base64.b64decode(d))
        print("   ✓ 截图 " + name)


def click_tab(c, label):
    r = c.js("""(() => {
        const b = Array.from(document.querySelectorAll('.wk-tab'))
          .find(x => x.textContent.includes(%s));
        if (!b) return 'notfound';
        b.click();
        return 'ok';
    })()""" % json.dumps(label, ensure_ascii=False))
    time.sleep(1.2)
    return r


def open_row(c, name):
    r = c.js("""(() => {
        const rows = Array.from(document.querySelectorAll('.wk-row'));
        const r = rows.find(x => ((x.querySelector('.wk-name')||{}).textContent||'')
                                  .replace(/\\s+/g,'').includes(%s));
        if (!r) return 'notfound';
        r.querySelector('.wk-line').click();
        return 'ok';
    })()""" % json.dumps(name, ensure_ascii=False))
    time.sleep(0.7)
    return r


def read_open(c):
    return c.js("""(() => {
        const r = document.querySelector('.wk-row.open');
        if (!r) return 'no-open';
        return JSON.stringify({
            name: (r.querySelector('.wk-name')||{}).textContent,
            tag: (r.querySelector('.wk-tag')||{}).textContent,
            dot: ((r.querySelector('.wk-ig-dot')||{}).style||{}).background || '',
            ico: !!r.querySelector('.wk-ico img'),
            icoOk: (() => { const i = r.querySelector('.wk-ico img');
                            return !!(i && i.complete && i.naturalWidth > 0); })(),
            stats: Array.from(r.querySelectorAll('.wk-stat')).map(s=>s.textContent.trim().replace(/\\s+/g,' ')),
            eff: Array.from(r.querySelectorAll('.wk-eff li')).map(x=>x.textContent),
            secs: Array.from(r.querySelectorAll('.wk-sec')).map(s => ({
                h: ((s.querySelector('.wk-h')||{}).textContent||'').trim(),
                hint: ((s.querySelector('.wk-hint')||{}).textContent||'').trim().slice(0,60),
                chips: Array.from(s.querySelectorAll('.wk-chip')).map(x=>x.textContent).slice(0,10),
                li: Array.from(s.querySelectorAll('.wk-eff li')).map(x=>x.textContent).slice(0,12),
            })),
        }, null, 1);
    })()""")


def main():
    import server as srv
    srv.CTX.switch_game("vic3")
    print("gameRoot =", srv.CTX.cfg.get("gameRoot"))
    httpd, port = srv.serve(WEB)
    print("服务已起:", port)

    proc = subprocess.Popen([
        EDGE, "--headless=new", "--disable-gpu", "--no-first-run",
        "--remote-debugging-port=%d" % PORT,
        "--user-data-dir=" + os.path.join(os.environ["TEMP"], "_edge_vic3ig"),
        "--window-size=1500,1350", URL,
    ], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    ws = None
    for _ in range(80):
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

    for _ in range(180):
        if c.js("!!document.querySelector('.wk-tab')"):
            break
        time.sleep(1)
    else:
        print("✗ 百科页没渲染出来:", c.js("document.body.innerText.slice(0,300)"))
        proc.terminate()
        httpd.shutdown()
        return 1

    print("\n=== tab 栏 ===")
    print(c.js("""(() => Array.from(document.querySelectorAll('.wk-tab'))
        .map(b=>b.textContent.trim().replace(/\\s+/g,' ')).join(' | '))()"""))

    # ---------- 利益集团 ----------
    print("\n########## 利益集团 ##########")
    print("切 tab:", click_tab(c, "利益集团"))
    print("条数:", c.js("document.querySelectorAll('.wk-row').length"))
    print("筛选条数（应为 0，8 条不分档）:",
          c.js("(() => { const s=document.querySelector('#wkSel'); return s ? s.options.length : 0; })()"))
    print("色点:", c.js("document.querySelectorAll('.wk-ig-dot').length"),
          " 图位:", c.js("document.querySelectorAll('.wk-ico').length"))

    for _ in range(40):
        a = c.js("document.querySelectorAll('.wk-ico img').length")
        b = c.js("Array.from(document.querySelectorAll('.wk-ico img')).filter(i=>i.complete&&i.naturalWidth>0).length")
        if a and b and int(b) >= int(a):
            break
        time.sleep(0.5)
    print("图标已加载: %s / %s" % (
        c.js("Array.from(document.querySelectorAll('.wk-ico img')).filter(i=>i.complete&&i.naturalWidth>0).length"),
        c.js("document.querySelectorAll('.wk-ico img').length")))

    print("\n展开「地主」:", open_row(c, "地主"))
    print(read_open(c))

    print("\n列表全部名称:")
    print(c.js("""(() => Array.from(document.querySelectorAll('.wk-row .wk-name'))
        .map(x=>x.textContent.trim()).join(' / '))()"""))
    shot(c, "vic3-ig.png")

    # ---------- 集团特质 ----------
    print("\n########## 集团特质 ##########")
    print("切 tab:", click_tab(c, "集团特质"))
    print("条数:", c.js("document.querySelectorAll('.wk-row').length"))
    print("筛选按钮:", c.js("""(() => Array.from(document.querySelectorAll('.wk-cat'))
        .map(o=>o.textContent.trim().replace(/\\s+/g,' ')).join(' / ') || '无')()"""))
    print("色点:", c.js("document.querySelectorAll('.wk-ig-dot').length"),
          " 图位:", c.js("document.querySelectorAll('.wk-ico').length"))

    for _ in range(50):
        a = c.js("document.querySelectorAll('.wk-ico img').length")
        b = c.js("Array.from(document.querySelectorAll('.wk-ico img')).filter(i=>i.complete&&i.naturalWidth>0).length")
        if a and b and int(b) >= int(a):
            break
        time.sleep(0.5)
    print("图标已加载: %s / %s" % (
        c.js("Array.from(document.querySelectorAll('.wk-ico img')).filter(i=>i.complete&&i.naturalWidth>0).length"),
        c.js("document.querySelectorAll('.wk-ico img').length")))

    print("\n展开「贵族义务」:", open_row(c, "贵族义务"))
    print(read_open(c))
    # 展开态也拍一张：滚到展开行，确认「生效条件」这些字段真的画出来了
    c.js("""(() => { const r=document.querySelector('.wk-row.open');
        if (r) r.scrollIntoView({block:'start'}); return 'ok'; })()""")
    time.sleep(0.5)
    shot(c, "vic3-igtrait-open.png")

    # 按集团筛选（8 个分类，前端用胶囊按钮，不是下拉）
    c.js("""(() => { const b = Array.from(document.querySelectorAll('.wk-cat'))
        .find(x => x.textContent.includes('地主'));
        if (b) b.click(); return 'ok'; })()""")
    time.sleep(0.7)
    print("\n筛选「地主」后条数:", c.js("document.querySelectorAll('.wk-row').length"))
    print("首几条:", c.js("""(() => Array.from(document.querySelectorAll('.wk-row')).slice(0,6)
        .map(r => { const n=(r.querySelector('.wk-name')||{}).textContent||'';
                   const t=(r.querySelector('.wk-tag')||{}).textContent||'';
                   return n+'['+t+']'; }).join(' / '))()"""))

    # 清筛选、展开一条「忠诚及以上」的，好截图
    c.js("""(() => { const b = Array.from(document.querySelectorAll('.wk-cat'))
        .find(x => x.dataset.cat === '');
        if (b) b.click(); return 'ok'; })()""")
    time.sleep(0.5)
    open_row(c, "贵族义务")
    shot(c, "vic3-igtrait.png")

    # ---------- 搜索 ----------
    c.js("""(() => { const q = document.querySelector('#wkQ');
        q.value = '容克'; q.dispatchEvent(new Event('input',{bubbles:true})); return 'ok'; })()""")
    time.sleep(0.7)
    print("\n搜索「容克」命中:", c.js("document.querySelectorAll('.wk-row').length"),
          "首条:", c.js("(document.querySelector('.wk-row .wk-name')||{}).textContent"))

    print("\n完成")
    proc.terminate()
    httpd.shutdown()
    return 0


if __name__ == "__main__":
    sys.exit(main())
