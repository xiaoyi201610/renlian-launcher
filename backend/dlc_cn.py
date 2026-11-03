# -*- coding: utf-8 -*-
"""各 P 社游戏 DLC 的中文名对照表（内置，完全离线）。

为什么要有这个文件
------------------
Paradox 在 Steam 商店里没给 DLC 设中文名（`l=schinese` 拉回来还是英文），
游戏本体的 localisation 里也没有 DLC 名称的翻译（DLC 名只在启动器/商店显示）。
所以只能内置一份对照表，这样**任何玩家装上启动器都能看到中文**，
不需要联网、也不需要他本机装过对应游戏的中文包。

索引方式
--------
键 = 归一化后的英文名（小写），见 normalize()：
    "Expansion - Europa Universalis IV: Conquest of Paradise"  ->  "conquest of paradise"
    "Content Pack - Crusader Kings II: Horse Lords"            ->  "horse lords"
    "Victoria 3: Iberian Twilight - Immersion Pack"            ->  "iberian twilight"
    "Cities: Skylines - Content Creator Pack: Africa in Miniature"
                                                               ->  "content creator pack: africa in miniature"

用英文名而不是目录名做键：目录名各游戏规则不同、还会变，英文名相对稳定，
而且同一款 DLC 在不同版本里目录名变了也照样能命中。
"""

from __future__ import annotations

import os
import json
import re

# 前缀类别（"Expansion - xxx" / "Music - xxx" / "DLC - xxx" ...）
# 注意：不包含 "content creator pack" —— 那部分保留在名字里，
# 中文显示成「内容创作者包：xxx」更清楚。
_PREFIX_KIND = (
    r"(?:expansion|immersion pack|content pack|"
    r"music pack|music|e-?book|dlc|collection|cosmetic pack|flavor pack|"
    r"country pack|art book|soundtrack)"
)
# 游戏名前缀（"Crusader Kings II: xxx" / "Cities: Skylines - xxx" ...）
# 注意：长的要放前面（imperator: rome 必须先于 imperator）
_PREFIX_GAME = (
    r"(?:crusader kings ii|crusader kings iii|europa universalis iv|europa universalis v|"
    r"hearts of iron iv|victoria 3|victoria ii|victoria 2|imperator: rome|"
    r"cities: skylines ii|cities: skylines|stellaris|surviving mars|"
    r"empire of sin|prison architect)"
)
_RE_PREFIX = re.compile(
    r"^(?:%s|%s)\s*[-\u2013\u2014:]\s*" % (_PREFIX_KIND, _PREFIX_GAME), re.I)
# "Crusader Kings III Creator Pack: xxx" —— 游戏名和类别连在一起，单独一条规则
_RE_CREATOR = re.compile(r"^(?:%s)\s+creator pack\s*:\s*" % _PREFIX_GAME, re.I)
# "E-Book Crusader Kings II: xxx" —— 类别和游戏名之间是空格（不是破折号/冒号）
_RE_EBOOK = re.compile(r"^e-?book\s+(?=%s)" % _PREFIX_GAME, re.I)
# 后缀类别（"Iberian Twilight - Immersion Pack"）
_RE_SUFFIX = re.compile(
    r"\s*[-\u2013\u2014]\s*(?:immersion pack|music pack|expansion|art pack|"
    r"mechanics pack|region pack|bonus pack|content pack|flavor pack|"
    r"unit pack|sprite pack)\s*$", re.I)


def normalize(name: str) -> str:
    """把商店里的英文 DLC 名压成查表用的键。"""
    s = (name or "").strip()
    # "E-Book Crusader Kings II: X" / "Crusader Kings III Creator Pack: X"
    # 这类"类别 + 游戏名"连写的，先单独剥掉
    s = _RE_EBOOK.sub("", s)
    s = _RE_CREATOR.sub("", s)
    # 反复剥前缀（"Expansion - Europa Universalis IV: X" 要剥两层）
    for _ in range(4):
        s2 = _RE_PREFIX.sub("", s)
        if s2 == s:
            break
        s = s2
    s = _RE_CREATOR.sub("", s)
    s = _RE_SUFFIX.sub("", s)
    s = re.sub(r"\s+", " ", s).strip().lower()
    return s


# ---------------------------------------------------------------- 对照表
# 顺序按游戏分组，方便以后补新 DLC。
DLC_CN_BY_NAME = {
    # ======================= 十字军之王 III =======================
    "all under heaven": "普天之下",
    "roads to power": "权力之路",
    "tours & tournaments": "巡游与比武",
    "royal court": "皇家宫廷",
    "by god alone": "唯凭神意",
    "khans of the steppe": "草原可汗",
    "legends of the dead": "亡者传说",
    "legacy of persia": "波斯的遗产",
    "fate of iberia": "伊比利亚的命运",
    "northern lords": "北方领主",
    "coronations": "加冕典礼",
    "wandering nobles": "流浪贵族",
    "wards & wardens": "监护与守护",
    "friends & foes": "友与敌",
    "celestial court attire": "天朝宫廷服饰",
    "east asian wonders": "东亚奇观",
    "north pacific attire": "北太平洋服饰",
    "holy buildings": "圣殿建筑",
    "high medieval warfare": "中古战阵服饰",
    "high medieval warfare attire": "中古战阵服饰",
    "arctic attire": "极地服饰",
    "medieval monuments": "中世纪遗迹",
    "west slavic attire": "西斯拉夫服饰",
    "symbols of authority": "权力象征",
    "crowns of the world": "天下王冠",
    "couture of the capets": "卡佩华服",
    "elegance of the empire": "帝国风雅",
    "fashion of the abbasid court": "阿拔斯宫廷风尚",
    "garments of the holy roman empire": "神圣罗马帝国服饰",
    "north african attire": "北非服饰",
    "songs of the realm": "王国之歌",
    "many roads to power - comic book": "权力之路 · 漫画书",
    "subscription": "订阅服务",
    "chapter v": "第五章",
    "silk & silver": "丝绸与白银",

    # ======================= 钢铁雄心 IV =======================
    "la resistance": "抵抗运动",
    "no step back": "寸步不让",
    "by blood alone": "唯有鲜血",
    "arms against tyranny": "以武抗暴",
    "historical german portraits": "德国历史肖像",
    "poland: united and ready": "波兰：团结与准备",
    "anniversary pack": "周年纪念包",
    "battle for the bosporus": "博斯普鲁斯之战",
    "german march order music pack": "德国进行曲音乐包",
    "allied radio music pack": "盟军电台音乐包",
    "radio pack": "电台包",
    "allied speeches pack": "盟军演讲包",
    "songs of the eastern front": "东线之歌",
    "no step back preorder bonus": "寸步不让 · 预购奖励",
    "rocket launcher unit pack": "火箭发射器单位包",
    "soviet tanks unit pack": "苏联坦克单位包",
    "german tanks unit pack": "德国坦克单位包",
    "french tanks unit pack": "法国坦克单位包",
    "british tanks unit pack": "英国坦克单位包",
    "us tanks unit pack": "美国坦克单位包",
    "axis armor pack": "轴心国装甲包",
    "allied armor pack": "盟军装甲包",
    "eastern front planes pack": "东线战机包",
    "la resistance preorder bonus": "抵抗运动 · 预购奖励",
    "trial of allegiance": "效忠试炼",
    "gotterdammerung": "诸神黄昏",
    "graveyard of empires": "帝国坟场",
    "peace for our time": "我们时代的和平",
    "no compromise, no surrender": "不妥协，不投降",
    "thunder at our gates": "兵临城下",
    "sabaton soundtrack": "Sabaton 原声带",
    "sabaton soundtrack vol. 2": "Sabaton 原声带 第二辑",

    # ======================= 维多利亚 3 =======================
    "iberian twilight": "伊比利亚暮色",
    "national awakening": "民族觉醒",
    "pivot of empire": "帝国枢轴",
    "voice of the people": "人民之声",
    "melodies for the masses": "大众旋律",
    "the great wave": "大浪潮",
    "dawn of wonder": "奇迹黎明",
    "charters of commerce": "商业特许状",
    "sphere of influence": "势力范围",
    "colossus of the south": "南方巨像",
    "victoria 2 remastered soundtrack": "维多利亚 2 重制原声带",
    "trains": "火车",
    "songs of the homeland": "祖国之歌",
    "trade ships": "贸易船只",
    "volume 3": "原声带 第三辑",
    "warships": "战舰",
    "state and revolution": "国家与革命",
    "century of strife": "动荡世纪",

    # ======================= 帝国：罗马 =======================
    # 注意：这几个原名是 "Imperator: Rome - Xxx Content Pack"，
    # 剥掉游戏名前缀后 "content pack" 还留在末尾（前面没有破折号，后缀规则不剥），
    # 所以键里要带上它。
    "hellenistic world flavor pack": "希腊化世界风味包",
    "the punic wars content pack": "布匿战争",
    "wallpapers + artbook": "壁纸与美术设定集",
    "complete soundtrack": "完整原声带",
    "magna graecia content pack": "大希腊",
    "heirs of alexander content pack": "亚历山大继承者",
    "epirus content pack": "伊庇鲁斯",

    # ======================= 城市：天际线 =======================
    "content creator pack: africa in miniature": "内容创作者包：微缩非洲",
    "jadia radio": "JADIA 电台",
    "content creator pack: sports venues": "内容创作者包：体育场馆",
    "content creator pack: shopping malls": "内容创作者包：购物中心",
    "80's movies tunes": "80 年代电影曲调",
    "content creator pack: brooklyn & queens": "内容创作者包：布鲁克林与皇后区",
    "content creator pack: railroads of japan": "内容创作者包：日本铁路",
    "content creator pack: industrial evolution": "内容创作者包：工业演进",
    "90's pop radio": "90 年代流行电台",
    "piano tunes radio": "钢琴曲调电台",
    "hotels & retreats": "酒店与度假村",
    "expansion subscription": "扩展内容订阅",
    "content creator pack: mountain village": "内容创作者包：山间村落",
    "content creator pack: map pack 3": "内容创作者包：地图包 3",
    "alpine tunes radio": "阿尔卑斯曲调电台",
    "content creator pack: countryside": "内容创作者包：乡村",
    "content creator pack: emerging downtown": "内容创作者包：新兴市中心",
    "harvest harmony": "丰收和韵",
    "content creator pack: shops of shibuya": "内容创作者包：涩谷商店",
    "content creator pack: map pack 4": "内容创作者包：地图包 4",
    "harumi nights fm": "晴海之夜 FM",
    "race day": "比赛日",
    "content creator pack: renewed history": "内容创作者包：焕新历史",
    "content creator pack: iconic brutalism": "内容创作者包：标志性粗野主义",
    "8 gear radio": "8 挡电台",
    "content creator pack: holiday getaways": "内容创作者包：假日出游",
    "content creator pack: mansions & manors": "内容创作者包：豪宅与庄园",
    "blueprint beats": "蓝图节拍",

    # ======================= 欧陆风云 IV =======================
    "conquest of paradise": "天堂征服",
    "wealth of nations": "国家的财富",
    "res publica": "共和国",
    "el dorado": "黄金国",
    "the cossacks": "哥萨克",
    "mare nostrum": "我们的海",
    "mandate of heaven": "天命",
    "cradle of civilization": "文明摇篮",
    "dharma": "达摩",
    "emperor": "皇帝",
    "national monuments": "国家纪念碑",
    "national monuments ii": "国家纪念碑 II",
    "muslim advisor portraits": "穆斯林顾问肖像",
    "forts pack": "要塞包",
    "muslim female advisor portraits": "穆斯林女顾问肖像",
    "winged hussars unit pack": "翼骑兵单位包",
    "conquistadors unit pack": "征服者单位包",
    "native americans unit pack": "北美原住民单位包",
    "native americans ii unit pack": "北美原住民单位包 II",
    "colonial british and french unit pack": "殖民地英法单位包",
    "muslim ships unit pack": "穆斯林船只单位包",
    "trade nations unit pack": "贸易国家单位包",
    "indian subcontinent unit pack": "印度次大陆单位包",
    "indian ships unit pack": "印度船只单位包",
    "evangelical union unit pack": "福音派联盟单位包",
    "catholic league unit pack": "天主教联盟单位包",
    "free cities unit pack": "自由市单位包",
    "catholic majors unit pack": "天主教主要国家单位包",
    "evangelical majors unit pack": "福音派主要国家单位包",
    "north african unit pack": "北非单位包",
    "west african unit pack": "西非单位包",
    "asian ships": "亚洲船只",
    "third rome": "第三罗马",
    "imperator unit pack": "帝国单位包",
    "songs of the new world": "新世界之歌",
    "songs of yuletide": "圣诞之歌",
    "republican music": "共和音乐",
    "songs of war": "战争之歌",
    "guns, drums and steel": "枪炮、鼓与钢铁",
    "songs of exploration": "探索之歌",
    "guns, drums and steel volume 2 music pack": "枪炮、鼓与钢铁 第二辑",
    "kairi's soundtrack": "Kairi 原声带",
    "kairis soundtrack part 2": "Kairi 原声带 第二部",
    "songs of regency": "摄政之歌",
    "fredman's epistles": "弗雷德曼书信",
    "the rus awaken": "罗斯觉醒",
    "rule britannia": "不列颠统治",
    "dharma music pack": "达摩音乐包",
    "golden century": "黄金世纪",
    "emperor music pack": "皇帝音乐包",
    "leviathan": "利维坦",
    "guns, drums and steel volume 3 music pack": "枪炮、鼓与钢铁 第三辑",
    "10th anniversary community music pack": "十周年社区音乐包",
    "origins": "起源",
    "lions of the north": "北方雄狮",
    "domination": "支配",
    "china history lessons": "中国历史课程",
    "japan history lessons": "日本历史课程",
    "king of kings": "万王之王",
    "sounds from the community - kairi soundtrack part iii": "社区之声：Kairi 原声带 第三部",
    "utopia holy roman empire music pack": "乌托邦神圣罗马帝国音乐包",
    "ultimate sprite pack": "终极图像包",
    "winds of change": "变革之风",
    "ultimate music pack": "终极音乐包",
    "ultimate e-book pack": "终极电子书包",

    # ======================= 十字军之王 II =======================
    "sword of islam": "伊斯兰之剑",
    "legacy of rome": "罗马遗产",
    "sunset invasion": "日落入侵",
    "the republic": "共和国",
    "the old gods": "旧神",
    "sons of abraham": "亚伯拉罕之子",
    "rajas of india": "印度王公",
    "charlemagne": "查理曼",
    "way of life": "生活方式",
    "horse lords": "马王",
    "conclave": "枢机会议",
    "the reaper's due": "死神索命",
    "jade dragon": "玉龙",
    "mongol faces": "蒙古人脸",
    "african portraits": "非洲肖像",
    "mediterranean portraits": "地中海肖像",
    "russian portraits": "俄罗斯肖像",
    "norse portraits": "北欧肖像",
    "celtic portraits": "凯尔特肖像",
    "turkish portraits": "土耳其肖像",
    "persian portraits": "波斯肖像",
    "early western clothing pack": "早期西方服饰包",
    "early eastern clothing pack": "早期东方服饰包",
    "iberian portraits": "伊比利亚肖像",
    "south indian portraits": "南印度肖像",
    "south indian portraits 5 year anniversary gift": "南印度肖像 · 五周年赠礼",
    "dynasty shields": "王朝纹章",
    "dynasty shield ii": "王朝纹章 II",
    "dynasty shield iii": "王朝纹章 III",
    "dynasty shields charlemagne": "王朝纹章 · 查理曼",
    "dynasty shield pack": "王朝纹章包",
    "african unit pack": "非洲单位包",
    "byzantine unit pack": "拜占庭单位包",
    "russian unit pack": "俄罗斯单位包",
    "norse unit pack": "北欧单位包",
    "celtic unit pack": "凯尔特单位包",
    "military orders unit pack": "军事修会单位包",
    "warriors of faith unit pack": "信仰战士单位包",
    "saxon unit pack": "撒克逊单位包",
    "finno-ugric unit pack": "芬兰-乌戈尔单位包",
    "turkish unit pack": "土耳其单位包",
    "persian unit pack": "波斯单位包",
    "iberian unit pack": "伊比利亚单位包",
    "monks and mystics": "修士与神秘主义",
    "songs of albion": "阿尔比恩之歌",
    "songs of faith": "信仰之歌",
    "songs of the holy land": "圣地之歌",
    "songs of the caliph": "哈里发之歌",
    "songs of byzantium": "拜占庭之歌",
    "songs of the rus": "罗斯之歌",
    "songs of prosperity": "繁荣之歌",
    "hymns to the old gods": "旧神赞美诗",
    "hymns of abraham": "亚伯拉罕赞美诗",
    "songs of india": "印度之歌",
    "orchestral house lords": "管弦马王",
    "viking metal": "维京金属",
    "full plate metal": "全身板甲金属",
    "hymns of revelation": "启示赞美诗",
    "pagan fury": "异教之怒",
    "pagan fury - warrior queen": "异教之怒 · 女武神",
    "ruler designer": "统治者设计器",
    "europa universalis iv converter": "欧陆风云 IV 存档转换器",
    "customization pack": "自定义包",
    "holy fury": "神圣之怒",
    "the song of roland": "罗兰之歌",
    "tales of treachery": "背叛故事",
    "ultimate portrait pack": "终极肖像包",
    "ultimate unit pack": "终极单位包",

    # ======================= 群星 =======================
    "ancient relics story pack": "远古遗迹故事包",
    "lithoids species pack": "岩石物种包",
    "federations": "联邦",
    "necroids species pack": "死灵物种包",
    "nemesis": "复仇女神",
    "aquatics species pack": "水生物种包",
    "overlord": "霸主",
    "toxoids species pack": "毒素物种包",
    "first contact story pack": "首次接触故事包",
    "galactic paragons": "银河典范",
    "astral planes": "星界位面",
    "the machine age": "机器时代",
    "cosmic storms": "宇宙风暴",
    "grand archive story pack": "大档案馆故事包",
    "rick the cube species portrait": "Rick 方块物种肖像",
    "infernals species pack": "炽炎物种包",
    "biogenesis": "生物创世",
    "shadows of the shroud": "帷幕之影",
    "stargazer species portrait": "观星者物种肖像",
    "season 10": "第十赛季",
    "vipra the vapor species portrait": "蝰蛇气体物种肖像",
    "nomads": "游牧者",
    "willpower": "意志",
    "scenario pack 1": "情景包 1",
    "scenario pack 2": "情景包 2",
    "plantoids species pack": "植物物种包",
    "leviathans story pack": "利维坦故事包",
    "anniversary portraits": "周年肖像",
    "apocalypse": "天启",
    "distant stars story pack": "遥远群星故事包",
    "megacorp": "巨型企业",
    "utopia": "乌托邦",
    "synthetic dawn story pack": "合成黎明故事包",
    "humanoids species pack": "类人物种包",

    # ======================= 欧陆风云 V =======================
    "premium upgrade": "高级升级包",
    "across the pillars": "越过赫拉克勒斯之柱",
    "fate of the phoenix": "凤凰的命运",
    "sacred sites": "圣地",

    # ======================= 城市：天际线 II =======================
    "beach properties": "海滨地产",
    "bridges & ports": "桥梁与港口",
    "san francisco set": "旧金山套装",
    "modern architecture": "现代建筑",
    "urban promenades": "城市步行街",
    "soft rock radio": "轻摇滚电台",
    "deluxe relax station": "豪华休闲电台",
    "japanese castle": "日本城堡",
    "mediterranean heritage": "地中海遗产",
    "leisure venues": "休闲场所",
    "dragon gate": "龙门",
    "skyscrapers": "摩天大楼",
    "mass transit": "大众运输",
    "industries": "工业",

    # ======================= 通用 =======================
    "expansion subscription": "扩展内容订阅",
}


def lookup_user(name: str) -> str:
    """只查玩家本地覆盖表（优先级最高）；没有返回空串。"""
    key = normalize(name)
    if not key:
        return ""
    alt = (key.replace(" and ", " & ").replace("&", " and ")
              .replace("  ", " ").strip())
    user = load_user_overrides()
    for k in (key, alt):
        if k in user:
            return user[k]
    return ""


def lookup(name: str) -> str:
    """按英文名查中文名；查不到返回空串。

    优先级：玩家本地覆盖表 > 内置表。
    """
    hit = lookup_user(name)
    if hit:
        return hit
    key = normalize(name)
    if not key:
        return ""
    # 二次尝试：& 与 and 互换（"Wards & Wardens" / "Wards and Wardens"）
    alt = (key.replace(" and ", " & ").replace("&", " and ")
              .replace("  ", " ").strip())
    for k in (key, alt):
        h = DLC_CN_BY_NAME.get(k)
        if h:
            return h
    return ""


# ---------------------------------------------------------------- 玩家本地覆盖
# 程序目录下的 dlc-names-cn.json —— 玩家自己加/改 DLC 中文名用。
# 由 ck3core 在启动时把绝对路径注入 USER_PATH（这里不 import ck3core，避免循环依赖）。
USER_PATH = ""

_user_cache: dict = {"mtime": None, "map": {}}

TEMPLATE = {
    "_说明": ("DLC 中文名的本地覆盖表。格式是 \"英文原名\": \"中文名\"，"
            "英文名大小写不限、前面的 Expansion - / 音乐 - 之类的词可以省略。"
            "改完保存，回启动器点 DLC 页右上角「重新校验」即可生效。"),
    "_示例（可以删掉）": "",
    "the great wave": "大浪潮",
    "Gotterdammerung": "诸神黄昏",
}


def load_user_overrides() -> dict:
    """读本地覆盖表。文件改了会自动重新加载（按修改时间判断），不用重启。"""
    if not USER_PATH or not os.path.isfile(USER_PATH):
        return {}
    try:
        mtime = os.path.getmtime(USER_PATH)
    except Exception:
        return {}
    if _user_cache["mtime"] == mtime:
        return _user_cache["map"]

    data: dict = {}
    try:
        with open(USER_PATH, "r", encoding="utf-8-sig") as f:
            raw = json.load(f)
        if isinstance(raw, dict):
            for k, v in raw.items():
                ks = str(k or "").strip()
                # 下划线开头的键是说明/示例，跳过
                if not ks or ks.startswith("_") or ks.startswith("#"):
                    continue
                nk = normalize(ks)
                if nk and isinstance(v, str) and v.strip():
                    data[nk] = v.strip()
    except Exception:
        data = {}

    _user_cache["mtime"] = mtime
    _user_cache["map"] = data
    return data


def ensure_user_file(path: str) -> tuple[bool, str]:
    """确保覆盖表文件存在（不存在就写一份带示例的模板）。返回 (ok, 错误信息)。"""
    try:
        if os.path.isfile(path):
            return True, ""
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(TEMPLATE, f, ensure_ascii=False, indent=2)
        return True, ""
    except Exception as ex:
        return False, str(ex)
