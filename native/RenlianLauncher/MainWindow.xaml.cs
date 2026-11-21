using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace RenlianLauncher;

public partial class MainWindow : Window
{
    /// <summary>当前工坊面板对应的游戏 appid：OpenWorkshop 打开页面时从 URL 提取并记住，
    /// 首页按钮 / 工坊内搜索都跟随它，不再写死 CK3。</summary>
    private string _workshopAppId = "1158310";

    private readonly string _url;
    private string _workshopUrl = "";
    private bool _wsInitializing;
    private bool _wsReady;
    private readonly List<SubItem> _subs = new();

    private sealed class SubItem
    {
        public string SteamId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Sub { get; set; } = "";
        public ImageSource? Thumb { get; set; }
    }

    public MainWindow(string url)
    {
        InitializeComponent();
        _url = url;

        // 窗口大小：跟随屏幕，跟旧版一致的观感
        var wa = SystemParameters.WorkArea;
        Width = Math.Max(1120, Math.Min(1520, wa.Width * 0.84));
        Height = Math.Max(700, Math.Min(960, wa.Height * 0.86));

        WsBack.Click += (_, _) =>
        {
            CloseWorkshop();
            // 返回启动器时把前端切回模组页，避免又停在那张引导页上
            try { Web.CoreWebView2?.ExecuteScriptAsync("location.hash='#/mods'"); } catch { }
        };
        WsHome.Click += (_, _) => NavigateWorkshop(WorkshopHomeUrl());
        WsNavBack.Click += (_, _) => { if (_wsReady && WorkshopWeb.CanGoBack) WorkshopWeb.GoBack(); };
        WsRefresh.Click += (_, _) => { if (_wsReady) WorkshopWeb.Reload(); };
        WsSearch.Click += (_, _) => DoWorkshopSearch();
        WsQuery.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) DoWorkshopSearch(); };
        WsSteamOpen.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("steam://openurl/" + _workshopUrl) { UseShellExecute = true }); }
            catch { }
        };
        WsUpload.Click += (_, _) =>
        {
            CloseWorkshop();
            try { Web.CoreWebView2?.ExecuteScriptAsync("window.__openWorkshopUpload && window.__openWorkshopUpload();"); }
            catch { }
        };
        WsCheck.Click += (_, _) =>
        {
            try { Web.CoreWebView2?.ExecuteScriptAsync("window.__wsRescan && window.__wsRescan();"); }
            catch { }
        };
        WsLocalSearch.TextChanged += (_, _) => ApplySubFilter();
        WsSubscribedList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.Count == 0) return;
            if (e.AddedItems[0] is SubItem s && !string.IsNullOrEmpty(s.SteamId))
            {
                NavigateWorkshop("https://steamcommunity.com/sharedfiles/filedetails/?id=" + s.SteamId + "&l=schinese");
                WsSubscribedList.SelectedIndex = -1;
            }
        };

        Loaded += async (_, _) => await InitWebAsync();
    }

    private string WorkshopHomeUrl() =>
        $"https://steamcommunity.com/app/{_workshopAppId}/workshop/?l=schinese";

    private void DoWorkshopSearch()
    {
        var q = WsQuery.Text.Trim();
        var url = string.IsNullOrEmpty(q)
            ? WorkshopHomeUrl()
            : "https://steamcommunity.com/workshop/browse/?appid=" + _workshopAppId + "&searchtext="
              + Uri.EscapeDataString(q) + "&browsesort=textsearch&section=readytouseitems&l=schinese";
        NavigateWorkshop(url);
    }

    private void NavigateWorkshop(string url)
    {
        _workshopUrl = url;
        WsTitle.Text = "Steam 创意工坊 · 载入中…";
        if (_wsReady) WorkshopWeb.Source = new Uri(url);
        else _ = InitWorkshopWebAsync(Core.Paths.WebViewCacheDir);
    }

    private async Task InitWebAsync()
    {
        try
        {
            var userData = Core.Paths.WebViewCacheDir;
            System.IO.Directory.CreateDirectory(userData);
            var env = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--disable-features=Translate,msEdgeIdentity,msEdgeSync --no-first-run",
            });
            await Web.EnsureCoreWebView2Async(env);

            var s = Web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreDevToolsEnabled = false;
            s.IsZoomControlEnabled = false;

            // 站外链接交给系统浏览器，别在启动器窗口里跑
            Web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
            };
            Web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith(_url, StringComparison.OrdinalIgnoreCase) &&
                    !e.Uri.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                    !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                }
            };

            Web.Source = new Uri(_url);

            // 工坊浏览器用独立缓存，避免污染启动器本地页面的登录态/存储
            _ = InitWorkshopWebAsync(userData);
        }
        catch (Exception ex)
        {
            MessageBox.Show("内置浏览器初始化失败：\n" + ex.Message +
                            "\n\n请确认已安装 Microsoft Edge WebView2 运行时。",
                "人联启动器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitWorkshopWebAsync(string userData)
    {
        if (_wsReady || _wsInitializing) return;
        _wsInitializing = true;
        try
        {
            var wsData = System.IO.Path.Combine(userData, "workshop");
            System.IO.Directory.CreateDirectory(wsData);
            var env = await CoreWebView2Environment.CreateAsync(null, wsData, new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--no-first-run",
            });
            await WorkshopWeb.EnsureCoreWebView2Async(env);

            var st = WorkshopWeb.CoreWebView2.Settings;
            st.IsStatusBarEnabled = false;
            st.AreDevToolsEnabled = false;
            st.IsZoomControlEnabled = false;

            // 设置 Steam 社区深色主题 cookie（Steam 官网据此渲染深色版式），只作用于本 WebView profile
            try
            {
                var ck = WorkshopWeb.CoreWebView2.CookieManager;
                foreach (var host in new[] { "steamcommunity.com", ".steamcommunity.com" })
                {
                    ck.AddOrUpdateCookie(ck.CreateCookie("birthtime", "1000", host, "/"));
                    ck.AddOrUpdateCookie(ck.CreateCookie("lastagecheckage", "1-January-2000", host, "/"));
                    ck.AddOrUpdateCookie(ck.CreateCookie("wants_mature_content", "1", host, "/"));
                    ck.AddOrUpdateCookie(ck.CreateCookie("Steam_Language", "schinese", host, "/"));
                    ck.AddOrUpdateCookie(ck.CreateCookie("theme", "dark", host, "/"));
                }
            }
            catch (Exception ex) { App.Log("Steam cookie 设置跳过：" + ex.Message); }

            // ① 把 Steam 站内新窗口/新标签导航拦回当前面板；
            // ② 注入深色 CSS（兜底，即使官方主题 cookie 没生效也保持深色）；
            // ③ 把工坊左侧分类 / 排序 / 语言的英文词条汉化成中文。
            await WorkshopWeb.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
(function () {
  if (!/^(www\.)?steamcommunity\.com$/.test(location.hostname)) return;

  // ---- 1. 新窗口拦回当前视图 ----
  if (!window.__RL_WS_POPUP__) {
    window.__RL_WS_POPUP__ = true;
    const go = value => {
      try {
        const u = new URL(String(value), location.href);
        if (u.protocol === 'https:' && /^(www\.)?steamcommunity\.com$/.test(u.hostname)) {
          location.assign(u.href);
          return true;
        }
      } catch (e) {}
      return false;
    };
    const orig = window.open.bind(window);
    window.open = function (url, target, features) {
      if (go(url)) return window;
      return orig(url, target, features);
    };
    document.addEventListener('click', e => {
      if (e.defaultPrevented || e.button !== 0 || !(e.target instanceof Element)) return;
      const a = e.target.closest('a[href]');
      if (!a || String(a.target || '').toLowerCase() !== '_blank') return;
      if (go(a.href)) { e.preventDefault(); e.stopImmediatePropagation(); }
    }, true);
  }

  // ---- 2. 深色主题 CSS 兜底 ----
  const cssId = '__rl_ws_dark_css';
  const ensureCss = () => {
    if (document.getElementById(cssId)) return;
    const s = document.createElement('style');
    s.id = cssId;
    s.textContent = `
      html, body { background:#1b2838 !important; color:#c7d5e0 !important; }
      .responsive_page_frame, .responsive_page_content, .page_content, #application_root,
      .workshopBrowseItemsPage, .workshopBrowseItems, .workshopItem, .workshopItemDetailsHeader,
      .rightcol, .leftcol, .workshopBrowseSidebar, .workshopBrowseResult, .itemSection {
        background-color:#16202d !important; color:#c7d5e0 !important; border-color:#2a475e !important;
      }
      .workshopItemTitle, .workshopItemTitle a, .workshopBrowseItemsPage a, .workshopItemDetailsHeader a {
        color:#e5e9ec !important;
      }
      .workshopBrowseResult, .workshopItem { border:1px solid #2a475e !important; }
      .workshopItem:hover, .workshopBrowseResult:hover { background-color:#1c2b3a !important; }
      a, .workshopBrowseSidebar a, .workshop_tags a { color:#66c0f4 !important; }
      .tab, ._tab, .browseFilter .tab { background:#16202d !important; color:#8ba0b2 !important; border-color:#2a475e !important; }
      .tab.active, .tab:hover, ._tab.active { background:#1a4a6e !important; color:#fff !important; }
      input[type=text], .workshopBrowseSearch input, select { background:#16202d !important; color:#c7d5e0 !important; border:1px solid #3d6480 !important; }
      .btnv6_blue_hoverfade, .btn_medium, .btn_green_white_innerfade { background:#2a475e !important; color:#c7d5e0 !important; }
      hr, .hr { border-color:#2a475e !important; }
      .workshopBrowseResultContent, .workshopItemDesc { color:#a9b6c2 !important; }
      .friendHighlight, .workshopItemFriend { background:#1c2b3a !important; }
    `;
    (document.head || document.documentElement).appendChild(s);
  };

  // ---- 3. 分类 / 排序 / 语言汉化 ----
  const ZH = {
    'Categories':'分类', 'Category':'分类',
    'Alternative History':'架空历史', 'Balance':'平衡性',
    'Character Focuses':'角色聚焦', 'Character Interactions':'角色互动',
    'Culture':'文化', 'Decisions':'决议', 'Events':'事件',
    'Gameplay':'游戏玩法', 'Graphics':'图形', 'Historical':'历史',
    'Map':'地图', 'Military':'军事', 'Religion':'宗教', 'Sound':'音效',
    'Total Conversion':'全面转换', 'Translation':'翻译', 'Uploads':'上传内容',
    'Utilities':'实用工具',
    'COMPATIBLE VERSION':'兼容版本', 'Compatible Version':'兼容版本',
    'Required DLC':'所需 DLC', 'All':'全部',
    'Most Popular':'最热门', 'Trending':'热门趋势', 'Most Subscribed':'最多订阅',
    'Recently Updated':'最近更新', 'Most Recent':'最新发布',
    'Top Rated':'最高好评', 'Most Reviewed':'最多评价',
    'All Languages':'全部语言', 'English':'英语', 'Simplified Chinese':'简体中文',
    'Traditional Chinese':'繁体中文', 'French':'法语', 'German':'德语',
    'Spanish - Spain':'西班牙语', 'Japanese':'日语', 'Korean':'韩语',
    'Russian':'俄语', 'Turkish':'土耳其语', 'Polish':'波兰语',
    'Portuguese - Brazil':'葡萄牙语（巴西）', 'Italian':'意大利语',
    'Only Chinese Content':'仅中文内容',
    'Home':'主页', 'Browse':'浏览', 'Your Items':'您的物品', 'Discussion':'讨论', 'About':'关于',
    'Friends\' Favorites':'好友收藏', 'Most Required':'被最多项目需要',
    'Your Subscriptions':'您的订阅', 'Your Collections':'您的收藏',
    'Browse All':'浏览全部', 'Show All':'显示全部',
    'Subscribe to all':'全部订阅', 'Items':'物品', 'Creators':'创作者',
    'Workshop':'创意工坊', 'Steam Workshop':'Steam 创意工坊',
    'Search the Workshop':'搜索创意工坊', 'Sort By':'排序方式'
  };
  const zhText = () => {
    // 文本节点汉化：只替换纯文本叶子节点，避免破坏结构
    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const nodes = [];
    let n;
    while ((n = walker.nextNode())) nodes.push(n);
    for (const node of nodes) {
      const t = node.nodeValue.trim();
      if (!t || t.length > 40) continue;
      const zh = ZH[t];
      if (zh && zh !== t) node.nodeValue = node.nodeValue.replace(t, zh);
    }
    // 占位符里的英文关键词汉化
    document.querySelectorAll('input[placeholder], a[title]').forEach(el => {
      const p = el.getAttribute('placeholder');
      if (p && ZH[p.trim()]) el.setAttribute('placeholder', ZH[p.trim()]);
    });
  };

  const applyAll = () => { ensureCss(); zhText(); };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', applyAll);
  else applyAll();
  // Steam 页面是动态渲染的，observe 到新节点再补一次
  const mo = new MutationObserver(() => { ensureCss(); zhText(); });
  mo.observe(document.documentElement, { childList:true, subtree:true });
  // 兜底定时（筛选切换后文本会被重建）
  setInterval(() => { ensureCss(); zhText(); }, 1200);
})();");

            WorkshopWeb.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                if (e.Uri.StartsWith("https://steamcommunity.com/", StringComparison.OrdinalIgnoreCase))
                {
                    e.Handled = true;
                    WorkshopWeb.Source = new Uri(e.Uri);
                    return;
                }
                e.Handled = true;
                try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
            };
            WorkshopWeb.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("https://steamcommunity.com/", StringComparison.OrdinalIgnoreCase) &&
                    !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                }
            };
            WorkshopWeb.CoreWebView2.NavigationCompleted += (_, _) =>
                Dispatcher.Invoke(() => WsTitle.Text = "Steam 创意工坊 · " + WorkshopWeb.CoreWebView2.DocumentTitle);

            _wsReady = true;
            if (WorkshopPanel.Visibility == Visibility.Visible)
                WorkshopWeb.Source = new Uri(_workshopUrl);
        }
        catch (Exception ex)
        {
            App.Log("工坊浏览器初始化失败：" + ex.Message);
        }
        finally { _wsInitializing = false; }
    }

    /// <summary>由本地 API 调用：在启动器窗口内打开 Steam 工坊并同步已订阅列表。
    /// API 跑在 Kestrel 线程，所有 WPF 控件访问必须切回 UI 线程。</summary>
    internal void OpenWorkshop(string url, JsonArray? mods = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        // 从 URL 里提取 appid 记住（/app/<id>/workshop/ 或 ?appid=<id>），
        // 之后面板里的「首页」「搜索」都跟随这个游戏
        var m = System.Text.RegularExpressions.Regex.Match(url, @"app/(\d+)/workshop|appid=(\d+)");
        if (m.Success)
        {
            var id = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!string.IsNullOrEmpty(id)) _workshopAppId = id;
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _workshopUrl = url;
            WorkshopPanel.Visibility = Visibility.Visible;
            WsTitle.Text = "Steam 创意工坊 · 载入中…";
            if (mods is not null) SetSubscribedMods(mods);
            if (_wsReady) WorkshopWeb.Source = new Uri(url);
            else _ = InitWorkshopWebAsync(Core.Paths.WebViewCacheDir);
        }));
    }

    /// <summary>更新右侧「已订阅 MOD」列表。传入前端 S.mods 里 source=steam 的精简数组。</summary>
    internal void SetSubscribedMods(JsonArray? mods)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _subs.Clear();
            if (mods is not null)
            {
                foreach (var node in mods)
                {
                    if (node is not JsonObject o) continue;
                    var sid = o["steamId"]?.ToString() ?? "";
                    if (string.IsNullOrEmpty(sid)) continue;
                    ImageSource? img = null;
                    try
                    {
                        var thumb = o["thumb"]?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(thumb) && System.IO.File.Exists(thumb))
                        {
                            var bi = new BitmapImage();
                            bi.BeginInit();
                            bi.UriSource = new Uri(thumb);
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            bi.DecodePixelWidth = 76;
                            bi.EndInit();
                            img = bi;
                        }
                    }
                    catch { }
                    _subs.Add(new SubItem
                    {
                        SteamId = sid,
                        Name = o["name"]?.ToString() ?? sid,
                        Sub = "MOD " + (o["version"]?.ToString() ?? "—") +
                              " · 支持 " + (o["supportedVersion"]?.ToString() ?? "—"),
                        Thumb = img,
                    });
                }
            }
            WsCnt.Text = _subs.Count.ToString();
            ApplySubFilter();
        }));
    }

    private void ApplySubFilter()
    {
        var q = (WsLocalSearch.Text ?? "").Trim();
        var list = string.IsNullOrEmpty(q)
            ? _subs.ToList()
            : _subs.Where(s => s.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                               s.SteamId.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        WsSubscribedList.ItemsSource = list;
    }

    internal void CloseWorkshop()
    {
        Dispatcher.BeginInvoke(new Action(() => WorkshopPanel.Visibility = Visibility.Collapsed));
    }
}
