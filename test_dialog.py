# -*- coding: utf-8 -*-
"""验证「另存为」对话框真的能弹出（之前 tkinter 在请求线程里不弹窗）"""

import ctypes
import json
import os
import sys
import threading
import time
import urllib.request
from ctypes import wintypes
from PIL import ImageGrab

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "backend"))

import server as srv  # noqa: E402
import diagnose      # noqa: E402
import ck3core       # noqa: E402

found = []
result = {}


def enum_windows(keywords):
    out = []
    u = ctypes.windll.user32

    def cb(h, l):
        n = u.GetWindowTextLengthW(h)
        if n > 0 and u.IsWindowVisible(h):
            b = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(h, b, n + 1)
            if any(k in b.value for k in keywords):
                r = wintypes.RECT()
                u.GetWindowRect(h, ctypes.byref(r))
                out.append((h, b.value, r))
        return True

    u.EnumWindows(ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)(cb), 0)
    return out


def main():
    httpd, port = srv.serve(0)
    base = "http://127.0.0.1:%d" % port

    # 先收集一份诊断包（很快）
    r = diagnose.collect(ck3core.load_config(), include_dxdiag=False)
    if not r.get("ok"):
        print("✗ 收集失败", r)
        return 1
    print("已生成诊断包:", r["name"])

    def call():
        req = urllib.request.Request(
            base + "/api/diag/save-as",
            data=json.dumps({"zip": r["zip"]}).encode(),
            headers={"Content-Type": "application/json"},
        )
        try:
            result["resp"] = json.loads(urllib.request.urlopen(req, timeout=120).read())
        except Exception as ex:
            result["err"] = repr(ex)

    t = threading.Thread(target=call, daemon=True)
    t.start()

    # 等对话框出现
    dlg = None
    for i in range(6):
        time.sleep(0.5)
        wins = enum_windows(["另存为", "保存诊断包", "Save As", "保存"])
        if wins:
            dlg = wins[0]
            break
        if result.get("resp") or result.get("err"):
            break

    if not dlg:
        print("✗ 没出现保存对话框")
        print("  接口返回:", result.get("resp") or result.get("err") or "(仍在阻塞，说明对话框可能在等输入)")
        print("  --- 当前所有可见窗口 ---")
        u = ctypes.windll.user32

        def cb(h, l):
            n = u.GetWindowTextLengthW(h)
            if n > 0 and u.IsWindowVisible(h):
                b = ctypes.create_unicode_buffer(n + 1)
                u.GetWindowTextW(h, b, n + 1)
                if b.value.strip():
                    print("   ", repr(b.value))
            return True

        u.EnumWindows(ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)(cb), 0)
        return 1

    h, title, rect = dlg
    print("✓ 对话框已弹出：标题=%r  位置=(%d,%d)  尺寸=%dx%d" % (
        title, rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top))

    time.sleep(0.6)
    try:
        ImageGrab.grab(bbox=(rect.left, rect.top, rect.right, rect.bottom)).save(
            os.path.join(HERE, "dialog-shot-real.png"))
        print("✓ 已截图 dialog-shot-real.png")
    except Exception as ex:
        print("截图失败:", ex)

    # 关掉对话框（等价于用户点取消）
    ctypes.windll.user32.PostMessageW(h, 0x0010, 0, 0)   # WM_CLOSE
    t.join(timeout=15)
    print("接口返回:", result.get("resp") or result.get("err"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
