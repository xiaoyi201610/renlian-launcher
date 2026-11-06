# -*- coding: utf-8 -*-
"""《维多利亚 3》百科 —— 法律部分。

数据全部读游戏本体脚本 + 官方简中本地化，不联网、不抓 wiki：
  game/common/laws/*.txt            单条法律（group / icon / progressiveness / modifier …）
  game/common/law_groups/*.txt      法律组（大类、审议周期、意识形态影响倍率）
  game/common/ideologies/*.txt      各意识形态对每条法律的立场（强烈支持 ~ 强烈反对）
  game/localization/<lang>/*.yml    官方文本

注意两个与其它 Paradox 作品不同的地方：
  · 脚本在 `game/` 下要下沉一层（和 CK3 一样）；
  · 本地化目录叫 `localization`（美式拼写），不是 `localisation`。
"""

import os
import re
import json
import glob
import time
import threading

V3_VER = 3                      # 解析逻辑变更时 +1，强制重建缓存

LANG_MAP = {
    "zh": "simp_chinese",
    "en": "english",
    "fr": "french",
    "de": "german",
    "sv": "english",            # 群星/钢铁雄心有瑞典语，维多利亚 3 官方没有，退回英文
    "ru": "russian",
    "ja": "japanese",
    "ko": "korean",
    "pl": "polish",
    "es": "spanish",
    "pt": "braz_por",
}

D_LAWS = os.path.join("common", "laws")
D_GROUPS = os.path.join("common", "law_groups")
D_IDEO = os.path.join("common", "ideologies")
D_LOC = "localization"

TABS = [
    {"id": "laws", "icon": "⚖", "label": "法律", "layout": "generic"},
]

# 法律组的大类：脚本里只写英文枚举，官方本地化没有这几个键，手工定名
AREA_ORDER = ["power_structure", "economy", "human_rights"]
AREA_LABEL = {
    "power_structure": "权力结构",
    "economy": "经济",
    "human_rights": "人权",
}

# 意识形态立场档位（对应游戏内的 ±2 / ±1 / 0）
STANCE_LABEL = {
    "strongly_approve": "强烈支持",
    "approve": "支持",
    "neutral": "中立",
    "disapprove": "反对",
    "strongly_disapprove": "强烈反对",
}
STANCE_V = {
    "strongly_approve": 2,
    "approve": 1,
    "neutral": 0,
    "disapprove": -1,
    "strongly_disapprove": -2,
}
STANCE_ORDER = ["strongly_approve", "approve", "neutral", "disapprove", "strongly_disapprove"]

# 税率档位（法律里的 tax_modifier_* 块）
TAX_TIERS = [
    ("tax_modifier_very_low", "极低税率"),
    ("tax_modifier_low", "低税率"),
    ("tax_modifier_medium", "中等税率"),
    ("tax_modifier_high", "高税率"),
    ("tax_modifier_very_high", "极高税率"),
]

# 脚本里没有本地化的修饰器（极少，兜底成可读中文）
MOD_ALIAS = {
    "country_legitimacy_headofstate_add": "国家元首所属利益集团执政带来的合法性",
}

APP_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


# ----------------------------------------------------------------- 基础工具
def _read(path):
    try:
        with open(path, encoding="utf-8-sig", errors="ignore") as f:
            return f.read()
    except Exception:
        return ""


def game_root_of(hint=""):
    """定位维多利亚 3 安装目录：脚本在 <root>/game 下。"""
    hint = (hint or "").strip().rstrip("\\/")
    cands = []
    if hint:
        cands += [hint, os.path.join(hint, "Victoria 3")]
    for drive in ("C", "D", "E", "F", "G"):
        for sub in ("Steam", "SteamLibrary", "Games", "Program Files (x86)\\Steam"):
            cands.append("%s:/%s/steamapps/common/Victoria 3" % (drive, sub.replace("\\", "/")))
    for c in cands:
        if os.path.isdir(os.path.join(c, "game", D_LAWS)):
            return c
        if os.path.isdir(os.path.join(c, D_LAWS)):
            return c
    return ""


def _game_dir(root):
    """脚本目录（游戏本体在 game/ 下再套一层）。"""
    if os.path.isdir(os.path.join(root, "game", D_LAWS)):
        return os.path.join(root, "game")
    return root


def _game_version(root):
    """版本号在 launcher/launcher-settings.json：rawVersion 是纯数字，version 带代号。"""
    p = os.path.join(root, "launcher", "launcher-settings.json")
    if os.path.isfile(p):
        t = _read(p)
        m = re.search(r'"rawVersion"\s*:\s*"([^"]+)"', t)
        if m:
            return m.group(1)
        m = re.search(r'"version"\s*:\s*"([^"]+)"', t)
        if m:
            return m.group(1)
    b = os.path.join(root, "game", "checksum_manifest.txt")
    if os.path.isfile(b):
        return time.strftime("%Y-%m-%d", time.localtime(os.path.getmtime(b)))
    return ""


def _cache_path(loc_lang):
    return os.path.join(APP_DIR, "wiki_cache_vic3_%s.json" % loc_lang)


def _icon_dir():
    d = os.path.join(APP_DIR, "vic3_law_icons")
    if not os.path.isdir(d):
        try:
            os.makedirs(d)
        except Exception:
            pass
    return d


# ----------------------------------------------------------------- 本地化
# 值里允许出现转义引号 \" 与转义反斜杠；Paradox 的 yml 值是单行的（换行写成 \n 字面量）
_LOC_RE = re.compile(r'^\s*([A-Za-z0-9_.\-]+):(\d+)?\s*"((?:[^"\\]|\\.)*)"', re.M)


def _load_loc(loc_dir):
    """读一整个语言目录的 yml，返回 {key: raw_text}。"""
    out = {}
    if not os.path.isdir(loc_dir):
        return out
    for f in glob.glob(os.path.join(loc_dir, "**", "*.yml"), recursive=True):
        txt = _read(f)
        if not txt:
            continue
        for m in _LOC_RE.finditer(txt):
            k = m.group(1)
            if k not in out:
                out[k] = m.group(3)
    return out


_FUNC_RE = re.compile(r"\[Concept\('([^']+)'[^\]]*\]", re.I)
_GETX_RE = re.compile(r"\[(Get[A-Za-z]+)\(\s*'([^']+)'\s*\)\.([A-Za-z]+)\]")
_BRACK_RE = re.compile(r"\[([^\[\]]+)\]")
_VAR_RE = re.compile(r"\$([^$]+)\$")
_ICON_RE = re.compile(r"@[a-zA-Z0-9_]+!")
_FMT_RE = re.compile(r"#[a-zA-Z]+(?:\s+[^#]*?)?#!")


def _clean(s, loc, depth=0):
    """把本地化里的脚本标记洗成人话。

    要处理五类：`[concept_x]` 概念引用、`[Concept('x','...')]` 函数式、
    `[GetInstitutionType('x').GetName]` 取值函数、`$var$` 变量、`@icon!` 与 `#bold ... #!` 样式。
    """
    if not s or depth > 8:
        return s or ""

    def _sub_key(k):
        raw = loc.get(k)
        if raw is None:
            return ""
        return _clean(raw, loc, depth + 1)

    s = _FUNC_RE.sub(lambda m: _sub_key(m.group(1)), s)
    s = _GETX_RE.sub(lambda m: _sub_key(m.group(2)), s)
    s = _BRACK_RE.sub(lambda m: _sub_key(m.group(1).split("|")[0].strip())
                      if (m.group(1).split("|")[0].strip().startswith("concept_")
                          or m.group(1).split("|")[0].strip() in loc) else "", s)
    s = _VAR_RE.sub(lambda m: _sub_key(m.group(1).split("|")[0].strip()), s)
    s = _ICON_RE.sub("", s)
    s = _FMT_RE.sub("", s)
    s = s.replace("[Nbsp]", " ").replace("\\n", " ").replace("\\t", " ")
    s = s.replace("”", "").replace("“", "")
    return re.sub(r"\s+", " ", s).strip()


def _L(loc, key):
    return _clean(loc.get(key) or "", loc)


# ----------------------------------------------------------------- 修饰器
def _fmt_num(key, val):
    """按修饰器后缀判定单位。

    `_mult` / `_factor` / 无后缀都是百分比；`_add` 是绝对值，
    但 `_add` 的值落在 0~1 之间时（税率 0.15、受教育机会 0.125、识字率增长 0.0001）
    游戏里也是当比率显示的，一并按百分比处理 —— 这类共 150 多个键，已逐条核对过。
    """
    try:
        f = float(val)
    except Exception:
        return str(val)
    pct = not key.endswith("_add") or (abs(f) < 1 and f != 0)
    if pct:
        x = f * 100.0
        if abs(x - round(x)) < 1e-6:
            return "%+d%%" % round(x)
        s = ("%.4f" % x).rstrip("0").rstrip(".")
        return s + "%" if s.startswith("-") else "+" + s + "%"
    if abs(f - round(f)) < 1e-9:
        return "%+d" % round(f)
    s = ("%.4f" % f).rstrip("0").rstrip(".")
    return s if s.startswith("-") else "+" + s


def _mod_lines(body, loc):
    """解析一段 modifier 块 → ["权威力 +200", "贵族政治力量 +25%", "给予…投票权：是"]

    `_bool` 类修饰器（接受度等级的各种开关）值只有 yes / no，显示成「是 / 否」。
    """
    out = []
    if not body:
        return out
    for m in re.finditer(r"([a-z][a-z0-9_]*)\s*=\s*(-?\d+(?:\.\d+)?|yes|no)\b", body):
        key, val = m.group(1), m.group(2)
        name = MOD_ALIAS.get(key) or _L(loc, key)
        if not name:
            continue
        if val in ("yes", "no"):
            line = "%s：%s" % (name, "是" if val == "yes" else "否")
        else:
            # `_add` 为 0 的没有信息量（脚本里写 0 是为了显式覆盖父级），跳过
            try:
                if key.endswith("_add") and float(val) == 0:
                    continue
            except Exception:
                pass
            line = "%s %s" % (name, _fmt_num(key, val))
        if line not in out:
            out.append(line)
    return out


def _sub(body, key):
    """取顶层 `key = { … }` 的块正文（不穿透嵌套）。"""
    if not body:
        return ""
    m = re.search(r"(?:^|[\s{])" + re.escape(key) + r"\s*=\s*\{", body)
    if not m:
        return ""
    st = body.index("{", m.end() - 1)
    d = 0
    for j in range(st, len(body)):
        if body[j] == "{":
            d += 1
        elif body[j] == "}":
            d -= 1
            if d == 0:
                return body[st + 1:j]
    return ""


def _scalar(body, key):
    if not body:
        return ""
    m = re.search(r"(?:^|[\s{])" + re.escape(key) + r"\s*=\s*\"?([^\"\s{}]+)\"?", body)
    return m.group(1) if m else ""


def _block_body(text, name):
    """取顶层 `name = { … }` 的正文。"""
    m = re.search(r"^" + re.escape(name) + r"\s*=\s*\{", text, re.M)
    if not m:
        return ""
    st = text.index("{", m.end() - 1)
    d = 0
    for j in range(st, len(text)):
        if text[j] == "{":
            d += 1
        elif text[j] == "}":
            d -= 1
            if d == 0:
                return text[st + 1:j]
    return ""


def _list_block(body, key):
    """取 `key = { a b c }` 这种裸列表。"""
    b = _sub(body, key)
    if not b:
        return []
    return [x for x in re.findall(r"[a-zA-Z_][a-zA-Z0-9_]*", b)]


# ----------------------------------------------------------------- 法律
def _scan_groups(gdir):
    """法律组：按脚本里的定义顺序（决定界面上的排列），带大类、审议周期。"""
    out = []
    seen = set()
    for f in sorted(glob.glob(os.path.join(gdir, "*.txt"))):
        txt = _read(f)
        if not txt:
            continue
        for m in re.finditer(r"^(lawgroup_[a-z0-9_]+)\s*=\s*\{", txt, re.M):
            key = m.group(1)
            if key in seen:
                continue
            seen.add(key)
            body = _block_body(txt, key)
            cat = _scalar(body, "law_group_category") or "power_structure"
            days = _scalar(body, "base_enactment_days")
            ideo = _scalar(body, "ideological_opinion_impact")
            out.append({
                "key": key,
                "area": cat,
                "days": int(days) if days.isdigit() else 100,
                "ideoMult": ideo,
                "order": len(out),
            })
    return out


def _icon_rel(icon_field):
    """`gfx/interface/icons/law_icons/monarchy.dds` → `monarchy`"""
    if not icon_field:
        return ""
    b = os.path.basename(icon_field.replace("\\", "/"))
    return os.path.splitext(b)[0]


def _prog_label(v):
    """进步性 -100 ~ 100：负数保守、正数进步。"""
    if v <= -60:
        return "%d（极端保守）" % v
    if v < 0:
        return "%d（保守）" % v
    if v == 0:
        return "0（中立）"
    if v < 60:
        return "+%d（进步）" % v
    return "+%d（极端进步）" % v


def _scan_laws(gdir, loc, groups):
    """逐条法律抽字段。返回 (条目列表, key→组key)。"""
    gmap = {g["key"]: g for g in groups}
    raw = []           # 先攒原始数据，变体关系要等全部读完才能建
    for f in sorted(glob.glob(os.path.join(gdir, "*.txt"))):
        txt = _read(f)
        if not txt:
            continue
        for m in re.finditer(r"^(law_[a-z0-9_]+)\s*=\s*\{", txt, re.M):
            key = m.group(1)
            if key.startswith("lawgroup_"):
                continue
            body = _block_body(txt, key)
            raw.append((key, body, os.path.basename(f)))

    by_key = {}
    for key, body, _f in raw:
        by_key[key] = (key, body)

    items = []
    for key, body in [v for v in by_key.values()]:
        group_key = _scalar(body, "group")
        ginfo = gmap.get(group_key) or {}
        prog = _scalar(body, "progressiveness")
        try:
            prog_v = int(prog)
        except Exception:
            prog_v = 0

        # 效果：国家层面的 modifier + 机构 modifier（两条来源，分开展示）
        effects = _mod_lines(_sub(body, "modifier"), loc)

        # 公民权 / 教会与国家这几组法律把真正的规则写在 acceptance_modifier 里：
        # 五个接受度等级（暴力敌视 / 文化抹除 / 公开偏见 / 二等公民 / 完全接纳）各自的
        # 投票权、同化、政治力量、工资、从业资格、兵役与公职限制
        acc_fx = _mod_lines(_sub(body, "acceptance_modifier"), loc)

        inst_key = _scalar(body, "institution")
        inst_fx = _mod_lines(_sub(body, "institution_modifier"), loc)
        if inst_key and inst_fx:
            iname = _L(loc, inst_key) or inst_key
            inst_fx = ["%s %s" % (iname, x) for x in inst_fx]

        # 税率档位：tax_modifier_* 各档的效果
        tax_tiers = []
        for tk, tlabel in TAX_TIERS:
            tb = _sub(body, tk)
            if not tb:
                continue
            tl = _mod_lines(tb, loc)
            if tl:
                tax_tiers.append({"label": tlabel, "effects": tl})

        # 解锁 / 互斥 / 前置
        techs = [_L(loc, t) or t for t in _list_block(body, "unlocking_technologies")]
        disallow = [_L(loc, t) or t for t in _list_block(body, "disallowing_laws")]
        req_or = [_L(loc, t) or t for t in _list_block(body, "requires_law_or")]
        unlock_laws = [_L(loc, t) or t for t in _list_block(body, "unlocking_laws")]

        parent = _scalar(body, "parent")
        # 制定条件：can_enact 里的可见提示（只取有中文的 custom_tooltip 之类）
        cond = _cond_lines(_sub(body, "can_enact"), loc)

        name = _L(loc, key) or key
        desc = _L(loc, key + "_desc")

        # 进步性已经用详情顶部的刻度条表示，这里不再重复占一个格子
        attrs = []
        if ginfo.get("days"):
            attrs.append({"label": "审议周期", "value": "%d 天" % ginfo["days"]})
        if ginfo.get("ideoMult"):
            try:
                mf = float(ginfo["ideoMult"])
                if abs(mf - 1.0) > 1e-6:
                    attrs.append({"label": "意识形态影响", "value": "×%s" % ("%g" % mf)})
            except Exception:
                pass

        items.append({
            "key": key,
            "name": name,
            "desc": desc,
            "cat": _L(loc, group_key) or group_key,
            "catKey": group_key,
            "area": AREA_LABEL.get(ginfo.get("area"), ""),
            "icon": _icon_rel(_scalar(body, "icon")),
            "prog": prog_v,
            "attrs": attrs,
            "effects": effects,
            "accFx": acc_fx,
            "instFx": inst_fx,
            "taxTiers": tax_tiers,
            "techs": techs,
            "disallow": disallow,
            "reqOr": req_or,
            "unlockLaws": unlock_laws,
            "cond": cond,
            "parent": parent,
            "stances": [],
            "subLaws": [],       # 本法律的变体（由特定文化/政体替换），前端用 secs 展示
            "secs": [],
        })
    return items


def _cond_lines(body, loc):
    """`can_enact` 里能直接读到的中文条件（多数是脚本触发器，读不出就不显示）。"""
    out = []
    if not body:
        return out
    for k in re.findall(r"custom_tooltip\s*=\s*([a-z0-9_]+)", body):
        t = _L(loc, k)
        if t and t not in out:
            out.append(t)
    return out[:4]


def _scan_stances(idir, loc):
    """意识形态对每条法律的立场。返回 {law_key: [{name, key, v, label, group}]}"""
    out = {}
    fname_label = {
        "00_ig_ideologies.txt": "利益集团",
        "00_ig_ideologies_flavored.txt": "利益集团·特色",
        "00_ig_ideologies_event.txt": "事件意识形态",
        "03_ig_ideologies_movement.txt": "政治运动",
        "00_rlit_ideologies.txt": "宗教意识形态",
        "01_character_ideologies.txt": "人物意识形态",
        "02_character_french_flavored.txt": "人物意识形态·法国",
    }
    for f in sorted(glob.glob(os.path.join(idir, "*.txt"))):
        txt = _read(f)
        if not txt:
            continue
        src = fname_label.get(os.path.basename(f), "意识形态")
        for m in re.finditer(r"^([a-z][a-z0-9_]+)\s*=\s*\{", txt, re.M):
            ideo_key = m.group(1)
            if ideo_key.startswith("law") or ideo_key in ("icon",):
                continue
            body = _block_body(txt, ideo_key)
            if not body or "lawgroup_" not in body:
                continue
            iname = _L(loc, ideo_key) or ideo_key
            for lm in re.finditer(
                    r"(law_[a-z0-9_]+)\s*=\s*(strongly_approve|approve|neutral|disapprove|strongly_disapprove)",
                    body):
                law, stance = lm.group(1), lm.group(2)
                out.setdefault(law, []).append({
                    "key": ideo_key,
                    "name": iname,
                    "v": STANCE_V.get(stance, 0),
                    "label": STANCE_LABEL.get(stance, stance),
                    "src": src,
                })
    return out


def _build(root, ui_lang):
    lang = LANG_MAP.get(ui_lang or "zh", "simp_chinese")
    gdir = _game_dir(root)
    loc = _load_loc(os.path.join(gdir, D_LOC, lang))
    if not loc:
        loc = _load_loc(os.path.join(gdir, D_LOC, "english"))

    groups = _scan_groups(os.path.join(gdir, D_GROUPS))
    items = _scan_laws(os.path.join(gdir, D_LAWS), loc, groups)
    stances = _scan_stances(os.path.join(gdir, D_IDEO), loc)

    assets = {it["key"]: it for it in items}

    # 变体继承父法的意识形态立场（脚本注释写明 "Inherits ideological stances from parent"）
    for k, lst in stances.items():
        if k in assets:
            assets[k]["stances"] = lst

    # 变体：parent 指向谁，谁就多一个子项
    for it in items:
        p = it["parent"]
        if p and p in assets:
            assets[p]["subLaws"].append({"key": it["key"], "name": it["name"]})
        elif it["stances"]:
            continue
    for it in items:
        # 自己没有立场声明、但有父法 → 借父法的（界面上标注继承）
        if not it["stances"] and it["parent"] in assets:
            pv = assets[it["parent"]]["stances"]
            if pv:
                it["stances"] = [dict(x, inherited=True) for x in pv]

    # 排序：大类 → 组（脚本顺序）→ 进步性升序
    gmap = {g["key"]: g for g in groups}
    for it in items:
        g = gmap.get(it["catKey"]) or {}
        it["_gorder"] = g.get("order", 999)
        it["_aorder"] = AREA_ORDER.index(g.get("area")) if g.get("area") in AREA_ORDER else 9
        st = it["stances"]
        it["stanceSum"] = sum(x["v"] for x in st)
    items.sort(key=lambda x: (x["_aorder"], x["_gorder"], x["prog"], x["name"]))

    for it in items:
        it.pop("_gorder", None)
        it.pop("_aorder", None)

    # 互斥是双向的：脚本只在一边写 disallowing_laws，界面上两边都得看得到
    rev = {}
    for it in items:
        for nm in it["disallow"]:
            rev.setdefault(nm, [])
            if it["name"] not in rev[nm]:
                rev[nm].append(it["name"])
    for it in items:
        for nm in rev.get(it["name"], []):
            if nm not in it["disallow"]:
                it["disallow"].append(nm)

    # 详情页的附加区块：前端按 kind 统一渲染（list / chips / tiers / stances）
    for it in items:
        secs = []
        if it["accFx"]:
            secs.append({"label": "接受度等级规则", "kind": "list", "items": it["accFx"],
                         "hint": "按人群的接受度等级（暴力敌视 / 文化抹除 / 公开偏见 / 二等公民 / 完全接纳）分别生效"})
        if it["instFx"]:
            secs.append({"label": "机构效果", "kind": "list", "items": it["instFx"]})
        if it["taxTiers"]:
            secs.append({"label": "税率档位", "kind": "tiers", "items": it["taxTiers"],
                         "hint": "五个税率等级下，这条法律分别提供多少税收"})
        if it["stances"]:
            # 一条法律可能有 80 多个意识形态表态（治理原则组每个都说话），
            # 按档位分成五行，前端一行一行列，强弱关系看得清
            sgroups = []
            for sk in STANCE_ORDER:
                arr = [x for x in it["stances"] if x["label"] == STANCE_LABEL[sk]]
                if arr:
                    arr.sort(key=lambda x: x["name"])
                    # 同一档位里会有同名的不同意识形态（flavored / 运动 / 人物各有一套
                    # 「共产主义者」），界面上只留一个，来源数放进 title 里
                    seen = {}
                    uniq = []
                    for x in arr:
                        nm = x["name"]
                        if nm in seen:
                            seen[nm]["dup"] += 1
                            s2 = x.get("src", "")
                            if s2 and s2 not in seen[nm]["srcs"]:
                                seen[nm]["srcs"].append(s2)
                            continue
                        cur = {"name": nm, "src": x.get("src", ""),
                               "inh": bool(x.get("inherited")), "dup": 1,
                               "srcs": [x.get("src", "")] if x.get("src") else []}
                        seen[nm] = cur
                        uniq.append(cur)
                    sgroups.append({
                        "key": sk,
                        "label": STANCE_LABEL[sk],
                        "v": STANCE_V[sk],
                        "items": uniq,
                        "n": len(uniq),
                        "raw": len(arr),
                    })
            secs.append({"label": "意识形态立场", "kind": "stances",
                         "groups": sgroups, "n": sum(g["n"] for g in sgroups),
                         "hint": "利益集团、政治运动与人物意识形态在立法表决时的态度；变体法律沿用父法的立场"})
        if it["techs"]:
            secs.append({"label": "解锁科技", "kind": "chips", "items": it["techs"]})
        if it["reqOr"]:
            secs.append({"label": "需要先采用其中一条", "kind": "chips", "items": it["reqOr"]})
        if it["disallow"]:
            secs.append({"label": "互斥法律", "kind": "chips", "items": it["disallow"], "bad": True})
        if it["unlockLaws"]:
            secs.append({"label": "采用后可解锁", "kind": "chips", "items": it["unlockLaws"]})
        if it["cond"]:
            secs.append({"label": "制定条件", "kind": "list", "items": it["cond"]})
        if it["subLaws"]:
            secs.append({"label": "变体法律", "kind": "chips",
                         "items": ["%s（%s）" % (v["name"], it["name"]) for v in it["subLaws"]],
                         "hint": "由特定文化或政体替换本法律，效果与立场各自独立"})
        it["secs"] = secs

    # 组清单（带条数），供前端筛选
    cnt = {}
    for it in items:
        cnt[it["catKey"]] = cnt.get(it["catKey"], 0) + 1
    law_groups = []
    for g in groups:
        if not cnt.get(g["key"]):
            continue
        law_groups.append({
            "key": g["key"],
            "label": _L(loc, g["key"]) or g["key"],
            "desc": _L(loc, g["key"] + "_desc"),
            "area": g["area"],
            "areaLabel": AREA_LABEL.get(g["area"], ""),
            "days": g["days"],
            "ideoMult": g["ideoMult"],
            "n": cnt[g["key"]],
        })
    law_areas = [{"key": a, "label": AREA_LABEL[a],
                  "n": sum(1 for g in law_groups if g["area"] == a)}
                 for a in AREA_ORDER if any(g["area"] == a for g in law_groups)]

    tabs = []
    for t in TABS:
        d = dict(t)
        d["count"] = len(items) if t["id"] == "laws" else 0
        tabs.append(d)

    return {
        "ok": True,
        "supported": True,
        "game": "vic3",
        "gameName": "Victoria 3",
        "gameVer": _game_version(root),
        "gameRoot": root,
        "uiLang": ui_lang,
        "tabs": tabs,
        "laws": items,
        "lawGroups": law_groups,
        "lawAreas": law_areas,
        "cached": False,
        "buildSeconds": 0,
    }


# ----------------------------------------------------------------- 对外接口
_LOCK = threading.Lock()
_MEM = {"key": "", "data": None}


def _sig(root, loc_lang):
    parts = [V3_VER, root, loc_lang]
    try:
        parts.append(os.path.getmtime(os.path.join(_game_dir(root), D_LAWS)))
    except Exception:
        parts.append(0)
    return "|".join(str(x) for x in parts)


def load(game_root="", ui_lang="zh", force=False):
    """百科首屏一次请求，多线程下要串起来，避免重复重建 / 同时写缓存。"""
    with _LOCK:
        return _load(game_root, ui_lang, force)


def _load(game_root="", ui_lang="zh", force=False):
    root = game_root_of(game_root)
    if not root:
        return {"ok": False, "supported": True, "game": "vic3", "gameName": "Victoria 3",
                "tabs": [], "laws": [], "lawGroups": [], "lawAreas": [],
                "error": "没找到游戏本体目录，请在设置里确认《维多利亚 3》的安装位置"}
    lang = LANG_MAP.get(ui_lang or "zh", "simp_chinese")
    sig = _sig(root, lang)
    if not force and _MEM["data"] and _MEM["key"] == sig:
        d = dict(_MEM["data"])
        d["cached"] = True
        d["buildSeconds"] = 0
        return d

    cp = _cache_path(lang)
    if not force and os.path.isfile(cp):
        try:
            with open(cp, encoding="utf-8") as f:
                cached = json.load(f)
            if cached.get("_sig") == sig and cached.get("laws"):
                data = dict(cached)
                data.pop("_sig", None)
                data["cached"] = True
                data["buildSeconds"] = 0
                data.setdefault("supported", True)      # 老缓存里没有这个字段
                data.setdefault("ok", True)
                _MEM["key"], _MEM["data"] = sig, data
                return data
        except Exception:
            pass

    t0 = time.time()
    data = _build(root, ui_lang)
    data["buildSeconds"] = round(time.time() - t0, 2)
    data["cached"] = False
    try:
        # 先写临时文件再换名：进程被强关时不会留下半截的 0 字节缓存
        tmp = cp + ".tmp"
        with open(tmp, "w", encoding="utf-8") as f:
            payload = dict(data)
            payload["_sig"] = sig
            json.dump(payload, f, ensure_ascii=False)
        os.replace(tmp, cp)
    except Exception as ex:             # 写不进去只影响下次重建，但要留痕，别静默吞
        import sys
        print("[vic3-cache] 写入失败：%s" % ex, file=sys.stderr)
    _MEM["key"], _MEM["data"] = sig, data
    return data


def law_icon_png(game_root, name) -> str:
    """法律图标的本地 PNG（DDS 转一次，之后直接读缓存）。失败返回空串。"""
    name = (name or "").strip()
    if not name:
        return ""
    root = game_root_of(game_root)
    if not root:
        return ""
    safe = re.sub(r"[^0-9A-Za-z_]", "_", name)
    out = os.path.join(_icon_dir(), safe + ".png")
    if os.path.isfile(out) and os.path.getsize(out) > 0:
        return out

    src = os.path.join(_game_dir(root), "gfx", "interface", "icons", "law_icons", name + ".dds")
    if not os.path.isfile(src):
        return ""
    try:
        from PIL import Image
        im = Image.open(src).convert("RGBA")
        w, h = im.size
        side = min(w, h)
        im = im.crop(((w - side) // 2, (h - side) // 2, (w + side) // 2, (h + side) // 2))
        im.thumbnail((128, 128), Image.LANCZOS)
        tmp = out + ".%d.tmp" % os.getpid()
        im.save(tmp, "PNG")
        os.replace(tmp, out)
        return out
    except Exception:
        return ""
