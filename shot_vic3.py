# -*- coding: utf-8 -*-
"""端到端验证：维多利亚 3 百科（法律）。

用法：python shot_vic3.py [输出文件名]
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
PORT = 9473
WEB = 8809
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
        r = self.call("Runtime.evaluate", {
            "expression": e, "returnByValue": True,
        })
        res = r.get("result", {})
        if res.get("exceptionDetails"):
            return {"__err": str(res["exceptionDetails"].get("text"))}
        return (res.get("result") or {}).get("value")


def open_law(c, name):
    """按名称展开一条法律，返回详情文本。"""
    hit = c.js("""(() => {
        const rows = Array.from(document.querySelectorAll('.wk-row'));
        const r = rows.find(x => (x.querySelector('.wk-name')||{}).textContent?.includes(%s));
        if (!r) return 'notfound';
        r.querySelector('.wk-line').click();
        return 'clicked';
    })()""" % json.dumps(name, ensure_ascii=False))
    if hit != "clicked":
        return "找不到 " + name
    time.sleep(0.6)
    txt = c.js("""(() => {
        const r = document.querySelector('.wk-row.open');
        if (!r) return 'no-open';
        const secs = Array.from(r.querySelectorAll('.wk-sec')).map(s => {
            const h = (s.querySelector('.wk-h')||{}).textContent || '';
            const chips = Array.from(s.querySelectorAll('.wk-chip')).map(x=>x.textContent).slice(0,6);
            const li = Array.from(s.querySelectorAll('.wk-eff li')).map(x=>x.textContent).slice(0,6);
            return {h: h, chips: chips, li: li};
        });
        return JSON.stringify({
            name: (r.querySelector('.wk-name')||{}).textContent,
            tag: (r.querySelector('.wk-tag')||{}).textContent,
            prog: (r.querySelector('.wk-prog-v')||{}).textContent || '',
            ico: !!r.querySelector('.wk-ico img'),
            stats: Array.from(r.querySelectorAll('.wk-stat')).map(s=>s.textContent.trim().replace(/\\s+/g,' ')),
            eff: Array.from(r.querySelectorAll('.wk-eff li')).map(x=>x.textContent).slice(0,4),
            secs: secs
        }, null, 0);
    })()""")
    c.js("(() => { const r=document.querySelector('.wk-row.open .wk-line'); if(r) r.click(); return 'ok'; })()")
    return txt


def main():
    import server as srv
    srv.CTX.switch_game("vic3")
    print("已切到维多利亚 3，gameRoot =", srv.CTX.cfg.get("gameRoot"))
    httpd, port = srv.serve(WEB)
    print("服务已起:", port)

    proc = subprocess.Popen([
        EDGE, "--headless=new", "--disable-gpu", "--no-first-run",
        "--remote-debugging-port=%d" % PORT,
        "--user-data-dir=" + os.path.join(os.environ["TEMP"], "_edge_vic3"),
        "--window-size=1500,1250", URL,
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

    ok = False
    for _ in range(180):
        if c.js("!!document.querySelector('.wk-tab')"):
            ok = True
            break
        time.sleep(1)
    if not ok:
        err = c.js("(document.querySelector('.empty')||{}).textContent || document.body.innerText.slice(0,400)")
        print("✗ 百科页没渲染出来:", err)
        proc.terminate()
        httpd.shutdown()
        return 1

    print("\n=== tab ===")
    print(c.js("""(() => Array.from(document.querySelectorAll('.wk-tab'))
        .map(b => b.textContent.trim().replace(/\\s+/g,' ')).join(' | '))()"""))
    print("页头副标题:", c.js("(document.querySelector('.page-sub')||{}).textContent"))
    print("条数提示:", c.js("(document.querySelector('.wk-count')||{}).textContent"))
    print("分类下拉（前 12 项）:", c.js("""(() => { const s = document.querySelector('#wkSel');
        return s ? Array.from(s.options).slice(0,12).map(o=>o.textContent.trim()).join(' / ') : '无下拉'; })()"""))
    print("下拉总项数:", c.js("""(() => { const s = document.querySelector('#wkSel');
        return s ? s.options.length : 0; })()"""))

    # 等图标转好
    for _ in range(40):
        got = c.js("Array.from(document.querySelectorAll('.wk-ico img')).filter(i=>i.complete&&i.naturalWidth>0).length")
        allc = c.js("document.querySelectorAll('.wk-ico').length")
        if got and allc and int(got) >= int(allc):
            break
        time.sleep(0.5)
    print("图标：图位 %s，已加载 %s" % (
        c.js("document.querySelectorAll('.wk-ico').length"),
        c.js("Array.from(document.querySelectorAll('.wk-ico img')).filter(i=>i.complete&&i.naturalWidth>0).length")))

    for nm in ("君主制", "族裔国家", "公立学校", "消费税"):
        print("\n===== %s =====" % nm)
        print(open_law(c, nm))

    # 按组筛选：只看「公民权」
    c.js("""(() => { const s = document.querySelector('#wkSel');
        const o = Array.from(s.options).find(x => x.textContent.includes('公民权'));
        if (o) { s.value = o.value; s.dispatchEvent(new Event('change',{bubbles:true})); }
        return 'ok'; })()""")
    time.sleep(0.6)
    print("\n筛选「公民权」后条数:", c.js("document.querySelectorAll('.wk-row').length"))

    # 搜索（先把组筛选清掉，否则是在「公民权」组里搜）
    c.js("""(() => { const s = document.querySelector('#wkSel');
        if (s) { s.value = ''; s.dispatchEvent(new Event('change',{bubbles:true})); }
        return 'ok'; })()""")
    time.sleep(0.4)
    c.js("""(() => { const q = document.querySelector('#wkQ');
        q.value = '学校'; q.dispatchEvent(new Event('input',{bubbles:true})); return 'ok'; })()""")
    time.sleep(0.6)
    print("搜索「学校」命中:", c.js("document.querySelectorAll('.wk-row').length"),
          "首条:", c.js("(document.querySelector('.wk-row .wk-name')||{}).textContent"))
    c.js("""(() => { const q = document.querySelector('#wkQ');
        q.value = ''; q.dispatchEvent(new Event('input',{bubbles:true})); return 'ok'; })()""")
    time.sleep(0.4)

    # 回到第一组，展开「君主制」拍照
    c.js("""(() => { const s = document.querySelector('#wkSel');
        s.value = ''; s.dispatchEvent(new Event('change',{bubbles:true})); return 'ok'; })()""")
    time.sleep(0.5)
    c.js("""(() => {
        const rows = Array.from(document.querySelectorAll('.wk-row'));
        const r = rows.find(x => (x.querySelector('.wk-name')||{}).textContent?.includes('君主制'));
        if (r) r.querySelector('.wk-line').click();
        return 'ok'; })()""")
    time.sleep(0.8)

    out = sys.argv[1] if len(sys.argv) > 1 else "vic3-shot.png"
    if not out.endswith(".png"):
        out = "vic3-shot.png"
    shot = c.call("Page.captureScreenshot", {"format": "png"})
    data = shot.get("result", {}).get("data")
    if data:
        with open(os.path.join(HERE, out), "wb") as f:
            f.write(base64.b64decode(data))
        print("\n✓ 已保存 " + out)

    # 再拍一张「族裔国家」的接受度规则（长内容，单独一张）
    c.js("(() => { const r=document.querySelector('.wk-row.open .wk-line'); if(r) r.click(); return 'ok'; })()")
    time.sleep(0.4)
    c.js("""(() => { const s = document.querySelector('#wkSel');
        const o = Array.from(s.options).find(x => x.textContent.includes('公民权'));
        if (o) { s.value = o.value; s.dispatchEvent(new Event('change',{bubbles:true})); }
        return 'ok'; })()""")
    time.sleep(0.6)
    c.js("""(() => {
        const rows = Array.from(document.querySelectorAll('.wk-row'));
        const r = rows.find(x => (x.querySelector('.wk-name')||{}).textContent?.includes('族裔国家'));
        if (r) { r.querySelector('.wk-line').click(); r.scrollIntoView({block:'start'}); }
        return 'ok'; })()""")
    time.sleep(0.9)
    shot = c.call("Page.captureScreenshot", {"format": "png"})
    data = shot.get("result", {}).get("data")
    if data:
        with open(os.path.join(HERE, "vic3-acc.png"), "wb") as f:
            f.write(base64.b64decode(data))
        print("✓ 已保存 vic3-acc.png")

    try:
        proc.terminate()
        httpd.shutdown()
        srv.CTX.switch_game("ck3")
        print("已切回 CK3")
    except Exception:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
