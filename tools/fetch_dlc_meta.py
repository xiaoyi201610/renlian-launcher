# -*- coding: utf-8 -*-
"""
从 Steam 商店 API 拉取 9 款 P 社游戏的完整 DLC 清单，生成 dlc_meta.py。

只拉清单（appid、中文名、发布日期、封面图 CDN URL），不下载封面图
（封面图用 Steam CDN URL 直连，运行时刷新新 DLC 也能直接显示）。

输出：backend/dlc_meta.py
结构：DLC_META = { 游戏key: { steam_id(str): {name, date, header} } }
"""
import os
import io
import sys
import json
import time
import urllib.request

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_META = os.path.join(HERE, "backend", "dlc_meta.py")

GAMES = {
    "ck3": "1158310",
    "ck2": "203770",
    "eu4": "236850",
    "hoi4": "394360",
    "stellaris": "281990",
    "vic3": "529340",
    "imperator": "859580",
    "vic2": "42960",
    "skylines": "255710",
}

UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}


def fetch_json(url, timeout=12):
    req = urllib.request.Request(url, headers=UA)
    # 禁用代理直连（Steam++ 会把 Steam 域名改写到 127.0.0.1，直连才能通）
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    return json.loads(opener.open(req, timeout=timeout).read())


def fetch_app(appid):
    url = ("https://store.steampowered.com/api/appdetails?appids=%s"
           "&l=schinese&cc=cn" % appid)
    try:
        d = fetch_json(url)
        app = d.get(str(appid), {})
        return app.get("data") if app.get("success") else None
    except Exception:
        return None


def main():
    result = {}
    for key, appid in GAMES.items():
        print("=== %s (%s) ===" % (key, appid), flush=True)
        parent = fetch_app(appid)
        if not parent:
            print("  ! 主游戏拉取失败", flush=True)
            continue
        dlc_ids = [str(x) for x in (parent.get("dlc") or [])]
        print("  %d 个 DLC" % len(dlc_ids), flush=True)
        game_meta = {}
        for i, did in enumerate(dlc_ids):
            d = fetch_app(did)
            if not d:
                continue
            rel = d.get("release_date") or {}
            date_str = rel.get("date", "") if isinstance(rel, dict) else ""
            game_meta[did] = {
                "name": d.get("name") or "",
                "date": date_str,
                "header": d.get("header_image") or "",
            }
            if (i + 1) % 10 == 0:
                print("  %d/%d" % (i + 1, len(dlc_ids)), flush=True)
            time.sleep(0.1)
        result[key] = game_meta
        print("  完成 %d 个" % len(game_meta), flush=True)
        time.sleep(0.3)

    lines = [
        "# -*- coding: utf-8 -*-",
        "# 各 P 社游戏的 Steam 商店 DLC 元数据（自动生成，勿手改）",
        "# 结构：{ 游戏key: { steam_id(str): {name(中文名), date(发布日期), header(封面图URL)} } }",
        "DLC_META = {",
    ]
    for key, meta in result.items():
        lines.append("    %r: {" % key)
        for sid, m in sorted(meta.items()):
            lines.append(
                "        %r: {'name': %r, 'date': %r, 'header': %r}," % (
                    sid, m.get("name", ""), m.get("date", ""), m.get("header", "")
                )
            )
        lines.append("    },")
    lines.append("}")
    with open(OUT_META, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print("\n完成，写入 %s" % OUT_META, flush=True)
    for key, meta in result.items():
        print("  %-10s %d 个 DLC" % (key, len(meta)), flush=True)


if __name__ == "__main__":
    main()
