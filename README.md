# 人联启动器

十字军之王 3 的第三方启动器。比官方启动器轻，**重点是排序**；除此之外，还有三个真正省事的地方：拖进窗口就能装的 Mod、一键排查播放集冲突、五种界面语言。

---

## 三个亮点

### ① 压缩包拖进窗口就装好（不用 7-Zip）

把 `.zip` / `.7z` / `.rar` 从资源管理器拖到启动器窗口**任意位置**，松手即开始：
自动解压 → 找到包里的 `descriptor.mod` → 装进本地 `mod` 目录 → 生成 `.mod` 描述文件 → 刷新模组列表。

- 支持一次拖多个包，界面逐条显示「上传 % → 解压中 → 已安装 / 失败原因」
- 不需要安装 7-Zip / WinRAR：`.7z` / `.rar` 用 Windows 自带的 `tar.exe` 解
- 重名不覆盖：目标目录已存在时自动加 `_2` / `_3`
- 包里没有 `descriptor.mod`、压缩包加密或损坏，会直接说明原因，不会装出一个空壳
- 超过 400MB 的包会先拦下，提示手动解压后放进 `mod` 目录，避免界面卡死

### ② 播放集冲突排查

「模组管理」右上角点 **冲突排查**，扫描当前播放集里**所有已启用模组**，输出四类结果：

| 类型 | 说明 |
|---|---|
| 文件覆盖 | 同一个相对路径被多个模组包含。按加载顺序，**越靠下越晚加载、最终生效**；面板直接标出「谁赢了、谁被盖住」 |
| 本地化键重复 | 多个模组在 `.yml` 里定义了同一个 key，属于软冲突，表现为文本随机被某一家覆盖 |
| 依赖缺失 | 模组声明了 `dependencies`，但依赖项没有启用 |
| 整目录替换 | 模组用 `replace_path` 替换原版整个目录，风险最高，单独提示 |

结果按 **高 / 中 / 低** 分级（`common/`、`events/`、`history/` 等高影响路径优先），并带逐模组扫描进度，大播放集也不会看起来像卡死。结果缓存 20 秒，改完顺序再点一次即可刷新。

### ③ 五种界面语言

简体中文 / English / Français / Deutsch / Svenska，在「启动器设置 → 界面语言」切换，**即时生效并记住**。除界面文案外，DLC 分类、日期、数量单位等也跟随切换；DLC 名称在中文界面显示中文，其他语言显示 Steam 原名。

---

## 两个版本，二选一

### ① 绿色版（推荐，给别人分享用这个）

到本项目的 **Releases** 页面下载最新的 `人联启动器_vX.Y.Z.zip` → 解压 → 双击 **`人联启动器.exe`**

- 单文件 exe，**不需要装 Python**，不用安装、不写注册表
- 整个文件夹可以随便拷、随便搬，删掉即卸载
- 附带 `使用教程.html`（图文教程）和 `说明.txt`

### ② 源码版（自己改代码时用）

先装依赖 `pip install -r requirements.txt`（需要本机有 Python 3.10+），然后双击 `启动启动器.bat`

改了 `backend/` 或 `static/` 下的代码会立即生效，不用重新打包。

---

## 怎么用

- 想看到报错信息 → 源码版用 `启动器-调试.bat`；绿色版看程序目录下的 `启动日志.log`
- 首次启动会自动探测游戏目录、用户数据目录、创意工坊目录
- 如果探测不准，到「启动器设置」里手动指定

---

## 重新打包 exe

改完代码后要重新生成绿色版，三步：

```bash
# 1. 打包单文件 exe（约 40 秒）
python -m PyInstaller build.spec --noconfirm --distpath dist --workpath build

# 2. 组装绿色目录 + 压缩包
python make_release.py

# 3. 产物在 release/人联启动器_v1.5.zip
```

`build.spec` 里有两个关键点，改动时别删：

- `pathex=['backend']` 与 `hiddenimports=['server','ck3core']` —— 否则 PyInstaller 静态分析找不到 `backend/` 下的模块，打出来的 exe 一启动就闪退
- `excludes` 里不要加 `distutils` / `setuptools` / `unittest`，会和 PyInstaller 自身 hook 冲突报错

打包后的资源路径处理：`ck3core.RES_DIR` 指向 PyInstaller 临时目录（放 `static/`），
`ck3core.APP_DIR` 指向 exe 所在目录（放配置和 `backups/`）。

---

## 功能

| 模块 | 能做什么 |
|---|---|
| **模组管理** | 双栏布局。左栏全部已识别模组，右栏当前播放集。**拖动右栏任意条目调整加载顺序**，也可以从右栏直接拖进左栏条目；支持搜索、按兼容性筛选、一键全开/全关、反转顺序。**把 `.zip/.7z/.rar` 拖进窗口即自动安装**；**一键冲突排查**覆盖文件覆盖、本地化键重复、依赖缺失、`replace_path` 整目录替换四类问题 |
| **播放集** | 新建 / 克隆 / 重命名 / 删除 / 切换。**导出为 `.ck3ps.json` 分享给朋友，导入时按 Steam ID 匹配本地模组，缺失的工坊模组自动订阅** |
| **界面语言** | 中文 / English / Français / Deutsch / Svenska，设置页切换即时生效，DLC 分类与名称同步本地化 |
| **DLC 管理** | 读取 `game/dlc`，卡片式开关，中文名称（官方中文包没翻的部分我补了）。拥有与否调用 `steam_api64.dll` 的 `BIsSubscribedApp` 直接问 Steam——和 Steam 库、游戏内锁定同源；Steam 没开时降级为不锁定并提示 |
| **存档管理** | 列出 `save games` 下所有 `.ck3`，支持定位到资源管理器、**移入回收站**（可撤销） |
| **创意工坊** | 已订阅模组清单、一键跳转工坊页面、关键词搜索、**新订阅一键注册**（生成 `mod/ugc_xxx.mod`） |
| **常用工具** | 一键打开游戏根目录、common / events / gui / localization、日志、`dlc_load.json`、`launcher-v2.sqlite`、备份目录 |
| **崩溃诊断** | 一键收集系统硬件 / 崩溃记录 / 日志 / 模组列表，裁剪超大文件后打包成几百 KB 的 zip，可另存到任意位置。内置判断：页文件不足、无 crashes 目录等 |
| **一致性检测** | 概览页会对比「播放集勾选的」和「游戏实际加载的」，不一致时报警并可一键修复 |

---

## 排序是怎么生效的

CK3 有**两套**并存的数据，只改一处是不够的：

1. `launcher-v2.sqlite` → `playsets_mods.position`：官方启动器界面里看到的顺序
2. `dlc_load.json` → `enabled_mods` 数组：**游戏启动时真正读取的加载顺序**

本启动器每次保存会**同时写这两处**，所以：
- 用我的启动器排完序，再开官方启动器，顺序是对的
- 直接点「开始游戏」跳过官方启动器，进游戏也是对的

> **越靠下优先级越高**：后加载的模组会覆盖先加载的同名文件。
> 所以大型框架类模组放上面，补丁/汉化放下面。

---

## 数据安全

写盘前会自动备份到 `backups/`，最多保留 30 份：

- `launcher-v2-YYYYMMDD-HHMMSS.sqlite`
- `dlc_load-YYYYMMDD-HHMMSS.json`

**官方启动器开着的时候保存会失败**（数据库被独占锁），启动器会提示你先关掉它。

想手动回滚：把 `backups/` 里对应时间的文件复制回
`C:\Users\<你>\Documents\Paradox Interactive\Crusader Kings III\` 覆盖即可。

---

## DLC 中文名（出新 DLC 怎么加翻译）

Paradox 在 Steam 商店里没给 DLC 设中文名（`l=schinese` 拉回来还是英文），
游戏本体的中文语言包里也**没有 DLC 名称条目**（DLC 名只在启动器和商店显示，
游戏内不显示，所以官方没做这部分本地化）。因此本程序内置了一份对照表：
`backend/dlc_cn.py`。

**以后 Paradox 出新 DLC，只要在里面加一行：**

```python
"英文名": "中文名",
```

例如：

```python
"the great wave": "大浪潮",
```

就能显示中文。**即使不改，也只会那一个新 DLC 显示英文，其他照常，不影响使用。**

几个说明：

- 键用**英文名**（不是目录名）：目录名各游戏规则不同、还会随版本变，英文名更稳定
- 写之前不用管前缀，`normalize()` 会自动剥掉
  `Expansion - Europa Universalis IV:` / `Music -` / `Country Pack -` /
  `Victoria 3: ... - Immersion Pack` 这类前后缀，只留核心名
- 已有 295 条，覆盖 ck3 / stellaris / hoi4 / vic3 / eu4 / eu5 / ck2 /
  imperator / skylines / skylines2
- CK3 另有一张按目录名索引的老表 `DLC_CN`（在 `ck3core.py` 里），
  优先级更高，中文名以它为准

**改完记得把 `server.py` 里的 `_DLC_CACHE_VER` +1**，否则玩家本机缓存里
还是旧名字，看不到变化。

---

## 目录结构

```
CK3Launcher/
├─ 启动启动器.bat        源码版启动（无控制台）
├─ 启动器-调试.bat       源码版调试启动（有控制台，看报错用）
├─ launcher.py           入口：起本地服务 + 开原生窗口
├─ build.spec            PyInstaller 打包配置
├─ make_release.py       组装绿色目录 + 生成 zip
├─ test_nav.py           自动化测试：模拟点击每个导航项
├─ shot_dialog.py        截图验证对话框样式
├─ launcher-config.json  自动生成的路径配置（首次运行后出现）
├─ backups/              自动备份（内容去重，每种最多 12 份）
├─ dlc_data_*.json       DLC 清单缓存（可删，下次重新读）
├─ achievement_data_*.json  成就缓存（可删）
├─ achievement_icons/    从游戏 DDS 转出的成就图标（可删）
├─ mod-aliases.json      模组自定义显示名（删了会丢改名记录）
├─ mod-collections.json  收藏夹（删了会丢收藏夹）
├─ backend/
│  ├─ ck3core.py         路径探测 / 描述文件解析 / 数据库读写 / 备份 / DLC 扫描
│  ├─ server.py          本地 API（只监听 127.0.0.1）+ 各类本地缓存
│  ├─ dlc_cn.py          DLC 中文名对照表（全游戏，离线内置）
│  ├─ dlc_meta.py        DLC 商店元数据内置快照（名称 / 日期 / 封面）
│  ├─ steamdlc.py        向 Steam 客户端查 DLC 授权
│  ├─ achievements.py    Steam 成就解析 + 游戏 DDS 图标转 PNG
│  ├─ diagnose.py        崩溃诊断信息收集
│  └─ windlg.py          原生文件对话框（保存 / 打开）
├─ static/               界面（HTML / CSS / JS，无框架）
├─ tutorial/             教程源文件（使用教程.html + img/）
├─ packaging/            绿色版附带文件模板（说明 / LICENSE）
├─ dist/                 打包输出的 exe
└─ release/              最终可分享的 zip 与绿色目录
```

---

## 常见问题

**Q：窗口打不开？**
用 `启动器-调试.bat` 看报错。如果提示 webview 初始化失败，程序会自动改用 Edge 应用模式打开。

**Q：显示「官方启动器正在运行」？**
Paradox 官方启动器会独占 `launcher-v2.sqlite`。关掉它（托盘也退掉）再保存。

**Q：模组显示「待注册」？**
这些是创意工坊新订阅、但还没生成 `.mod` 描述文件的。到「创意工坊」页点「一键注册全部」。

**Q：改了排序但游戏里没变？**
看概览页的一致性检测。如果显示不一致，点「按当前播放集修复」。

**Q：会不会把我存档搞坏？**
不会。启动器只碰 `launcher-v2.sqlite` 和 `dlc_load.json`，不碰 `save games` 里的文件（除非你在存档页主动删除，那是移进回收站）。
