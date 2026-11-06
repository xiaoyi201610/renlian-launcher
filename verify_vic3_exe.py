# -*- coding: utf-8 -*-
"""端到端验证打包后的 exe：切到维多利亚 3，拉百科，核对条数与图标。

做法：exe 起来后会监听一个随机本地端口，从 netstat 里按 PID 找到它，
直接对内置 HTTP 服务发请求 —— 测的是打包产物本身，不是源码。

用法：python verify_vic3_exe.py [--keep]
"""

import os
import sys
import json
import time
import subprocess
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.join(HERE, "dist", "人联启动器.exe")


def post(url, body):
    req = urllib.request.Request(
        url, data=json.dumps(body or {}).encode("utf-8"),
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(req, timeout=180) as r:
        return json.loads(r.read().decode("utf-8"))


def get_raw(url, timeout=180):
    with urllib.request.urlopen(url, timeout=timeout) as r:
        return r.read(), dict(r.headers)


def pids_of_exe():
    tl = subprocess.run(["tasklist", "/FO", "CSV", "/NH"],
                        capture_output=True).stdout.decode("gbk", "ignore")
    return [l.split('","')[1] for l in tl.splitlines() if "人联启动器" in l]


def listen_port(pids):
    ns = subprocess.run(["netstat", "-ano"], capture_output=True).stdout.decode("gbk", "ignore")
    for line in ns.splitlines():
        f = line.split()
        if len(f) >= 5 and f[0].startswith("TCP") and f[3] == "LISTENING" and f[4] in pids:
            return f[1]
    return ""


def kill_all(pids):
    for pid in pids:
        subprocess.run(["taskkill", "/PID", pid, "/F"], capture_output=True)


def main():
    if not os.path.isfile(EXE):
        print("找不到 exe：", EXE)
        return 1
    p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), close_fds=True, env=dict(os.environ))
    print("已启动 %s（pid %d）" % (os.path.basename(EXE), p.pid))

    addr = ""
    for i in range(40):
        time.sleep(1)
        pids = pids_of_exe()
        if pids:
            addr = listen_port(pids)
            if addr:
                print("内置服务就绪：http://%s（%d 秒）" % (addr, i + 1))
                break
    if not addr:
        print("等不到内置服务端口")
        kill_all(pids_of_exe())
        return 1

    base = "http://" + addr
    ok = True
    try:
        # 1) 先看当前游戏
        st = json.loads(get_raw(base + "/api/state")[0].decode("utf-8"))
        print("当前游戏：", st.get("game"), st.get("gameName") or "")

        # 2) 切到维多利亚 3
        sw = post(base + "/api/switch-game", {"game": "vic3"})
        print("切换维多利亚 3：", "成功" if sw.get("ok") else "失败", sw.get("error") or "")
        ok = ok and bool(sw.get("ok"))

        # 3) 拉百科（首次要现抽，给足超时）
        t0 = time.time()
        raw, hdr = get_raw(base + "/api/wiki")      # 默认超时 180 秒，够
        d = json.loads(raw.decode("utf-8"))
        el = time.time() - t0
        print("百科返回：%.2f 秒  %.1f KB" % (el, len(raw) / 1024.0))
        print("  ok=%s supported=%s 游戏=%s %s cached=%s"
              % (d.get("ok"), d.get("supported"), d.get("gameName"), d.get("gameVer"), d.get("cached")))
        tabs = d.get("tabs") or []
        print("  tab：", [(t.get("label"), t.get("count")) for t in tabs])
        total = sum(t.get("count") or 0 for t in tabs)
        laws = d.get("laws") or []
        print("  法律 %d 条 / 法律组 %d / 大类 %s"
              % (len(laws), len(d.get("lawGroups") or []),
                 [(a.get("label"), a.get("n")) for a in d.get("lawAreas") or []]))

        # 4) 抽一条看详情字段
        one = next((x for x in laws if x.get("key") == "law_monarchy"), laws[0] if laws else None)
        grp = next((g for g in d.get("lawGroups") or [] if g.get("key") == (one or {}).get("catKey")), {})
        if one:
            secs = one.get("secs") or []
            print("  样例：%s  组=%s  进步性=%s  审议=%s 天  效果 %d 条  附加区块 %d"
                  % (one.get("name"), one.get("cat"), one.get("prog"), grp.get("days"),
                     len(one.get("effects") or []), len(secs)))
            for s in secs:
                n = len(s.get("items") or s.get("rows") or s.get("tiers") or [])
                print("        · %-8s %-16s %d 项" % (s.get("kind"), s.get("label") or "", n))

        # 5) 图标接口（DDS → PNG）
        icon = (one or {}).get("icon") or ""
        if icon:
            try:
                b, h = get_raw(base + "/api/wiki/law-icon?n=" + urllib.parse.quote(icon), 60)
                print("  图标 %s：%s  %d 字节  %s"
                      % (icon, h.get("Content-Type"), len(b), "PNG" if b[:4] == b"\x89PNG" else "非PNG"))
                ok = ok and b[:4] == b"\x89PNG"
            except Exception as ex:
                print("  图标接口失败：", ex)
                ok = False

        ok = ok and bool(d.get("supported")) and len(laws) == 148 and total == 148
    finally:
        if "--keep" not in sys.argv:
            kill_all(pids_of_exe())
            print("已关闭启动器")
    print("\n==> 结论：", "通过" if ok else "不通过")
    return 0 if ok else 1


if __name__ == "__main__":
    import urllib.parse
    sys.exit(main())
