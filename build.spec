# -*- mode: python ; coding: utf-8 -*-
"""PyInstaller 打包配置 —— 生成单文件 人联启动器.exe"""

import os

block_cipher = None

a = Analysis(
    ['launcher.py'],
    pathex=['backend'],            # 关键：否则静态分析找不到 backend 下的模块
    binaries=[],
    datas=[('static', 'static')],
    hiddenimports=[
        'server',                  # backend/server.py
        'ck3core',                 # backend/ck3core.py
        'diagnose',                # backend/diagnose.py（崩溃诊断收集）
        'steamdlc',                # backend/steamdlc.py（向 Steam 查 DLC 授权）
        'steambeta',               # backend/steambeta.py（Steam 版本分支读写）
        'wiki',                    # backend/wiki.py（百科：兵种/文化/传统，读本体脚本+本地化）
        'dlc_meta',                # backend/dlc_meta.py（DLC 发布日期/大小）
        'dlc_cn',                  # backend/dlc_cn.py（DLC 中文名对照，全游戏离线）
        'windlg',                  # backend/windlg.py（原生文件对话框）
        'achievements',            # backend/achievements.py（Steam 成就解析）
        'webview.platforms.edgechromium',
        'webview.platforms.winforms',
        'clr_loader',
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[
        # 环境里装了不少用不到的大包，排掉避免 exe 膨胀
        # 注意：不能排除 distutils / setuptools / unittest 等，会和 PyInstaller 自身 hook 冲突
        # 注意：PIL/Pillow 现在**不能排除** —— 成就图标要把游戏的 DDS 纹理转成 PNG
        'numpy', 'scipy', 'pymeshlab', 'trimesh',
        'pandas', 'matplotlib', 'IPython', 'jupyter', 'notebook',
        'requests', 'urllib3', 'certifi', 'charset_normalizer', 'idna',
        'zstandard',
    ],
    win_no_prefer_redirects=False,
    win_private_assemblies=False,
    cipher=block_cipher,
    noarchive=False,
)

pyz = PYZ(a.pure, a.zipped_data, cipher=block_cipher)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.zipfiles,
    a.datas,
    [],
    name='人联启动器',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=False,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,          # 无控制台窗口
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon='static/icon.ico',
)
