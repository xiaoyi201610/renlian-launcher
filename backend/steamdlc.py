"""向 Steam 客户端查询「这个 DLC 到底有没有」。

为什么必须走这条路：
    DLC 拥有状态只存在于 Steam 的授权数据里。磁盘上任何东西都不可靠 ——
    - launcher-v2.sqlite 的 playsets_dlcs：官方启动器把**扫描到的所有** DLC 都写进去，
      没买的也在（30/30 全有记录）
    - 注册表 Valve\\Steam\\Apps\\<id>：全部 Installed=1，没买的也是
    - appmanifest 的 InstalledDepots：CK3 的所有 DLC depot 都随主游戏挂载
    - DLC 目录里的文件多少：CK3 有的 DLC 内容合并进主包（只有空壳），
      有的独立成 depot，跟拥有与否无关
    - 游戏 debug.log 的 DLC 段：只是注册列表，不区分拥有

    唯一准确的是 Steam 自己的 SteamApps()->BIsSubscribedApp()，
    它和 Steam 库页面、游戏内 DLC 锁定用的是同一份授权数据。

用法：
    r = query([1296730, 1296731], game_root)
    if r["ok"]:
        r["owned"][1296730]  → True / False
    else:
        r["msg"]             → 失败原因（Steam 没开等），此时调用方应降级为「全部可用」
"""

import ctypes
import os
import tempfile
import threading

APP_ID = 1158310          # Crusader Kings III
_lock = threading.Lock()
_cache = {}               # {game_appid: result}，按游戏分开缓存


def _find_dll(game_root: str) -> str:
    cands = [
        os.path.join(game_root, "binaries", "steam_api64.dll"),
        os.path.join(game_root, "steam_api64.dll"),
        os.path.join(game_root, "game", "steam_api64.dll"),
    ]
    for p in cands:
        if os.path.isfile(p):
            return p
    return ""


def reset_cache() -> None:
    """强制下次重新查询（用户点了「重新校验」时用）。"""
    global _cache
    with _lock:
        _cache = {}


def query(steam_ids, game_root: str, game_appid: str | int = 1158310) -> dict:
    """返回 {"ok": bool, "msg": str, "owned": {appid: bool}}。结果按游戏缓存。

    game_appid：主游戏的 Steam AppID（决定 SteamAPI 以哪个游戏身份查询 DLC）。
    """
    global _cache
    game_appid = int(game_appid)
    with _lock:
        if game_appid in _cache:
            return _cache[game_appid]

        res = {"ok": False, "msg": "", "owned": {}}
        ids = []
        for s in steam_ids or []:
            try:
                ids.append(int(s))
            except Exception:
                pass
        if not ids:
            res["msg"] = "没有可查询的 DLC"
            _cache[game_appid] = res
            return res

        dll = _find_dll(game_root or "")
        if not dll:
            res["msg"] = "没找到 steam_api64.dll（游戏目录不完整？）"
            _cache[game_appid] = res
            return res

        old_cwd = os.getcwd()
        tmp_dir = ""
        api = None
        inited = False
        try:
            # steam_appid.txt 必须放在当前工作目录，所以临时切过去
            tmp_dir = tempfile.mkdtemp(prefix="ck3_steam_")
            with open(os.path.join(tmp_dir, "steam_appid.txt"), "w") as f:
                f.write(str(game_appid))
            os.chdir(tmp_dir)
            os.environ["SteamAppId"] = str(game_appid)
            os.environ["SteamGameId"] = str(game_appid)

            api = ctypes.CDLL(dll)
            api.SteamAPI_Init.restype = ctypes.c_bool
            if not api.SteamAPI_Init():
                res["msg"] = "Steam 没有运行（或未登录），无法校验 DLC 拥有情况"
                _cache[game_appid] = res
                return res
            inited = True

            # 获取 SteamApps 接口指针，兼容新旧两种 Steam SDK：
            #  新版（CK3/EU4/VIC3）：SteamAPI_SteamApps_v008() 直接返回接口
            #  旧版（HOI4/CK2/Imperator）：只有 SteamClient() + GetISteamApps 扁平函数
            apps = None
            try:
                api.SteamAPI_SteamApps_v008.restype = ctypes.c_void_p
                apps = api.SteamAPI_SteamApps_v008()
            except Exception:
                apps = None
            if not apps:
                # 旧版 SDK 兜底：SteamClient() → GetISteamApps
                try:
                    api.SteamClient.restype = ctypes.c_void_p
                    client = api.SteamClient()
                    api.SteamAPI_ISteamClient_GetISteamApps.argtypes = [
                        ctypes.c_void_p, ctypes.c_int32, ctypes.c_int32]
                    api.SteamAPI_ISteamClient_GetISteamApps.restype = ctypes.c_void_p
                    h_user = 1
                    h_pipe = 1
                    for sym in ("SteamAPI_GetHSteamUser", "SteamAPI_GetHSteamPipe"):
                        try:
                            f = getattr(api, sym)
                            f.restype = ctypes.c_int32
                            v = f()
                            if "User" in sym:
                                h_user = v
                            else:
                                h_pipe = v
                        except Exception:
                            pass
                    apps = api.SteamAPI_ISteamClient_GetISteamApps(client, h_user, h_pipe)
                except Exception:
                    apps = None
            if not apps:
                res["msg"] = "游戏 Steam SDK 版本过旧，无法获取 DLC 授权接口（无法校验拥有情况）"
                _cache[game_appid] = res
                return res

            fn = api.SteamAPI_ISteamApps_BIsSubscribedApp
            fn.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
            fn.restype = ctypes.c_bool

            for sid in ids:
                try:
                    res["owned"][sid] = bool(fn(apps, sid))
                except Exception:
                    pass
            res["ok"] = bool(res["owned"])
            if not res["ok"]:
                res["msg"] = "查询没有返回结果"
        except Exception as ex:
            res["msg"] = "调用 Steam 接口失败：%s" % ex
        finally:
            if inited:
                try:
                    api.SteamAPI_Shutdown()
                except Exception:
                    pass
            try:
                os.chdir(old_cwd)
            except Exception:
                pass
            if tmp_dir:
                try:
                    import shutil
                    shutil.rmtree(tmp_dir, ignore_errors=True)
                except Exception:
                    pass

        _cache[game_appid] = res
        return res


def _remote_storage(api, client, hu: int, hp: int):
    """拿 ISteamRemoteStorage 接口指针（老接口，但 Steam 客户端至今仍保留）。

    注意：不能像 SteamApps 那样用 SteamAPI_SteamRemoteStorage_v0xx() 扁平入口——
    在非 Steam 启动的进程里那组入口会直接崩，必须走 SteamClient + GetISteamRemoteStorage。
    """
    fn = api.SteamAPI_ISteamClient_GetISteamRemoteStorage
    fn.argtypes = [ctypes.c_void_p, ctypes.c_int32, ctypes.c_int32, ctypes.c_char_p]
    fn.restype = ctypes.c_void_p
    for ver in (16, 14, 13, 12, 11, 10, 9, 8):
        p = fn(client, hu, hp, ("STEAMREMOTESTORAGE_INTERFACE_VERSION%03d" % ver).encode())
        if p:
            return p
    return None


def subscribe(steam_ids, game_root: str, game_appid: str | int = 1158310) -> dict:
    """向 Steam 发起创意工坊订阅（导入播放集时补齐缺失 mod 用）。

    返回 {"ok": bool, "msg": str, "subscribed": [id...], "failed": [id...]}
    “ok”只代表请求被 Steam 客户端受理；实际下载由 Steam 异步完成。
    """
    ids: list[int] = []
    for s in steam_ids or []:
        try:
            i = int(str(s).strip())
        except Exception:
            continue
        if i > 0 and i not in ids:
            ids.append(i)
    res = {"ok": False, "msg": "", "subscribed": [], "failed": list(ids)}
    if not ids:
        res["msg"] = "没有可订阅的工坊 ID"
        return res

    game_appid = int(game_appid)
    with _lock:
        dll = _find_dll(game_root or "")
        if not dll:
            res["msg"] = "没找到 steam_api64.dll（游戏目录不完整？）"
            return res

        old_cwd = os.getcwd()
        tmp_dir = ""
        api = None
        inited = False
        try:
            # 与 query() 相同：steam_appid.txt 必须放在当前工作目录
            tmp_dir = tempfile.mkdtemp(prefix="ck3_steam_sub_")
            with open(os.path.join(tmp_dir, "steam_appid.txt"), "w") as f:
                f.write(str(game_appid))
            os.chdir(tmp_dir)
            os.environ["SteamAppId"] = str(game_appid)
            os.environ["SteamGameId"] = str(game_appid)

            api = ctypes.CDLL(dll)
            api.SteamAPI_Init.restype = ctypes.c_bool
            if not api.SteamAPI_Init():
                res["msg"] = "Steam 没有运行（或未登录），无法自动订阅"
                return res
            inited = True

            api.SteamClient.restype = ctypes.c_void_p
            client = api.SteamClient()
            if not client:
                res["msg"] = "拿不到 SteamClient 接口，无法自动订阅"
                return res
            try:
                api.SteamAPI_GetHSteamUser.restype = ctypes.c_int32
                api.SteamAPI_GetHSteamPipe.restype = ctypes.c_int32
                hu, hp = api.SteamAPI_GetHSteamUser(), api.SteamAPI_GetHSteamPipe()
            except Exception:
                hu = hp = 1

            rs = _remote_storage(api, client, hu, hp)
            if not rs:
                res["msg"] = "Steam 客户端没有开放 RemoteStorage 接口，无法自动订阅"
                return res

            fn = api.SteamAPI_ISteamRemoteStorage_SubscribePublishedFile
            fn.argtypes = [ctypes.c_void_p, ctypes.c_uint64]
            fn.restype = ctypes.c_uint64

            ok_ids: list[int] = []
            fail_ids: list[int] = []
            for sid in ids:
                try:
                    handle = fn(rs, sid)
                except Exception:
                    handle = 0
                (ok_ids if handle else fail_ids).append(sid)
            res["subscribed"] = ok_ids
            res["failed"] = fail_ids
            res["ok"] = bool(ok_ids)
            if fail_ids:
                res["msg"] = "%d 个订阅请求被 Steam 拒绝" % len(fail_ids)
        except Exception as ex:
            res["msg"] = "调用 Steam 订阅接口失败：%s" % ex
        finally:
            if inited:
                try:
                    api.SteamAPI_Shutdown()
                except Exception:
                    pass
            try:
                os.chdir(old_cwd)
            except Exception:
                pass
            if tmp_dir:
                try:
                    import shutil
                    shutil.rmtree(tmp_dir, ignore_errors=True)
                except Exception:
                    pass
    return res
