# -*- coding: utf-8 -*-
"""端到端验证：钢铁雄心百科（理念 / 军官团与顾问 / 国策树）。
用法：
  python shot_hoi4.py                 # 体检 + 存 hoi4-shot.png
  python shot_hoi4.py 德国 1800       # 指定国策树 + 输出宽度（仅调试用）
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
PORT = 9471
WEB = 8807
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
        res = r.get("result", {})
        if res.get("exceptionDetails"):
            return {"__err": str(res["exceptionDetails"].get("text"))}
        return (res.get("result") or {}).get("value")


def main():
    import server as srv
    srv.CTX.switch_game("hoi4")
    print("已切到钢铁雄心，gameRoot =", srv.CTX.cfg.get("gameRoot"))
    httpd, port = srv.serve(WEB)
    print("服务已起:", port)

    proc = subprocess.Popen([
        EDGE, "--headless=new", "--disable-gpu", "--no-first-run",
        "--remote-debugging-port=%d" % PORT,
        "--user-data-dir=" + os.path.join(os.environ["TEMP"], "_edge_hoi4"),
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
        err = c.js("(document.querySelector('.empty')||{}).textContent || document.body.innerText.slice(0,300)")
        print("✗ 百科页没渲染出来:", err)
        proc.terminate()
        httpd.shutdown()
        return 1

    print("\n=== tab 列表 ===")
    print(c.js("""(() => Array.from(document.querySelectorAll('.wk-tab'))
        .map(b => b.textContent.trim().replace(/\\s+/g,' ')).join(' | '))()"""))

    # 两个列表页
    for tid in ("ideas", "officers"):
        c.js("document.querySelector('.wk-tab[data-tab=\"%s\"]').click(); 'ok'" % tid)
        time.sleep(0.6)
        n = c.js("document.querySelectorAll('.wk-row').length")
        first = c.js("""(() => { const r = document.querySelector('.wk-row');
            return r ? (r.querySelector('.wk-name')||{}).textContent : ''; })()""")
        cats = c.js("""(() => Array.from(document.querySelectorAll('.wk-cat'))
            .map(b => b.textContent.trim().replace(/\\s+/g,'')).slice(0,10).join(' / '))()""")
        sel = c.js("""(() => { const s = document.querySelector('#wkSel');
            return s ? Array.from(s.options).slice(0,10).map(o=>o.textContent.trim()).join(' / ') : ''; })()""")
        print("\n[%s] %s 条  首条=%s" % (tid, n, first))
        if cats:
            print("   分类: " + cats)
        if sel:
            print("   下拉: " + sel)

    # 抽一条展开看效果
    for tid, name in (("ideas", "正规传承"), ("officers", "步兵指挥官")):
        c.js("document.querySelector('.wk-tab[data-tab=\"%s\"]').click(); 'ok'" % tid)
        time.sleep(0.5)
        hit = c.js("""(() => {
            const rows = Array.from(document.querySelectorAll('.wk-row'));
            const r = rows.find(x => (x.querySelector('.wk-name')||{}).textContent?.includes(%s));
            if (!r) return 'notfound';
            r.querySelector('.wk-line').click();
            return 'clicked';
        })()""" % json.dumps(name, ensure_ascii=False))
        if hit != "clicked":
            print("\n✗ [%s] 找不到 %s" % (tid, name))
            continue
        time.sleep(0.5)
        txt = c.js("""(() => {
            const r = document.querySelector('.wk-row.open');
            if (!r) return 'no-open';
            const nm = (r.querySelector('.wk-name')||{}).textContent||'';
            const stats = Array.from(r.querySelectorAll('.wk-stat')).map(s=>s.textContent.trim().replace(/\\s+/g,' '));
            const eff = Array.from(r.querySelectorAll('.wk-eff li')).map(x=>x.textContent);
            return JSON.stringify({name:nm, stats:stats, eff:eff.slice(0,6)});
        })()""")
        print("\n===== [%s] %s =====" % (tid, name))
        print(txt)
        c.js("(() => { const r=document.querySelector('.wk-row.open .wk-line'); if(r) r.click(); return 'ok'; })()")

    # 国策树
    c.js("document.querySelector('.wk-tab[data-tab=\"focus\"]').click(); 'ok'")
    for _ in range(60):
        if c.js("document.querySelectorAll('.fx-node').length > 0"):
            break
        time.sleep(0.5)

    tree_name = sys.argv[1] if len(sys.argv) > 1 else ""
    if tree_name:
        c.js("""(() => { const s = document.querySelector('#fxTree');
            const o = Array.from(s.options).find(x => x.textContent.includes(%s));
            if (o) { s.value = o.value; s.dispatchEvent(new Event('change',{bubbles:true})); }
            return 'ok'; })()""" % json.dumps(tree_name, ensure_ascii=False))
        time.sleep(1.2)

    stats = c.js("""(() => {
        const nodes = document.querySelectorAll('.fx-node');
        const imgs = document.querySelectorAll('.fx-node img');
        const loaded = Array.from(imgs).filter(i => i.src && i.complete && i.naturalWidth > 0).length;
        return JSON.stringify({
            tree: (document.querySelector('#fxTree')||{}).value,
            treeLabel: (document.querySelector('#fxTree')||{}).selectedOptions?.[0]?.textContent || '',
            nodes: nodes.length,
            links: document.querySelectorAll('#fxLinks path').length,
            imgs: imgs.length,
            loaded: loaded,
            scale: (typeof FX !== 'undefined') ? FX.scale.toFixed(2) : '-',
            zoomText: (document.querySelector('#fxZoom')||{}).textContent || ''
        });
    })()""")
    print("\n=== 国策树画布 ===")
    print(stats)

    # 点第一个节点看详情
    c.js("(() => { const n = document.querySelector('.fx-node'); if (n) n.click(); return 'ok'; })()")
    time.sleep(0.4)
    print("\n=== 节点详情 ===")
    print(c.js("(document.querySelector('#fxDetail')||{}).innerText || 'empty'"))

    # 缩放检查
    c.js("(() => { document.querySelector('#fxIn').click(); document.querySelector('#fxIn').click(); return 'ok'; })()")
    time.sleep(0.3)
    print("\n缩放后:", c.js("(document.querySelector('#fxZoom')||{}).textContent"))

    # 搜索
    c.js("""(() => { const q = document.querySelector('#fxQ');
        q.value = '工业'; q.dispatchEvent(new Event('input',{bubbles:true})); return 'ok'; })()""")
    time.sleep(0.5)
    print("搜索“工业”命中:", c.js("document.querySelectorAll('.fx-node.hit').length"),
          " 变暗:", c.js("document.querySelectorAll('.fx-node.dim').length"))
    c.js("""(() => { const q = document.querySelector('#fxQ');
        q.value = ''; q.dispatchEvent(new Event('input',{bubbles:true})); return 'ok'; })()""")
    time.sleep(0.4)

    # 等图标转换队列消化完（首次每个 DDS 要转一次 PNG）
    # 注意：只有进入视口的节点才会被赋值 src（懒加载），所以分母是「带 src 的」，
    # 不是全部节点。剩下的要平移过去才会加载，这是设计如此。
    for _ in range(30):
        n = c.js("Array.from(document.querySelectorAll('.fx-node img')).filter(i=>i.src&&i.complete&&i.naturalWidth>0).length")
        want = c.js("document.querySelectorAll('.fx-node img[src]').length")
        if n and want and int(n) >= int(want) and int(n) > 30:
            break
        time.sleep(0.5)
    vis = c.js("document.querySelectorAll('.fx-node img[src]').length")
    got = c.js("Array.from(document.querySelectorAll('.fx-node img')).filter(i=>i.src&&i.complete&&i.naturalWidth>0).length")
    allimg = c.js("document.querySelectorAll('.fx-node img').length")
    print("图标：节点共 %s 个图位，当前视口内 %s 个，已加载 %s 个（其余平移过去才加载）"
          % (allimg, vis, got))

    # 截图
    out = sys.argv[2] if len(sys.argv) > 2 else "hoi4-shot.png"
    if not out.endswith(".png"):
        out = "hoi4-shot.png"
    shot = c.call("Page.captureScreenshot", {"format": "png"})
    data = shot.get("result", {}).get("data")
    if data:
        with open(os.path.join(HERE, out), "wb") as f:
            f.write(base64.b64decode(data))
        print("\n✓ 已保存 " + out)

    # 回到「国家理念」页复核：不能有裸键（`SWE_xxx_dummy` 那类），
    # 顾问型理念（效果只写在 traits 上）要能看到展开后的效果
    c.js("document.querySelector('.wk-tab[data-tab=\"ideas\"]').click(); 'ok'")
    time.sleep(0.6)
    bare = c.js("""(() => {
        const bad = [];
        document.querySelectorAll('.wk-row .wk-name').forEach(e => {
            const t = (e.textContent || '').trim();
            if (/^[A-Za-z0-9_]+$/.test(t) && t.includes('_')) bad.push(t);
        });
        return JSON.stringify({rows: document.querySelectorAll('.wk-row').length,
                               bare: bad.length, sample: bad.slice(0, 5)});
    })()""")
    print("\n理念页裸键检查:", bare)

    hit = c.js("""(() => {
        const q = document.querySelector('#wkQ');
        q.value = '鲍里斯'; q.dispatchEvent(new Event('input',{bubbles:true}));
        return 'ok';
    })()""")
    time.sleep(0.8)
    c.js("""(() => { const r = document.querySelector('.wk-row .wk-line');
        if (r) r.click(); return 'ok'; })()""")
    time.sleep(0.5)
    print("顾问型理念展开:", c.js("""(() => {
        const r = document.querySelector('.wk-row.open');
        if (!r) return 'no-open';
        return JSON.stringify({
            name: (r.querySelector('.wk-name')||{}).textContent,
            eff: Array.from(r.querySelectorAll('.wk-eff li')).map(x=>x.textContent).slice(0,5)});
    })()"""))
    shot = c.call("Page.captureScreenshot", {"format": "png"})
    data = shot.get("result", {}).get("data")
    if data:
        with open(os.path.join(HERE, "hoi4-ideas.png"), "wb") as f:
            f.write(base64.b64decode(data))
        print("✓ 已保存 hoi4-ideas.png")

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
