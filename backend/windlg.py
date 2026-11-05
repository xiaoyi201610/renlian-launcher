# -*- coding: utf-8 -*-
"""
Windows 原生文件对话框
======================
直接调 comdlg32 / shell32，不依赖 tkinter。

原因：tkinter 的 Tk() 必须在主线程创建，而这里的调用来自 HTTP 请求线程，
会静默失败并返回空字符串 —— 表现就是"点了保存却说已取消"。

用 Win32 API 就没有线程限制，也不会有 Tk 窗口闪烁。
"""

from __future__ import annotations

import os
import ctypes
from ctypes import wintypes

_user32 = ctypes.windll.user32
_comdlg32 = ctypes.windll.comdlg32
_shell32 = ctypes.windll.shell32

# 打开/保存对话框 flags
OFN_READONLY = 0x00000001
OFN_OVERWRITEPROMPT = 0x00000002
OFN_HIDEREADONLY = 0x00000004
OFN_NOCHANGEDIR = 0x00000008
OFN_PATHMUSTEXIST = 0x00000800
OFN_FILEMUSTEXIST = 0x00001000
OFN_EXPLORER = 0x00080000

# 浏览文件夹 flags
BIF_RETURNONLYFSDIRS = 0x0001
BIF_NEWDIALOGSTYLE = 0x0040
BIF_EDITBOX = 0x0010

# SHBrowseForFolder 回调消息（用于把初始目录定位到 current folder）
BFFM_INITIALIZED = 1
BFFM_SETSELECTIONW = 0x400 + 103   # WM_USER + 103


class OPENFILENAMEW(ctypes.Structure):
    """注意：不包含末尾的 FlagsEx 字段。

    FlagsEx 是较新 SDK 才加的，把它算进 lStructSize 会让部分系统判定尺寸不符，
    GetSaveFileName 直接返回 0 并报 CDERR_INITIALIZATION(0x1)。
    系统只读它认识的前 N 个字节，所以少给字段是安全且兼容性最好的做法。
    """
    _fields_ = [
        ("lStructSize", wintypes.DWORD),
        ("hwndOwner", wintypes.HWND),
        ("hInstance", wintypes.HINSTANCE),
        ("lpstrFilter", wintypes.LPCWSTR),
        ("lpstrCustomFilter", wintypes.LPWSTR),
        ("nMaxCustFilter", wintypes.DWORD),
        ("nFilterIndex", wintypes.DWORD),
        ("lpstrFile", wintypes.LPWSTR),
        ("nMaxFile", wintypes.DWORD),
        ("lpstrFileTitle", wintypes.LPWSTR),
        ("nMaxFileTitle", wintypes.DWORD),
        ("lpstrInitialDir", wintypes.LPCWSTR),
        ("lpstrTitle", wintypes.LPCWSTR),
        ("Flags", wintypes.DWORD),
        ("nFileOffset", wintypes.WORD),
        ("nFileExtension", wintypes.WORD),
        ("lpstrDefExt", wintypes.LPCWSTR),
        ("lCustData", wintypes.LPARAM),
        ("lpfnHook", ctypes.c_void_p),
        ("lpTemplateName", wintypes.LPCWSTR),
        ("pvReserved", ctypes.c_void_p),
        ("dwReserved", wintypes.DWORD),
        ("dwReserved2", wintypes.DWORD),
    ]


class BROWSEINFOW(ctypes.Structure):
    _fields_ = [
        ("hwndOwner", wintypes.HWND),
        ("pidlRoot", ctypes.c_void_p),
        ("pszDisplayName", wintypes.LPWSTR),
        ("lpszTitle", wintypes.LPCWSTR),
        ("ulFlags", wintypes.UINT),
        ("lpfn", ctypes.c_void_p),
        ("lParam", wintypes.LPARAM),
        ("iImage", ctypes.c_int),
    ]


def _owner() -> int:
    """只找**属于当前进程**的可见窗口。

    不能用标题找窗口 —— 用户可能同时开着另一个启动器实例，
    那样对话框会挂到别的进程的窗口上，结果就是"点了没反应"。
    """
    try:
        pid = os.getpid()
        u = _user32
        hits = []

        def cb(h, _l):
            if not u.IsWindowVisible(h):
                return True
            p = wintypes.DWORD()
            u.GetWindowThreadProcessId(h, ctypes.byref(p))
            if p.value == pid and u.GetWindowTextLengthW(h) > 0:
                hits.append(h)
            return True

        u.EnumWindows(ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)(cb), 0)
        if hits:
            return hits[0]
    except Exception:
        pass
    return 0


def _with_com(fn):
    """对话框走 COM，先按 STA 初始化；没有窗口的线程也能正常显示。"""
    inited = False
    try:
        hr = ctypes.windll.ole32.CoInitializeEx(None, 0x2)  # COINIT_APARTMENTTHREADED
        inited = hr in (0, 1)   # S_OK / S_FALSE
    except Exception:
        pass
    try:
        return fn()
    finally:
        if inited:
            try:
                ctypes.windll.ole32.CoUninitialize()
            except Exception:
                pass


def _filter_buffer(pairs) -> ctypes.Array:
    """把 [('名称','*.zip'), ...] 变成 comdlg 需要的双 null 结尾宽字符内存块。

    用 UTF-16LE 原始字节构造，最稳（不依赖 ctypes 的字符串终止约定）。
    """
    s = ""
    for name, pat in pairs:
        s += name + "\0" + pat + "\0"
    s += "\0"
    raw = s.encode("utf-16-le")
    return ctypes.create_string_buffer(raw, len(raw))


def _initial_dir(path: str = "") -> str:
    if path and os.path.isdir(path):
        return path
    desktop = os.path.join(os.path.expanduser("~"), "Desktop")
    return desktop if os.path.isdir(desktop) else os.path.expanduser("~")


def _nearest_dir(path: str = "") -> str:
    """返回 path（或它最近的、确实存在的上级目录）；都不存在则返回 ""。

    用于「配置里的目录还没建」时，也让文件夹选择框打开到最接近的位置，
    而不是永远从「此电脑/桌面」开始。
    """
    p = (path or "").strip().strip('"').rstrip("\\/")
    while p:
        if os.path.isdir(p):
            return p
        parent = os.path.dirname(p)
        if parent == p:
            break
        p = parent
    return ""


def _base_ofn(title: str, default_name: str, filters, initial_dir: str, def_ext: str):
    buf = ctypes.create_unicode_buffer(2048)
    full = default_name
    if initial_dir and not os.path.isabs(full):
        full = os.path.join(initial_dir, default_name) if default_name else ""
    buf.value = full

    # 这些缓冲区必须一直被引用，否则被 GC 回收后指针悬空，
    # GetSaveFileName 会直接返回 0 并报 CDERR_INITIALIZATION(0x1)
    fbuf = _filter_buffer(filters)

    ofn = OPENFILENAMEW()
    ofn.lStructSize = ctypes.sizeof(OPENFILENAMEW)
    ofn.hwndOwner = _owner()
    ofn.lpstrFilter = ctypes.cast(fbuf, wintypes.LPCWSTR)
    ofn.lpstrFile = ctypes.cast(buf, wintypes.LPWSTR)
    ofn.nMaxFile = 2048
    ofn.lpstrTitle = title
    ofn.lpstrInitialDir = initial_dir
    ofn.lpstrDefExt = def_ext or None
    ofn._keepalive = (fbuf, buf)   # 防止被回收
    return ofn, buf


ERR_PREFIX = "__ERR__"


def _check_error() -> str:
    """GetSaveFileName 返回 0 时，用它区分「用户取消」和「真的出错」。"""
    try:
        code = _comdlg32.CommDlgExtendedError()
        if code:
            return "%s0x%X" % (ERR_PREFIX, code)
    except Exception:
        pass
    return ""


def save_file(title: str, default_name: str, filters, def_ext: str = "",
              initial_dir: str = "") -> str:
    """弹出原生「另存为」。返回路径；用户取消返回 ""；出错返回 ERR_PREFIX+代码。"""
    def run():
        try:
            init = _initial_dir(initial_dir)
            ofn, buf = _base_ofn(title, default_name, filters, init, def_ext)
            ofn.Flags = (OFN_EXPLORER | OFN_OVERWRITEPROMPT
                         | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR)
            if _comdlg32.GetSaveFileNameW(ctypes.byref(ofn)):
                return buf.value
            return _check_error()
        except Exception as ex:
            return "%sexc:%s" % (ERR_PREFIX, ex)
    return _with_com(run)


def open_file(title: str, filters, initial_dir: str = "") -> str:
    """弹出原生「打开」。返回路径；取消返回 ""；出错返回 ERR_PREFIX+代码。"""
    def run():
        try:
            init = _initial_dir(initial_dir)
            ofn, buf = _base_ofn(title, "", filters, init, "")
            ofn.Flags = (OFN_EXPLORER | OFN_FILEMUSTEXIST
                         | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR)
            if _comdlg32.GetOpenFileNameW(ctypes.byref(ofn)):
                return buf.value
            return _check_error()
        except Exception as ex:
            return "%sexc:%s" % (ERR_PREFIX, ex)
    return _with_com(run)


def pick_folder(title: str = "选择文件夹", initial_dir: str = "") -> str:
    """弹出原生文件夹选择框，尽量定位到 initial_dir（或其最近的已存在上级）。

    用 SHBrowseForFolderW + BFFM_INITIALIZED 回调把初始目录设过去；
    否则系统总是从「此电脑 / 桌面」打开（用户会觉得「为什么不定位当前文件夹」）。
    """
    start = _nearest_dir(initial_dir) or _initial_dir("")

    def run():
        try:
            display = ctypes.create_unicode_buffer(1024)
            init_buf = ctypes.create_unicode_buffer(start) if start else None

            CALLBACK = ctypes.WINFUNCTYPE(
                ctypes.c_int, wintypes.HWND, wintypes.UINT,
                wintypes.LPARAM, wintypes.LPARAM)

            def _cb(hwnd, msg, lparam, lpdata):
                if msg == BFFM_INITIALIZED and lpdata:
                    try:
                        _user32.SendMessageW(hwnd, BFFM_SETSELECTIONW, 1, lpdata)
                    except Exception:
                        pass
                return 0
            cb = CALLBACK(_cb)

            bi = BROWSEINFOW()
            bi.hwndOwner = _owner()
            bi.pidlRoot = None
            bi.pszDisplayName = ctypes.cast(display, wintypes.LPWSTR)
            bi.lpszTitle = title
            bi.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE | BIF_EDITBOX
            bi.lpfn = ctypes.cast(cb, ctypes.c_void_p)
            bi.lParam = (ctypes.addressof(init_buf) if init_buf else 0)
            bi.iImage = 0
            pidl = _shell32.SHBrowseForFolderW(ctypes.byref(bi))
            if not pidl:
                return ""
            path = ctypes.create_unicode_buffer(2048)
            ok = _shell32.SHGetPathFromIDListW(pidl, path)
            try:
                ctypes.windll.ole32.CoTaskMemFree(pidl)
            except Exception:
                pass
            return path.value if ok else ""
        except Exception:
            return ""
    return _with_com(run)
