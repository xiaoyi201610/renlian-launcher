/* ============================================================
   CK3 Launcher — 前端逻辑
   ============================================================ */
'use strict';

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];

const S = {
  state: null,
  mods: [],
  modByKey: new Map(),
  play: [],              // [{modFile, enabled, name, thumb, ...}]
  route: 'home',
  qLeft: '', qRight: '',
  filter: 'all',         // all | enabled | local | warn
  sortBy: 'name',
  dirtyTimer: null,
  saving: false,
  collections: [],       // [{name, count}] 收藏夹
  colFilter: '',         // 当前收藏夹筛选（空=全部）
  steamLink: null,       // 顶栏「关联 Steam」探测结果（按当前游戏查）
  steamDlgRefresh: null, // 「关联 Steam」弹窗打开时的重绘钩子，切换游戏后同步刷新
};

/* ---------------- 常量 ---------------- */
const LAUNCHER_VERSION = '1.7.2.5'; // 原生 .NET 版（1.7.2 正式版前的递进版本，与 csproj 保持一致）

/* ---------------- 工具 ---------------- */
async function api(path, opts) {
  const r = await fetch(path, opts ? { headers: { 'Content-Type': 'application/json' }, ...opts } : undefined);
  if (!r.ok) {
    let msg = 'HTTP ' + r.status;
    try {
      const j = await r.json();
      if (j && j.error) msg = String(j.error).trim().split('\n').pop();
    } catch (e) {}
    throw new Error(msg);
  }
  return r.json();
}
const post = (p, body) => api(p, { method: 'POST', body: JSON.stringify(body || {}) });

function esc(s) {
  return String(s == null ? '' : s).replace(/[&<>"']/g, c =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}
// 游戏显示名：中文界面用中文名，其他语言用英文名
function gameLabel(def) {
  if (!def) return '';
  return i18nLang() === 'zh' ? (def.name || def.en || '') : (def.en || def.name || '');
}

function fmtSize(b) {
  if (!b) return '—';
  const u = ['B', 'KB', 'MB', 'GB'];
  let i = 0, v = b;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return v.toFixed(v < 10 && i > 0 ? 1 : 0) + ' ' + u[i];
}
function fmtTime(ts) {
  if (!ts) return '—';
  let d;
  if (typeof ts === 'number') {
    d = new Date(ts > 1e12 ? ts : ts * 1000);
  } else {
    // 后端 SQLite datetime('now') 返回 "YYYY-MM-DD HH:MM:SS"，JS 不能直接解析
    const s = String(ts).trim().replace(' ', 'T');
    d = new Date(s.length === 19 ? s : s);
  }
  if (isNaN(d.getTime())) return String(ts);
  const p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

/* DLC 分类：后端只给中文名，其他语言用下面这张表（先按 code 查，查不到再按中文名映射成 code） */
const DLC_TYPE_CODES = {
  '大型扩展包': 'expansion', '扩展': 'expansion', '核心包': 'core', '核心扩展包': 'ce',
  '风味包': 'fp', '内容包': 'minor', '内容': 'minor', '内容创作者包': 'cp',
  '音乐包': 'mp', '音乐': 'mp', '事件包': 'bp', '兵模包': 'unit_pack', '单位包': 'unit_pack',
  '头像包': 'portrait', '纹章包': 'dynasty_shield', '装饰包': 'sp', '其他': 'other',
};
const DLC_TYPE_I18N = {
  en: {
    expansion: 'Expansion', core: 'Core', ce: 'Core expansion', fp: 'Flavour pack',
    bp: 'Event pack', cp: 'Creator pack', sp: 'Cosmetic pack', mp: 'Music pack',
    minor: 'Content pack', medium: 'Flavour pack', major: 'Expansion', music: 'Music pack',
    event: 'Event pack', content: 'Content pack', content_pack: 'Content pack',
    unit: 'Unit pack', unit_pack: 'Unit pack', portrait: 'Portrait pack',
    dynasty_shield: 'Heraldry pack', cosmetic: 'Cosmetic pack', extra: 'Cosmetic pack',
    other: 'Other',
  },
  fr: {
    expansion: 'Extension', core: 'Cœur', ce: 'Extension principale', fp: 'Pack thématique',
    bp: "Pack d'événements", cp: 'Pack de créateur', sp: 'Pack cosmétique', mp: 'Pack musical',
    minor: 'Pack de contenu', medium: 'Pack thématique', major: 'Extension', music: 'Pack musical',
    event: "Pack d'événements", content: 'Pack de contenu', content_pack: 'Pack de contenu',
    unit: "Pack d'unités", unit_pack: "Pack d'unités", portrait: 'Pack de portraits',
    dynasty_shield: 'Pack héraldique', cosmetic: 'Pack cosmétique', extra: 'Pack cosmétique',
    other: 'Autre',
  },
  de: {
    expansion: 'Erweiterung', core: 'Kern', ce: 'Kern-Erweiterung', fp: 'Flavour-Paket',
    bp: 'Event-Paket', cp: 'Creator-Paket', sp: 'Kosmetik-Paket', mp: 'Musik-Paket',
    minor: 'Inhalts-Paket', medium: 'Flavour-Paket', major: 'Erweiterung', music: 'Musik-Paket',
    event: 'Event-Paket', content: 'Inhalts-Paket', content_pack: 'Inhalts-Paket',
    unit: 'Einheiten-Paket', unit_pack: 'Einheiten-Paket', portrait: 'Porträt-Paket',
    dynasty_shield: 'Wappen-Paket', cosmetic: 'Kosmetik-Paket', extra: 'Kosmetik-Paket',
    other: 'Sonstiges',
  },
  sv: {
    expansion: 'Expansion', core: 'Kärna', ce: 'Kärnexpansion', fp: 'Temapaket',
    bp: 'Händelsepaket', cp: 'Skaparpaket', sp: 'Kosmetikpaket', mp: 'Musikpaket',
    minor: 'Innehållspaket', medium: 'Temapaket', major: 'Expansion', music: 'Musikpaket',
    event: 'Händelsepaket', content: 'Innehållspaket', content_pack: 'Innehållspaket',
    unit: 'Enhetspaket', unit_pack: 'Enhetspaket', portrait: 'Porträttpaket',
    dynasty_shield: 'Vapenpaket', cosmetic: 'Kosmetikpaket', extra: 'Kosmetikpaket',
    other: 'Övrigt',
  },
};
function dlcTypeLabel(code, cn) {
  if (i18nLang() === 'zh') return cn || code || '';
  const table = DLC_TYPE_I18N[i18nLang()] || DLC_TYPE_I18N.en;
  return table[code] || table[DLC_TYPE_CODES[cn]] || cn || code || '';
}
/** DLC 显示名：中文界面用中文名，其他语言用 Steam 原名 */
function dlcName(x) {
  return i18nLang() === 'zh'
    ? (x.nameCn || x.name || '')
    : (x.nameEn || x.name || x.nameCn || '');
}
/** Steam 返回的日期可能是「2024 年 9 月 10 日」，非中文界面转成 2024-09-10 */
function fmtDate(s) {
  if (!s || i18nLang() === 'zh') return s || '';
  const m = /(\d{4})\s*年\s*(\d{1,2})\s*月\s*(\d{1,2})\s*日/.exec(s);
  if (!m) return s;
  return `${m[1]}-${String(m[2]).padStart(2, '0')}-${String(m[3]).padStart(2, '0')}`;
}
let toastTimer = null;
function toast(msg, type = '') {
  const el = $('#toast');
  el.textContent = TT(msg);
  el.className = 'toast ' + type;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, 2600);
}

/* ---------------- 应用内对话框（替代原生 confirm / prompt） ----------------
   原生弹窗标题会显示 "127.0.0.1:端口 显示"，很难看，也暴露本地地址。 */
function dialog(opt) {
  const o = Object.assign({
    title: '提示', message: '', input: false, value: '',
    okText: '确定', cancelText: '取消', danger: false, hideCancel: false,
  }, opt || {});
  return new Promise(resolve => {
    const prevFocus = document.activeElement;
    const mask = document.createElement('div');
    mask.className = 'modal-mask';
    mask.innerHTML = `
      <div class="modal${o.danger ? ' danger' : ''}">
        <div class="modal-head">${esc(TT(o.title))}</div>
        <div class="modal-body">
          ${o.message ? `<div class="modal-msg">${esc(TT(o.message))}</div>` : ''}
          ${o.input ? `<input type="text" class="modal-input" value="${esc(o.value)}">` : ''}
        </div>
        <div class="modal-foot">
          ${o.hideCancel ? '' : `<button class="btn ghost" data-m="cancel">${esc(TT(o.cancelText))}</button>`}
          <button class="btn ${o.danger ? 'danger-btn' : 'primary'}" data-m="ok">${esc(TT(o.okText))}</button>
        </div>
      </div>`;
    document.body.appendChild(mask);
    requestAnimationFrame(() => mask.classList.add('show'));

    const inp = mask.querySelector('.modal-input');
    const finish = (val) => {
      mask.classList.remove('show');
      document.removeEventListener('keydown', onKey, true);
      setTimeout(() => {
        mask.remove();
        if (prevFocus && prevFocus.focus) { try { prevFocus.focus(); } catch (e) {} }
        resolve(val);
      }, 130);
    };
    const onKey = (e) => {
      if (e.key === 'Escape') { e.stopPropagation(); finish(o.input ? null : false); }
      else if (e.key === 'Enter') {
        e.stopPropagation();
        finish(o.input ? (inp.value.trim() || null) : true);
      }
    };
    mask.querySelector('[data-m="ok"]').onclick = () =>
      finish(o.input ? (inp.value.trim() || null) : true);
    const c = mask.querySelector('[data-m="cancel"]');
    if (c) c.onclick = () => finish(o.input ? null : false);
    mask.addEventListener('mousedown', e => {
      if (e.target === mask) finish(o.input ? null : false);
    });
    document.addEventListener('keydown', onKey, true);
    setTimeout(() => {
      if (inp) { inp.focus(); inp.select(); }
      else mask.querySelector('[data-m="ok"]').focus();
    }, 60);
  });
}

const ui = {
  confirm: (message, o = {}) => dialog(Object.assign({ message, title: '请确认' }, o)),
  alert: (message, o = {}) => dialog(Object.assign({ message, title: '提示', hideCancel: true, okText: '知道了' }, o)),
  prompt: (message, value = '', o = {}) => dialog(Object.assign({ message, input: true, value, title: '请输入' }, o)),
};
/* ---------------- 路径探测失败提示 ----------------
   探测不到或目录不存在时，给出常见原因与解决办法，而不是只弹一句“找不到”。 */
const PATH_TIPS = {
  gameRoot: {
    causes: '游戏没有安装或已卸载；游戏装在非默认 Steam 库、移动硬盘没挂载；Steam 注册表信息缺失；安装目录被改名或移动。',
    fixes: '先在 Steam 里启动一次游戏；点「切换」选到游戏安装目录（里面应有 game 文件夹和游戏 exe）；移动硬盘先接好；仍不行用 Steam 的「验证游戏文件完整性」。',
  },
  userDir: {
    causes: '游戏从未启动过（首次启动才会生成该目录）；「文档」被 OneDrive 重定向；目录权限不足或被杀软拦截。',
    fixes: '先启动游戏到主菜单再退出；点「切换」选到 文档\\Paradox Interactive\\游戏名 这一层；不要把上一级目录填进来。',
  },
  workshopDir: {
    causes: '游戏装在非默认 Steam 库；没有订阅模组，或 Steam 还没下载完；Steam 未登录 / 离线；工坊缓存被清理。',
    fixes: '确认订阅并在 Steam 里等下载完成；点「切换」选到 steamapps\\workshop\\content\\游戏ID；有多个 Steam 库时选游戏所在的那个库。',
  },
};

function pathIssueMessage(issues) {
  return (issues || []).map((it, i) => {
    const tip = PATH_TIPS[it.field] || {};
    // 后端 pathIssues 返回 { field,label,value,hint,found }，注意不是 kind/path
    const val = it.value || it.path || '';
    const state = !val ? '未探测到（空）' : (it.found === false ? `路径不存在：${val}` : `当前值：${val}`);
    const hint = it.hint ? `\n提示：${it.hint}` : '';
    return `${i + 1}. 【${it.label}】${state}\n可能原因：${tip.causes || '未知'}\n解决办法：${tip.fixes || '请在设置里手动指定路径'}${hint}`;
  }).join('\n\n');
}

function pathIssueDialog(issues) {
  if (!issues || !issues.length) return Promise.resolve(false);
  // 弹一个带「去设置」的对话框，用户可直接跳到设置页手动指定，而不是只告诉他"知道了"
  return new Promise(resolve => {
    const mask = document.createElement('div');
    mask.className = 'modal-mask';
    mask.innerHTML = `
      <div class="modal">
        <div class="modal-head">路径未找到 · 请检查</div>
        <div class="modal-body">
          <div class="modal-msg" style="white-space:pre-wrap;max-height:46vh;overflow:auto;line-height:1.7">${esc(pathIssueMessage(issues))}</div>
        </div>
        <div class="modal-foot">
          <button class="btn ghost" data-m="later">稍后处理</button>
          <button class="btn primary" data-m="setup">去设置指定目录</button>
        </div>
      </div>`;
    document.body.appendChild(mask);
    requestAnimationFrame(() => mask.classList.add('show'));
    const close = (go) => {
      mask.classList.remove('show');
      setTimeout(() => { mask.remove(); resolve(go); }, 130);
    };
    mask.querySelector('[data-m="later"]').onclick = () => close(false);
    mask.querySelector('[data-m="setup"]').onclick = () => close(true);
    mask.addEventListener('mousedown', e => { if (e.target === mask) close(false); });
  }).then(go => {
    if (go) { location.hash = '#/settings'; }
    return go;
  });
}

function saveState(text, persist) {
  const el = $('#saveState');
  el.textContent = text;
  el.classList.add('show');
  clearTimeout(el._t);
  if (!persist) el._t = setTimeout(() => el.classList.remove('show'), 1800);
}
const thumbUrl = p => p ? '/api/thumb?p=' + encodeURIComponent(p) : '';

/* ---------------- 启动 ---------------- */
async function boot() {
  try {
    S.state = await api('/api/state');
  } catch (e) {
    $('#view').innerHTML = `<div class="empty"><div class="big">⚠</div>无法连接本地服务<br><small>${esc(e.message)}</small></div>`;
    return;
  }
  // 界面语言：先套上语言包，再把导航/顶部栏这些静态文字刷一遍
  const savedLang = (S.state.config || {}).uiLang || 'zh';
  setLang(I18N_DICT[savedLang] ? savedLang : 'zh');
  applyI18nDom();
  // 还原用户上次拖拽好的右侧栏顺序
  applyNavOrder((S.state.config || {}).navOrder || []);

  paintTop();
  // 顶栏 Steam 状态：启动时刷一次；之后由「关联 Steam」弹窗保存后自行刷新
  const btnSteam = $('#btnSteam');
  if (btnSteam) btnSteam.onclick = openSteamLinkDialog;
  refreshSteamChip();
  bindNavDrag();
  bindNav();
  bindGameSwitcher();
  window.addEventListener('hashchange', route);
  route();

  // 路径探测失败或目录不存在：启动时弹一次，写明原因和解决办法
  if ((S.state.pathIssues || []).length && !S.pathNoticeShown) {
    S.pathNoticeShown = true;
    setTimeout(() => pathIssueDialog(S.state.pathIssues), 900);
  }

  // 后台加载 mod 索引
  saveState(T('home.scanning'), true);
  try {
    const d = await api('/api/mods');
    S.mods = d.items || [];
    S.modByKey = new Map(S.mods.map(m => [m.modFile.toLowerCase(), m]));
  } catch (e) { /* ignore */ }
  saveState(T('home.loaded', { n: S.mods.length }));
  // 这些页面的内容依赖模组索引，加载完要重绘一次
  if (['mods', 'home', 'workshop'].includes(S.route)) render();
}

/* ---------------- 顶栏「关联 Steam」按钮 ----------------
   让群友一眼看出启动器到底读不读得到 Steam；读不到可以直接在这里手动关联，
   不用去猜是没装 Steam、权限不够，还是装在非默认库。 */
async function refreshSteamChip(data) {
  // data 由调用方传入时（弹窗保存、刚切换完游戏）直接用，避免重复请求
  let r = data;
  if (!r) {
    try { r = await api('/api/steam/link-info'); }
    catch (e) { return; }   // 本地服务暂不可用时保持默认外观，不影响其他操作
  }
  S.steamLink = r;
  // Steam 探测是按「当前游戏」查的（appmanifest / 工坊路径 / 模组数都随游戏变）。
  // 弹窗若正开着，必须同步重绘，否则切完游戏还显示上一个游戏的路径，看着像没连上。
  if (typeof S.steamDlgRefresh === 'function') S.steamDlgRefresh(r);
  const btn = $('#btnSteam');
  if (!btn) return;
  try {
    const linked = !!r.linked;
    btn.classList.toggle('linked', linked);
    const lab = btn.querySelector('[data-i18n]');
    if (lab) {
      lab.setAttribute('data-i18n', linked ? 'top.steamLinked' : 'top.steamLink');
      lab.textContent = T(linked ? 'top.steamLinked' : 'top.steamLink');
    }
    btn.title = linked
      ? (r.manualDir ? `Steam 已关联：${r.manualDir}` : `Steam 已自动识别（${(r.roots || []).length} 个库）`)
      : 'Steam 未关联，点此手动指定目录';
  } catch (e) { /* 本地服务暂不可用时保持默认外观，不影响其他操作 */ }
}

function openSteamLinkDialog() {
  const mask = document.createElement('div');
  mask.className = 'modal-mask';
  document.body.appendChild(mask);
  requestAnimationFrame(() => mask.classList.add('show'));

  const close = () => {
    S.steamDlgRefresh = null;
    mask.classList.remove('show');
    setTimeout(() => mask.remove(), 130);
  };
  mask.addEventListener('mousedown', e => { if (e.target === mask) close(); });

  const render = (data) => {
    const r = data || S.steamLink || {};
    const zh = i18nLang() === 'zh';
    const roots = (r.roots || []).map(x => x.path || x);
    const cg = (S.state && S.state.gameDefs || {})[S.state && S.state.currentGame];
    const rows = [
      // 让路径和具体游戏对上号，切完游戏打开弹窗不会误以为关联串到别的游戏
      [zh ? '当前游戏' : 'Game', cg ? gameLabel(cg) : '—'],
      [T('top.steamRoots'), roots.length ? roots.join('\n') : '—'],
      [zh ? '游戏清单' : 'Manifest', r.manifest || '—'],
      [zh ? '创意工坊' : 'Workshop', r.workshopDir || '—'],
      [zh ? '已识别模组' : 'Mods found', String(r.modCount || 0)],
    ];
    const modal = mask.querySelector('.modal') || document.createElement('div');
    // 重绘时保留用户还没保存的输入，避免切游戏刷新把正在填的目录冲掉
    const oldInput = modal.querySelector('#steamDirInput');
    const typed = oldInput ? oldInput.value : null;
    modal.className = 'modal steam-modal';
    modal.innerHTML = `
      <div class="modal-head">${esc(T('top.steamTitle'))}</div>
      <div class="modal-body">
        <div class="steam-state ${r.linked ? 'ok' : 'bad'}">${esc(r.msg || '')}</div>
        <div class="steam-kv">${rows.map(([k, v]) =>
          `<div class="steam-kv-row"><span>${esc(k)}</span><b>${esc(v)}</b></div>`).join('')}</div>
        <div class="steam-manual">
          <div class="steam-manual-lab">${esc(T('top.steamManual'))}</div>
          <input type="text" class="inp" id="steamDirInput" value="${esc(r.manualDir || '')}" placeholder="例如 D:/Steam">
          <div class="bar" style="margin-top:8px">
            <button class="btn sm ghost" id="steamDirPick">${esc(T('top.steamBrowse'))}</button>
          </div>
          <div class="hint" style="margin-top:8px;line-height:1.6">${esc(T('top.steamHint'))}</div>
          <div class="hint" id="steamLinkErr" style="color:var(--danger,#e24b4a);margin-top:6px"></div>
        </div>
      </div>
      <div class="modal-foot">
        <button class="btn ghost" data-m="clear">${esc(T('top.steamClear'))}</button>
        <button class="btn ghost" data-m="close">${esc(T('top.steamClose'))}</button>
        <button class="btn primary" data-m="save">${esc(T('top.steamSave'))}</button>
      </div>`;
    if (!modal.parentNode) mask.appendChild(modal);
    if (typed !== null && typed !== (r.manualDir || '')) {
      const inp = modal.querySelector('#steamDirInput');
      if (inp) inp.value = typed;
    }

    const err = () => modal.querySelector('#steamLinkErr');
    const apply = async (dir) => {
      modal.querySelector('[data-m="save"]').disabled = true;
      err().textContent = '';
      try {
        const res = await post('/api/steam/link', { dir });
        if (!res.ok) { err().textContent = res.error || T('common.failed'); return; }
        await refreshSteamChip(res);   // 传入结果，内部会同步重绘弹窗
        toast(T('top.steamLinked'), 'ok');
      } catch (e) { err().textContent = String((e && e.message) || e); }
      finally {
        const b = modal.querySelector('[data-m="save"]');
        if (b) b.disabled = false;
      }
    };
    modal.querySelector('[data-m="save"]').onclick = () =>
      apply(modal.querySelector('#steamDirInput').value.trim());
    modal.querySelector('[data-m="clear"]').onclick = () => apply('');
    modal.querySelector('[data-m="close"]').onclick = close;
    modal.querySelector('#steamDirPick').onclick = async () => {
      const inp = modal.querySelector('#steamDirInput');
      try {
        const res = await post('/api/pick-dir', { initial: inp.value });
        if (res && res.path) inp.value = res.path;
      } catch (e) { err().textContent = String((e && e.message) || e); }
    };
  };

  S.steamDlgRefresh = render;
  render();
}

function paintTop() {
  const st = S.state;
  $('#gameVer').textContent = (st.versionFull || st.version || T('common.unknown'));
  $('#footVer').textContent = 'v' + LAUNCHER_VERSION;

  // 游戏切换器：封面图 + 名称 + 版本
  const cur = st.currentGame || 'ck3';
  const curDef = (st.gameDefs || {})[cur] || { name: '十字军之王 III', en: 'Crusader Kings III' };
  const curGame = (st.games || []).find(g => g.key === cur) || {};
  const curCover = curGame.coverHeader || `/static/img/games/${cur}.jpg`;
  $('#gameLogo').src = curCover;
  $('#gameName').innerHTML = `${esc(gameLabel(curDef))} <span class="caret">▾</span>`;
  // 侧边栏品牌封面图
  $('#brandImg').src = curCover;
  $('#brandSub').textContent = curDef.en.toUpperCase();

  // 下拉菜单（卡片式，封面图）
  const menu = $('#gameMenu');
  const games = st.games || [];
  menu.innerHTML = `
    <div class="game-menu-title">${esc(T('top.chooseGame'))}</div>
    ${games.map(g => `
    <div class="game-menu-item ${g.key === cur ? 'active' : ''} ${g.installed ? '' : 'notinstalled'}" data-game="${g.key}">
      <img class="gmi-logo" src="${esc(g.cover)}" alt="" loading="lazy">
      <div>
        <div class="gmi-name">${esc(gameLabel(g))}</div>
        <div class="gmi-en">${esc(g.en)}</div>
      </div>
      <div class="gmi-tag">${g.installed ? esc(T('top.installed')) : esc(T('top.notInstalled'))}</div>
    </div>`).join('')}
    <div class="game-menu-foot" id="gameAddBtn">＋ ${esc(T('top.addGame'))}</div>`;
  $('#gameAddBtn').onclick = (e) => {
    e.stopPropagation();
    hideGameMenu();
    openAddGame();
  };
  $$('#gameMenu .game-menu-item').forEach(el => {
    el.onclick = async () => {
      const key = el.dataset.game;
      if (key === cur) { hideGameMenu(); return; }
      hideGameMenu();
      saveState(T('top.switching', { name: gameLabel(curDef) }), true);
      try {
        const r = await post('/api/switch-game', { game: key });
        if (r.ok) {
          // 先把上一个游戏的缓存全部清掉：否则概览页会拿旧游戏的数据算「已安装/已启用」
          S.state = r.state;
          S.mods = [];
          S.modByKey = new Map();
          S.play = [];
          S.collections = [];
          S.colFilter = '';
          await reloadGameData();   // 先重读模组索引 + 当前播放集（顶部统计也依赖它）
          paintTop();
          // Steam 探测是按当前游戏查的：appmanifest、工坊目录、模组数全跟着 appid 变。
          // 切完游戏必须重刷，否则顶栏和弹窗还显示上一个游戏的路径，用户会以为没连上。
          await refreshSteamChip();
          render();                 // 立刻重绘当前页面，不用再切侧边栏
          toast(T('top.switched', { name: gameLabel((r.state.gameDefs || {})[key]) || key }), 'ok');
          if ((r.state.pathIssues || []).length) pathIssueDialog(r.state.pathIssues);
        }
      } catch (e) {
        toast('切换失败：' + e.message, 'err');
      }
      saveState('', true);
    };
  });

  const sel = $('#quickPlayset');
  const list = st.playsets || [];
  sel.innerHTML = list.map(p =>
    `<option value="${p.id}"${p.id === st.activePlayset ? ' selected' : ''}>${esc(p.name)} (${p.enabled}/${p.total})</option>`
  ).join('') || `<option>${esc(T('top.noPlayset'))}</option>`;
  const act = list.find(p => p.id === st.activePlayset);
  const badge = $('#psBadge');
  badge.textContent = act ? T('top.enabledBadge', { n: act.enabled }) : T('top.notActive');
  badge.className = 'ps-badge' + (act ? '' : ' off');

  // 版本配置快速切换（每个游戏可有多个版本配置，切换即换整套模组/存档/设置）
  const vsel = $('#verSel');
  const vquick = $('#verQuick');
  const vprofiles = (st.config && st.config.profiles) || [];
  if (vsel && vquick) {
    if (vprofiles.length > 1) {
      vquick.hidden = false; vquick.style.display = '';
      vsel.innerHTML = vprofiles.map(p =>
        `<option value="${esc(p.id)}"${p.active ? ' selected' : ''}>${esc(p.name)}${p.gameVersion ? ' (' + esc(p.gameVersion) + ')' : ''}</option>`
      ).join('');
      vsel.onchange = async () => {
        const id = vsel.value;
        saveState(T('top.switchingVersion'), true);
        try {
          const r = await post('/api/profile/switch', { id });
          if (r.ok) {
            S.state = r.state;
            S.mods = []; S.modByKey = new Map(); S.play = []; S.collections = []; S.colFilter = '';
            await reloadGameData();
            paintTop();
            await refreshSteamChip();   // 版本配置切换也可能换工坊/清单路径，同步刷新关联状态
            render();
            toast(T('top.versionSwitched'), 'ok');
          } else {
            toast(r.error || T('common.failed'), 'err');
          }
        } catch (e) {
          toast('切换失败：' + e.message, 'err');
        }
        saveState('', true);
      };
    } else {
      // 只有 1 个（或 0 个）配置时隐藏下拉，并清空残留选项/回调
      vquick.hidden = true; vquick.style.display = 'none';
      vsel.innerHTML = ''; vsel.onchange = null;
    }
  }

  if (st.dbLocked) toast(T('top.dbLocked'), 'err');
}

// 游戏切换下拉的显示/隐藏
function showGameMenu() { $('#gameMenu').hidden = false; }
function hideGameMenu() { $('#gameMenu').hidden = true; }
function toggleGameMenu() {
  const m = $('#gameMenu');
  if (m.hidden) showGameMenu(); else hideGameMenu();
}

// 手动添加未识别的游戏
function openAddGame() {
  const st = S.state;
  const games = st.games || [];
  const mask = document.createElement('div');
  mask.className = 'modal-mask';
  mask.innerHTML = `
    <div class="modal" style="width:min(520px,calc(100vw - 60px))">
      <div class="modal-head">手动添加游戏</div>
      <div class="modal-body">
        <div style="font-size:calc(12px*var(--fs-scale));color:var(--dim-2);margin-bottom:14px;line-height:1.6">
          游戏没被自动识别到？选一个游戏，手动指定它的目录。识别不到本体的，至少指定用户数据目录即可。
        </div>
        <div style="margin-bottom:12px">
          <label style="font-size:calc(12px*var(--fs-scale));color:var(--dim)">选择游戏</label>
          <select id="agGame" style="width:100%;margin-top:5px;background:var(--panel-2);color:var(--fg);border:1px solid var(--line);border-radius:8px;padding:8px 10px;font-size:calc(13px*var(--fs-scale))">
            ${games.map(g => `<option value="${g.key}" ${g.installed ? 'disabled' : ''}>${esc(g.name)}${g.installed ? '（已识别）' : ''}</option>`).join('')}
          </select>
        </div>
        <div style="margin-bottom:10px">
          <label style="font-size:calc(12px*var(--fs-scale));color:var(--dim)">游戏根目录（含主程序）</label>
          <div style="display:flex;gap:8px;margin-top:5px">
            <input type="text" id="agRoot" placeholder="例如 D:\\steam\\steamapps\\common\\Victoria 2" style="flex:1;background:var(--panel-2);color:var(--fg);border:1px solid var(--line);border-radius:8px;padding:8px 10px;font-size:calc(13px*var(--fs-scale))">
            <button class="btn ghost" data-p="root">${esc(T('common.browse'))}</button>
          </div>
        </div>
        <div style="margin-bottom:4px">
          <label style="font-size:calc(12px*var(--fs-scale));color:var(--dim)">用户数据目录（可选）</label>
          <div style="display:flex;gap:8px;margin-top:5px">
            <input type="text" id="agUser" placeholder="含 mod 文件夹与 launcher-v2.sqlite" style="flex:1;background:var(--panel-2);color:var(--fg);border:1px solid var(--line);border-radius:8px;padding:8px 10px;font-size:calc(13px*var(--fs-scale))">
            <button class="btn ghost" data-p="user">${esc(T('common.browse'))}</button>
          </div>
        </div>
      </div>
      <div class="modal-foot">
        <button class="btn ghost" data-m="cancel">取消</button>
        <button class="btn primary" data-m="ok">添加</button>
      </div>
    </div>`;
  document.body.appendChild(mask);
  requestAnimationFrame(() => mask.classList.add('show'));
  const close = () => { mask.classList.remove('show'); setTimeout(() => mask.remove(), 130); };

  // 浏览目录
  $$('[data-p]', mask).forEach(b => b.onclick = async () => {
    const t = b.dataset.p;
    const target = t === 'root' ? '#agRoot' : '#agUser';
    const r = await post('/api/pick-dir', { initial: $(target, mask).value });
    if (r.path) $(target, mask).value = r.path;
  });

  mask.querySelector('[data-m="cancel"]').onclick = close;
  mask.querySelector('[data-m="ok"]').onclick = async () => {
    const key = $('#agGame', mask).value;
    const groot = $('#agRoot', mask).value.trim();
    const udir = $('#agUser', mask).value.trim();
    if (!groot && !udir) { toast('请至少指定一个目录', 'err'); return; }
    close();
    saveState('正在添加游戏…', true);
    try {
      const r = await post('/api/add-game', { game: key, gameRoot: groot, userDir: udir });
      if (r.ok) {
        S.state = r.state;
        paintTop();
        toast('已添加，可在顶部下拉切换', 'ok');
      } else {
        toast(r.error || '添加失败', 'err');
      }
    } catch (e) { toast('添加失败：' + e.message, 'err'); }
    saveState('', true);
  };
  mask.addEventListener('mousedown', e => { if (e.target === mask) close(); });
}

function bindGameSwitcher() {
  const sw = $('#gameSwitcher');
  if (!sw || sw._bound) return;
  sw._bound = true;
  sw.addEventListener('click', e => {
    e.stopPropagation();
    toggleGameMenu();
  });
  document.addEventListener('click', e => {
    if (!sw.contains(e.target)) hideGameMenu();
  });
}

/* ---------------- 路由 ---------------- */
function bindNav() {
  $$('.nav-item').forEach(a => {
    if (a._bound) return;
    a._bound = true;
    a.addEventListener('click', e => {
      // 刚拖拽完的那次 click 不算点击，避免误切页面
      const nav = $('#nav');
      if (nav && nav._suppressClick) { nav._suppressClick = false; e.preventDefault(); return; }
      const r = a.dataset.route;
      if (!r) return;
      e.preventDefault();
      const target = '#/' + r;
      // hash 相同不会触发 hashchange，这里手动刷新一次
      if (location.hash === target) route();
      else location.hash = target;
    });
  });
}

/* ---------------- 右侧栏拖拽排序 ---------------- */
function navItems() { return $$('#nav .nav-item'); }

// 启动页永远是右侧栏第一项
function firstNavRoute() {
  const a = navItems()[0];
  return (a && a.dataset.route) || 'home';
}

// 按保存的顺序重排；配置里没有的新选项保持默认顺序排在后面
function applyNavOrder(list) {
  const nav = $('#nav');
  if (!nav || !Array.isArray(list) || !list.length) return;
  const items = navItems();
  const byRoute = new Map(items.map(a => [a.dataset.route, a]));
  const seen = new Set();
  for (const r of list) {
    const a = byRoute.get(r);
    if (a && !seen.has(r)) { nav.appendChild(a); seen.add(r); }
  }
  for (const a of items) if (!seen.has(a.dataset.route)) nav.appendChild(a);
}

let navSaveTimer = null;
let navLastSaved = '';
function saveNavOrder() {
  const order = navItems().map(a => a.dataset.route);
  const key = order.join(',');
  if (key === navLastSaved) return;
  navLastSaved = key;
  clearTimeout(navSaveTimer);
  navSaveTimer = setTimeout(async () => {
    try {
      const r = await post('/api/config', { navOrder: order });
      if (r && r.ok) toast('侧栏顺序已保存', 'ok');
      else toast((r && r.error) || '侧栏顺序保存失败', 'err');
    } catch (e) {
      toast('侧栏顺序保存失败：' + (e.message || e), 'err');
    }
  }, 200);
}

// 鼠标按住拖动排序（不用 HTML5 DnD，WebView2 里更稳）
function bindNavDrag() {
  const nav = $('#nav');
  if (!nav || nav._dragBound) return;
  nav._dragBound = true;
  let drag = null;

  navItems().forEach(a => {
    // 关掉链接自带的原生拖拽，避免和自定义拖动打架
    a.setAttribute('draggable', 'false');
    a.addEventListener('mousedown', e => {
      if (e.button !== 0) return;
      drag = { el: a, y0: e.clientY, moved: false };
    });
  });

  document.addEventListener('mousemove', e => {
    if (!drag) return;
    if (!drag.moved) {
      if (Math.abs(e.clientY - drag.y0) < 4) return;
      drag.moved = true;
      drag.el.classList.add('dragging');
      document.body.classList.add('nav-dragging');
    }
    e.preventDefault();
    // 指针越过谁的中线就插到它前面/后面
    const others = navItems().filter(x => x !== drag.el);
    const next = others.find(x => {
      const r = x.getBoundingClientRect();
      return e.clientY < r.top + r.height / 2;
    });
    if (next) nav.insertBefore(drag.el, next);
    else nav.appendChild(drag.el);
  });

  document.addEventListener('mouseup', () => {
    if (!drag) return;
    const moved = drag.moved;
    drag.el.classList.remove('dragging');
    document.body.classList.remove('nav-dragging');
    drag = null;
    if (moved) { nav._suppressClick = true; saveNavOrder(); }
  });
}

/* ---------------- 百科（军队 / 文化 / 传统） ---------------- */
const WIKI_SITE = 'https://ck3.paradoxwikis.com/';
const WIKI_ST = { data: null, owners: null, tab: '', q: '', cat: '', area: '', tier: '', flag: '', grp: '',
  open: '', force: false, sort: 'type' };
const WK_LABEL = { damage: 'wiki.damage', toughness: 'wiki.toughness', pursuit: 'wiki.pursuit', screen: 'wiki.screen' };

/* 各游戏的 wiki 站点（用于「打开 wiki」按钮） */
function wikiSite() {
  const g = WIKI_ST.data && WIKI_ST.data.game;
  if (g === 'stellaris') return 'https://stellaris.paradoxwikis.com/';
  if (g === 'hoi4') return 'https://hoi4.paradoxwikis.com/';
  if (g === 'vic3') return 'https://vic3.paradoxwikis.com/';
  if (g === 'eu4') return 'https://eu4.paradoxwikis.com/';
  return WIKI_SITE;
}

/* 当前 tab 的元数据：后端下发，带 layout 决定用哪套渲染分支 */
function wkTab(id) {
  const tabs = (WIKI_ST.data && WIKI_ST.data.tabs) || [];
  const tid = id || WIKI_ST.tab;
  return tabs.find(t => t.id === tid) || { id: tid, layout: 'generic', label: tid, icon: '', count: 0 };
}
function wkLayout() { return wkTab().layout || 'generic'; }

/* tab 标题：群星由后端给中文/英文名；CK3 与维多利亚 3 走界面语言表 */
const WK_TAB_KEY = {
  maa: 'wiki.tabMaa', cultures: 'wiki.tabCultures', traditions: 'wiki.tabTraditions',
  religions: 'wiki.tabReligions', tenets: 'wiki.tabTenets', doctrines: 'wiki.tabDoctrines',
  innovations: 'wiki.tabInnovations', laws: 'wiki.tabLaws',
  igs: 'wiki.tabIGs', igtraits: 'wiki.tabIGTraits',
  ideas: 'wiki.tabIdeas', army: 'wiki.tabArmy', landunits: 'wiki.tabLandUnits',
  navalunits: 'wiki.tabNavalUnits', navy: 'wiki.tabNavy', ages: 'wiki.tabAges',
  missions: 'wiki.tabMissions',
};
function wkTabLabel(t) {
  if (WK_TAB_KEY[t.id]) return T(WK_TAB_KEY[t.id]);
  if (t.label) return t.label;
  return t.id;
}

/* 脚本键 → 可读英文标题（拿去 wiki 搜索用） */
function wikiTitle(key) {
  let k = String(key || '');
  /* 维多利亚 3 的 key 是 law_xxx / ig_xxx / ig_trait_xxx，wiki 上的条目名不带前缀 */
  if (WIKI_ST.data && WIKI_ST.data.game === 'vic3') {
    k = k.replace(/^law_/, '').replace(/^ig_trait_/, '').replace(/^ig_/, '');
  }
  return k.split('_').filter(Boolean)
    .map(w => w.charAt(0).toUpperCase() + w.slice(1)).join(' ');
}
function wikiGoto(kind, key) {
  const url = wikiSite() + 'index.php?search=' + encodeURIComponent(wikiTitle(key));
  post('/api/url', { url });
}
function wnum(v) { return (v === null || v === undefined || v === '') ? '—' : v; }
function wstat(label, val, unit) {
  return `<div class="wk-stat"><span class="wk-k">${esc(label)}</span>
    <span class="wk-v">${esc(String(val))}${unit ? `<small>${esc(unit)}</small>` : ''}</span></div>`;
}

/* 地形/冬季的增减值：正数绿色 +N，负数红色 -N */
function wmods(mods, compact) {
  const order = compact ? ['damage', 'toughness'] : ['damage', 'toughness', 'pursuit', 'screen'];
  const out = [];
  order.forEach(k => {
    const v = mods ? mods[k] : undefined;
    if (v === undefined || v === null || v === 0) return;
    const cls = v > 0 ? 'pos' : 'neg';
    out.push(`<span class="wk-m ${cls}">${compact ? '' : esc(T(WK_LABEL[k])) + ' '}${v > 0 ? '+' : ''}${v}</span>`);
  });
  return out.join('');
}

function wpillar(label, p, withDesc) {
  if (!p || !p.name) return '';
  return `<div class="wk-pillar"><span class="wk-k">${esc(label)}</span>
    <span class="wk-pv">${esc(p.name)}</span>
    ${(withDesc && p.desc) ? `<div class="wk-pd">${esc(p.desc)}</div>` : ''}</div>`;
}

async function renderWiki() {
  const view = $('#view');
  const head = `<div class="page-head"><div>
      <div class="page-title">${esc(T('wiki.title'))}</div>
      <div class="page-sub">${esc(T('wiki.sub'))}</div>
    </div></div>`;

  if (!WIKI_ST.data) {
    view.innerHTML = head + `<div class="card"><div class="wk-loading">
      <span class="wk-spin"></span>${esc(T('wiki.building'))}</div></div>`;
  }

  const prevGame = WIKI_ST.data && WIKI_ST.data.game;
  let d = null;
  try {
    d = await api('/api/wiki' + (WIKI_ST.force ? '?refresh=1' : ''));
  } catch (e) {
    view.innerHTML = head + `<div class="card"><div class="empty">${esc(T('wiki.loadFailed'))}
      <br><span style="color:var(--dim-2)">${esc(e.message)}</span></div></div>`;
    return;
  }
  WIKI_ST.force = false;

  if (!d.supported) {
    view.innerHTML = head + `<div class="card"><div class="empty">${esc(T('wiki.unsupported'))}</div></div>`;
    return;
  }
  if (!d.ok) {
    view.innerHTML = head + `<div class="card"><div class="empty">${esc(d.error || T('wiki.loadFailed'))}</div></div>`;
    return;
  }

  WIKI_ST.data = d;
  if (prevGame && prevGame !== d.game) {
    // 换游戏时清空国策/任务树画布缓存，避免 HOI4 与 EU4 的树 id 串用
    FX.cache = {}; FX.tree = ''; FX.data = null; FX.sel = ''; FX.q = '';
  }
  WIKI_ST.owners = null;
  WIKI_ST.open = '';
  // 换游戏后旧 tab 可能不存在，回落到第一个可用分类
  const tabs = d.tabs || [];
  if (!tabs.some(t => t.id === WIKI_ST.tab)) {
    WIKI_ST.tab = tabs.length ? tabs[0].id : 'maa';
    WIKI_ST.cat = '';
    WIKI_ST.area = ''; WIKI_ST.tier = ''; WIKI_ST.flag = ''; WIKI_ST.grp = '';
  }
  paintWiki();
}

function wikiOwners() {
  if (WIKI_ST.owners) return WIKI_ST.owners;
  const map = {};
  (WIKI_ST.data.cultures || []).forEach(c => (c.traditions || []).forEach(t => {
    (map[t.key] = map[t.key] || []).push(c.name);
  }));
  WIKI_ST.owners = map;
  return map;
}

function wikiItems() {
  const d = WIKI_ST.data, q = WIKI_ST.q.trim().toLowerCase(), cat = WIKI_ST.cat;
  const hit = s => !q || String(s || '').toLowerCase().includes(q);
  const layout = wkLayout();
  const tid = WIKI_ST.tab;

  if (layout === 'maa') {
    const arr = (d.maa || []).filter(m =>
      (!cat || m.type === cat) &&
      (hit(m.name) || hit(m.nameEn) || hit(m.key) || hit(m.typeName)));
    // 默认保持后端的「基础兵种 → 特殊兵种」分类顺序；其余按数值降序
    const key = { damage: 'damage', toughness: 'toughness', pursuit: 'pursuit', screen: 'screen' }[WIKI_ST.sort];
    if (!key) return arr;
    return arr.slice().sort((a, b) => (Number(b[key]) || 0) - (Number(a[key]) || 0) ||
      String(a.name).localeCompare(String(b.name), 'zh'));
  }
  if (layout === 'cultures') {
    return (d.cultures || []).filter(c =>
      (!cat || (c.heritage && c.heritage.key) === cat) &&
      (hit(c.name) || hit(c.key) || hit(c.ethos && c.ethos.name) || hit(c.heritage && c.heritage.name)));
  }
  if (layout === 'generic') {
    const arr = (d[wkListKey(tid)] || []).filter(x => {
      if (!cat) { /* 未选子分类 */ }
      else if (x.catKey !== cat && (x.cats || []).indexOf(cat) < 0) return false;
      if (tid === 'tech' && WIKI_ST.area && x.areaKey !== WIKI_ST.area) return false;
      if (tid === 'landunits' && WIKI_ST.grp && x.grpKey !== WIKI_ST.grp) return false;
      if (tid === 'tech') {
        if (WIKI_ST.tier !== '' && Number(x.tier) !== Number(WIKI_ST.tier)) return false;
        if (WIKI_ST.flag && !x[WIKI_ST.flag]) return false;
      }
      return hit(x.name) || hit(x.key) || hit(x.desc) || hit(x.cat) ||
        (x.effects || []).some(hit) || (x.links || []).some(hit) ||
        (x.attrs || []).some(a => hit(a.label) || hit(a.value)) ||
        (x.cats || []).some(hit);
    });
    const order = (d.catOrders || {})[tid] || [];
    if (order.length) {
      // 先按分类顺序分组，组内保持后端的脚本原始顺序
      // （传统要「开端 → 完成 → 其余」，不能被打成字母序）
      const idx = new Map(arr.map((x, i) => [x, i]));
      return arr.slice().sort((a, b) => {
        const ia = order.indexOf(a.catKey), ib = order.indexOf(b.catKey);
        return (ia < 0 ? 999 : ia) - (ib < 0 ? 999 : ib) || idx.get(a) - idx.get(b);
      });
    }
    return arr;
  }
  return (d.traditions || []).filter(t =>
    (!cat || t.cat === cat) &&
    (hit(t.name) || hit(t.key) || hit(t.desc) || (t.effects || []).some(hit)));
}

/* 列表字段名多数跟 tab id 同名，个别是驼峰（维3 的集团特质） */
function wkListKey(tid) {
  return tid === 'igtraits' ? 'igTraits' : tid;
}

function wikiFilters() {
  const d = WIKI_ST.data;
  const layout = wkLayout(), tid = WIKI_ST.tab;
  if (layout === 'maa') {
    return (d.typesInfo || []).map(t => ({ v: t.key, label: t.name, n: t.count }));
  }
  if (layout === 'cultures') {
    const seen = {};
    (d.cultures || []).forEach(c => {
      const h = c.heritage || {};
      if (!h.key) return;
      seen[h.key] = { v: h.key, label: h.name || h.key, n: (seen[h.key] ? seen[h.key].n : 0) + 1 };
    });
    return Object.values(seen).sort((a, b) => b.n - a.n);
  }
  if (tid === 'tech' && (d.techAreas || []).length) {
    // 领域 → 子分类：选中领域后只列该领域下的子分类
    const areas = d.techAreas || [];
    const cur = WIKI_ST.area ? areas.filter(a => a.key === WIKI_ST.area) : areas;
    const out = [];
    cur.forEach(a => (a.cats || []).forEach(c => out.push({ v: c.key, label: c.label, n: c.n })));
    return out;
  }
  if (layout === 'generic') {
    /* 维多利亚 3 的法律有一条整组的顺序（权力结构 → 经济 → 人权，组内按进步性），
       直接照后端给的组清单来，别按条数排 */
    if (tid === 'laws' && (d.lawGroups || []).length) {
      return d.lawGroups.map(g => ({
        v: g.key,
        label: (g.areaLabel ? g.areaLabel + ' · ' : '') + g.label,
        n: g.n,
      }));
    }
    /* 集团特质按所属集团分，顺序跟利益集团页一致（脚本里的 index） */
    if (tid === 'igtraits' && (d.igList || []).length) {
      return d.igList.filter(g => g.n).map(g => ({ v: g.key, label: g.label, n: g.n }));
    }
    /* 利益集团只有 8 条，不分档，不显示筛选条 */
    if (tid === 'igs') return [];
    const seen = {};
    (d[wkListKey(tid)] || []).forEach(x => {
      const k = x.catKey || '_';
      if (!seen[k]) seen[k] = { v: k, label: x.cat || T('wiki.all'), n: 0 };
      seen[k].n += 1;
    });
    const arr = Object.values(seen);
    const order = (d.catOrders || {})[tid] || [];
    if (order.length) {
      return arr.sort((a, b) => {
        const ia = order.indexOf(a.v), ib = order.indexOf(b.v);
        return (ia < 0 ? 999 : ia) - (ib < 0 ? 999 : ib) || b.n - a.n;
      });
    }
    return arr.sort((a, b) => b.n - a.n);
  }
  return (d.tradCats || []).map(c => ({
    v: c, label: c, n: (d.traditions || []).filter(t => t.cat === c).length,
  }));
}

/* 顶部基础克制闭环：环形图（节点 + 顺时针箭头） */
function wikiCycleCard() {
  // 后端已把轻骑兵 + 重骑兵合并成「轻/重骑兵」一组，直接用 5 节点闭环
  const chain = WIKI_ST.data.counterChain || [];
  if (chain.length < 5) return '';
  const nodes = [
    { x: 210, y: 46, t: chain[0].fromName },     // 散兵
    { x: 352, y: 116, t: chain[1].fromName },    // 重步兵
    { x: 296, y: 222, t: chain[2].fromName },    // 长枪兵
    { x: 124, y: 222, t: chain[3].fromName },    // 轻/重骑兵
    { x: 68, y: 116, t: chain[4].fromName },     // 弓箭手
  ];
  const r = 38;
  const edges = [];
  for (let i = 0; i < nodes.length; i++) {
    const a = nodes[i], b = nodes[(i + 1) % nodes.length];
    const dx = b.x - a.x, dy = b.y - a.y, len = Math.hypot(dx, dy) || 1;
    const ux = dx / len, uy = dy / len;
    edges.push(`<line x1="${(a.x + ux * r).toFixed(1)}" y1="${(a.y + uy * r).toFixed(1)}"
      x2="${(b.x - ux * (r + 9)).toFixed(1)}" y2="${(b.y - uy * (r + 9)).toFixed(1)}"
      class="wk-cyc-line" marker-end="url(#wkArr)"/>`);
  }
  return `<div class="card wk-cycle">
    <div class="card-title">⚔ ${esc(T('wiki.baseCounter'))}</div>
    <div class="wk-cycle-body">
      <svg class="wk-cycsv" viewBox="0 0 420 268" role="img" aria-label="${esc(T('wiki.baseCounter'))}">
        <defs><marker id="wkArr" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="6" markerHeight="6"
          orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" fill="var(--gold-b)"/></marker></defs>
        ${edges.join('')}
        ${nodes.map(n => {
          const parts = n.t.split(' / ');
          const txt = parts.length > 1
            ? `<text x="${n.x}" y="${n.y - 3}" text-anchor="middle" class="wk-cyc-text small">
                 ${parts.map((p, i) => `<tspan x="${n.x}" dy="${i === 0 ? 0 : 15}">${esc(p)}</tspan>`).join('')}</text>`
            : `<text x="${n.x}" y="${n.y + 4}" text-anchor="middle" class="wk-cyc-text${n.t.length > 4 ? ' small' : ''}">${esc(n.t)}</text>`;
          return `<g><circle cx="${n.x}" cy="${n.y}" r="${r}" class="wk-cyc-circle"/>${txt}</g>`;
        }).join('')}
      </svg>
      <div class="wk-cycle-note">
        <div class="wk-hint">${esc(T('wiki.baseCounterHint'))}</div>
        <div class="wk-cyc-legend">
          ${chain.map(c => `<div><b>${esc(c.fromName)}</b> ➜ ${esc(c.toName)}</div>`).join('')}
        </div>
      </div>
    </div>
  </div>`;
}

/* 进步性刻度：0 在中点，负值向左（保守）、正值向右（进步），满格 ±100 */
function wikiProg(v) {
  const p = Math.max(-100, Math.min(100, v | 0));
  const half = Math.abs(p) / 2;
  const fill = p < 0
    ? `<i class="neg" style="right:50%;width:${half}%"></i>`
    : `<i class="pos" style="left:50%;width:${half}%"></i>`;
  return `<div class="wk-prog">
    <span class="wk-prog-k">${esc(T('wiki.prog'))}</span>
    <span class="wk-prog-track"><span class="wk-prog-mid"></span>${fill}</span>
    <span class="wk-prog-v">${esc(wikiProgLabel(p))}</span>
  </div>`;
}
function wikiProgLabel(v) {
  if (v <= -60) return T('wiki.progVeryCons') + ' ' + v;
  if (v < 0) return T('wiki.progCons') + ' ' + v;
  if (v === 0) return T('wiki.progNeutral');
  if (v < 60) return T('wiki.progLib') + ' +' + v;
  return T('wiki.progVeryLib') + ' +' + v;
}

/* 附加区块：后端按 kind 下发，前端只管画
   list = 文字列表、chips = 标签、tiers = 分档列表、stances = 意识形态立场（按档位分行） */
function wikiSecs(secs) {
  if (!secs || !secs.length) return '';
  return secs.map(s => {
    const n = s.n || (s.items ? s.items.length : 0);
    let body = '';
    if (s.kind === 'chips') {
      body = `<div class="wk-chips">${(s.items || []).map(x =>
        `<span class="wk-chip ${s.bad ? 'bad' : ''}">${esc(x)}</span>`).join('')}</div>`;
    } else if (s.kind === 'tiers') {
      body = `<div class="wk-tiers">${(s.items || []).map(t => `<div class="wk-tier">
        <span class="wk-tier-k">${esc(t.label)}</span>
        <ul class="wk-eff tight">${(t.effects || []).map(e => `<li>${esc(e)}</li>`).join('')}</ul>
      </div>`).join('')}</div>`;
    } else if (s.kind === 'stances') {
      body = `<div class="wk-stances">${(s.groups || []).map(g => `<div class="wk-stance st${g.v}">
        <span class="wk-stance-k">${esc(g.label)}<b>${g.n}</b></span>
        <div class="wk-chips">${(g.items || []).map(x => {
          /* 同名意识形态来自不同文件时会合并成一条，来源与「沿用父法」放进 title */
          const tip = [];
          if (x.inh) tip.push(T('wiki.stanceInherit'));
          if (x.dup > 1 && (x.srcs || []).length) tip.push(x.srcs.join(' / '));
          return `<span class="wk-chip"${tip.length ? ` title="${esc(tip.join(' · '))}"` : ''}>${esc(x.name)}</span>`;
        }).join('')}</div>
      </div>`).join('')}</div>`;
    } else {
      body = `<ul class="wk-eff">${(s.items || []).map(e => `<li>${esc(e)}</li>`).join('')}</ul>`;
    }
    return `<div class="wk-sec"><div class="wk-h">${esc(s.label)}${n ? ` (${n})` : ''}</div>
      ${s.hint ? `<div class="wk-hint">${esc(s.hint)}</div>` : ''}${body}</div>`;
  }).join('');
}

function wikiRow(it) {
  const open = WIKI_ST.open === it.key;
  const layout = wkLayout();
  let line = '', detail = '';

  if (layout === 'maa') {
    const ct = (it.counters || []).slice(0, 3).map(c => c.name).join('、');
    line = `<span class="wk-name">${esc(it.name)}<small>${esc(it.nameEn || '')}</small></span>
      <span class="wk-tag">${esc(it.typeName || it.type)}</span>
      <span class="wk-mini">${esc(T('wiki.damage'))} ${wnum(it.damage)} · ${esc(T('wiki.toughness'))} ${wnum(it.toughness)} · ${esc(T('wiki.stack'))} ${wnum(it.stack)} · ${esc(T('wiki.cost'))} ${wnum(it.cost)}</span>
      ${ct ? `<span class="wk-ctr-mini">${esc(T('wiki.counterShort'))} ${esc(ct)}</span>` : ''}`;
    if (open) {
      detail = `${it.flavor ? `<div class="wk-flavor">${esc(it.flavor)}</div>` : ''}
        <div class="wk-stats">
          ${wstat(T('wiki.damage'), wnum(it.damage))}
          ${wstat(T('wiki.toughness'), wnum(it.toughness))}
          ${wstat(T('wiki.pursuit'), wnum(it.pursuit))}
          ${wstat(T('wiki.screen'), wnum(it.screen))}
          ${wstat(T('wiki.stack'), wnum(it.stack))}
          ${wstat(T('wiki.siege'), wnum(it.siege))}
          ${wstat(T('wiki.provision'), wnum(it.provision))}
          ${wstat(T('wiki.cost'), wnum(it.cost))}
          ${wstat(T('wiki.maintLow'), wnum(it.maintLow))}
          ${wstat(T('wiki.maintHigh'), wnum(it.maintHigh))}
        </div>
        ${(it.recruit || []).length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.recruitReq'))}</div>
          <ul class="wk-eff">${it.recruit.map(r => `<li>${esc(r)}</li>`).join('')}</ul></div>` : ''}
        ${(it.counters || []).length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.countersTo'))}</div>
          <div class="wk-chips">${it.counters.map(c => `<span class="wk-chip bad">${esc(c.name)} <i>×${wnum(c.mult)}</i></span>`).join('')}</div>
        </div>` : ''}
        ${(it.counteredBy || []).length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.counteredBy'))}</div>
          <div class="wk-chips">${it.counteredBy.map(c => `<span class="wk-chip">${esc(c.name)}</span>`).join('')}</div>
        </div>` : ''}
        ${(it.terrain || []).length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.terrain'))}</div>
          <div class="wk-chips">${it.terrain.map(t => `<span class="wk-chip">${esc(t.name)} ${wmods(t.mods)}</span>`).join('')}</div>
        </div>` : ''}
        ${(it.winter || []).length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.winter'))}</div>
          <div class="wk-chips">${it.winter.map(t => `<span class="wk-chip">${esc(t.name)} ${wmods(t.mods)}</span>`).join('')}</div>
        </div>` : ''}
        <div class="wk-foot">
          <span class="wk-src">${esc(T('wiki.source'))}: ${esc(it.src)}</span>
          <button class="btn sm ghost" data-wiki="${esc(it.key)}">${esc(T('wiki.openWiki'))}</button>
        </div>`;
    }
  } else if (layout === 'cultures') {
    line = `<span class="wk-name">${esc(it.name)}</span>
      <span class="wk-tag">${esc((it.heritage || {}).name || '—')}</span>
      <span class="wk-mini">${esc((it.ethos || {}).name || '')} · ${esc(T('wiki.tradCount', { n: (it.traditions || []).length }))}</span>`;
    if (open) {
      const trs = it.traditions || [];
      detail = `<div class="wk-pillars">
          ${wpillar(T('wiki.ethos'), it.ethos, false)}
          ${wpillar(T('wiki.heritage'), it.heritage, true)}
          ${wpillar(T('wiki.language'), it.language, true)}
        </div>
        ${trs.length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.traditionsOf'))} (${trs.length})</div>
          <table class="wk-trad-table">
            <tr><th>${esc(T('wiki.tradName'))}</th><th>${esc(T('wiki.tradDesc'))}</th><th>${esc(T('wiki.effects'))}</th></tr>
            ${trs.map(t => `<tr>
              <td><b>${esc(t.name)}</b>${t.dlc ? ` <em class="wk-dlc">${esc(T('wiki.dlcTag'))}</em>` : ''}</td>
              <td>${esc(t.desc || '—')}</td>
              <td>${(t.effects || []).length
                ? `<ul class="wk-eff tight">${t.effects.map(e => `<li>${esc(e)}</li>`).join('')}</ul>`
                : '<span class="wk-pd">—</span>'}</td></tr>`).join('')}
          </table>
        </div>` : ''}
        ${(it.parents || []).length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.parents'))}</div>
          <div class="wk-chips">${it.parents.map(p => `<span class="wk-chip">${esc(p.name)}</span>`).join('')}</div>
        </div>` : ''}
        <div class="wk-foot">
          <button class="btn sm ghost" data-wiki="${esc(it.key)}">${esc(T('wiki.openWiki'))}</button>
        </div>`;
    }
  } else if (layout === 'generic') {
    /* 宗教 / 信条 / 教义 / 革新（CK3）、群星十类、钢铁雄心理念与顾问、维多利亚 3 法律：
       统一条目结构，靠各自的附加字段（icon / prog / secs）决定要不要多画点东西 */
    const ef = it.effects || [], links = it.links || [], attrs = it.attrs || [];
    /* 信条分两个槽位：核心信条（全信徒）与个人教义（只对本人），效果要分开展示 */
    const isTenet = WIKI_ST.tab === 'tenets';
    const effTitle = isTenet ? T('wiki.coreEffects') : T('wiki.effects');
    const pf = it.personal || [];
    /* 群星的名字本身就是中文，再挂一行英文字号反而挤；CK3 保留英文副标题便于对照 wiki */
    const isSl = (WIKI_ST.data && ['stellaris', 'hoi4', 'vic3', 'eu4'].includes(WIKI_ST.data.game));
    /* 维多利亚 3 的图标：DDS 由后端转 PNG，按需懒加载；利益集团/特质/法律分属不同子目录 */
    const ico = it.icon
      ? `<span class="wk-ico"><img loading="lazy" src="/api/wiki/law-icon?n=${encodeURIComponent(it.icon)}${it.iconKind ? '&k=' + it.iconKind : ''}" alt=""
           onerror="this.parentNode.classList.add('wk-ico-na');this.remove()"></span>`
      : '';
    /* 利益集团有主题色（脚本里的 hsv），用一个色点看着比灰图标直观 */
    const dot = it.color
      ? `<i class="wk-ig-dot" style="background:${esc(it.color)}"></i>` : '';
    line = `${ico}${dot}<span class="wk-name">${esc(it.name)}${isSl ? '' : `<small>${esc(wikiTitle(it.key))}</small>`}</span>
      <span class="wk-tag">${esc(it.cat || T('wiki.all'))}</span>
      <span class="wk-mini wk-ellipsis">${esc(it.desc || (ef[0] || ''))}</span>`;
    if (open) {
      detail = `${typeof it.prog === 'number' ? wikiProg(it.prog) : ''}
        ${attrs.length ? `<div class="wk-stats">
          ${attrs.map(a => wstat(esc(a.label), esc(a.value))).join('')}
        </div>` : ''}
        ${it.desc ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.flavor'))}</div>
          <div class="wk-desc">${esc(it.desc)}</div></div>` : ''}
        ${ef.length ? `<div class="wk-sec"><div class="wk-h">${esc(effTitle)} (${ef.length})</div>
          <ul class="wk-eff">${ef.map(e => `<li>${esc(e)}</li>`).join('')}</ul></div>`
          : (it.secs && it.secs.length ? '' : `<div class="wk-sec"><div class="wk-h">${esc(effTitle)}</div>
          <div class="wk-pd">${esc(T('wiki.noEffects'))}</div></div>`)}
        ${wikiSecs(it.secs)}
        ${pf.length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.personalEffects'))} (${pf.length})</div>
          <ul class="wk-eff">${pf.map(e => `<li>${esc(e)}</li>`).join('')}</ul></div>`
          : (it.personalSameAsCore ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.personalEffects'))}</div>
          <div class="wk-pd">${esc(T('wiki.personalSameAsCore'))}</div></div>` : '')}
        ${(it.variants || []).length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.variants'))} (${it.variants.length})</div>
          <div class="wk-var-note">${esc(T('wiki.variantsHint'))}</div>
          ${it.variants.map(v => `<div class="wk-var">
            <div class="wk-var-top"><b>${esc(v.name || it.name)}</b>
              ${(v.cond || []).length ? `<span class="wk-var-cond">${esc(v.cond.join(' / '))}</span>` : ''}</div>
            ${v.desc ? `<div class="wk-var-desc">${esc(v.desc)}</div>` : ''}
            ${(v.effects || []).length
              ? `<ul class="wk-eff tight">${v.effects.map(e => `<li>${esc(e)}</li>`).join('')}</ul>`
              : (v.inheritEffects && !v.desc ? `<div class="wk-pd">${esc(T('wiki.variantInherit'))}</div>` : '')}
          </div>`).join('')}
        </div>` : ''}
        ${links.length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.linked'))} (${links.length})</div>
          <div class="wk-chips">${links.map(n => `<span class="wk-chip">${esc(n)}</span>`).join('')}</div>
        </div>` : ''}
        <div class="wk-foot">
          <span class="wk-src">${esc(it.cat || '')}</span>
          <button class="btn sm ghost" data-wiki="${esc(it.key)}">${esc(T('wiki.openWiki'))}</button>
        </div>`;
    }
  } else {
    const ef = it.effects || [];
    line = `<span class="wk-name">${esc(it.name)}</span>
      <span class="wk-tag">${esc(it.cat)}</span>
      <span class="wk-mini wk-ellipsis">${esc(it.desc || '')}</span>`;
    if (open) {
      const own = wikiOwners()[it.key] || [];
      detail = `${ef.length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.effects'))}</div>
          <ul class="wk-eff">${ef.map(e => `<li>${esc(e)}</li>`).join('')}</ul>
        </div>` : `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.effects'))}</div>
          <div class="wk-pd">${esc(T('wiki.noEffects'))}</div></div>`}
        <div class="wk-sec"><div class="wk-h">${esc(T('wiki.flavor'))}</div>
          <div class="wk-desc">${esc(it.desc || '—')}</div>
        </div>
        ${own.length ? `<div class="wk-sec"><div class="wk-h">${esc(T('wiki.usedBy'))} (${own.length})</div>
          <div class="wk-chips">${own.map(n => `<span class="wk-chip">${esc(n)}</span>`).join('')}</div>
        </div>` : ''}
        <div class="wk-foot">
          <button class="btn sm ghost" data-wiki="${esc(it.key)}">${esc(T('wiki.openWiki'))}</button>
        </div>`;
    }
  }
  return `<div class="wk-row ${open ? 'open' : ''}" data-k="${esc(it.key)}">
    <div class="wk-line">${line}<span class="wk-arrow">▾</span></div>
    ${open ? `<div class="wk-detail">${detail}</div>` : ''}
  </div>`;
}


/* ---------------- 国策树画布（钢铁雄心） ----------------
   坐标来自脚本的网格：x 每格 96px、y 每格 132px，和游戏里的排布比例一致。
   节点是普通 DOM（带 transform 缩放），连线是同一容器里的 SVG，
   这样滚轮缩放 / 拖动平移都是纯 CSS 变换，几百个节点也不掉帧。 */
const FX = {
  tree: '', data: null, cache: {}, scale: 1, tx: 0, ty: 0, sel: '', q: '',
  io: null, bx: 0, by: 0, px: 0, py: 0, drag: false,
};
function fxIsEu4() { return !!(WIKI_ST.data && WIKI_ST.data.game === 'eu4'); }
const FX_X = 96, FX_Y = 132, FX_W = 88, FX_ICON = 64;

function fxTreeList(d) {
  const ts = ((d.game === 'eu4' ? d.missionTrees : d.focusTrees) || []).slice();
  // 通用国策树放最前面，其余按名字排（中文用 localeCompare）
  ts.sort((a, b) => (b.default ? 1 : 0) - (a.default ? 1 : 0) ||
    String(a.name).localeCompare(String(b.name), 'zh'));
  return ts;
}

function fxTabsHtml(tabs) {
  return `<div class="wk-tabs">${tabs.map(t =>
    `<button class="wk-tab ${WIKI_ST.tab === t.id ? 'active' : ''}" data-tab="${t.id}">
      <i>${t.icon || ''}</i><span>${esc(wkTabLabel(t))}</span><b>${t.n}</b></button>`).join('')}</div>`;
}

async function paintFocus(tabs) {
  const d = WIKI_ST.data;
  const list = fxTreeList(d);
  if (!list.length) {
    $('#view').innerHTML = `<div class="page-head"><div>
        <div class="page-title">${esc(T('wiki.title'))}</div>
        <div class="page-sub">${esc(T('wiki.sub'))}</div></div></div>
      ${fxTabsHtml(tabs)}
      <div class="card"><div class="empty">${esc(T('wiki.fxEmpty'))}</div></div>`;
    bindFxTabs();
    return;
  }
  if (!FX.tree || !list.some(t => t.id === FX.tree)) FX.tree = list[0].id;

  $('#view').innerHTML = `
    <div class="page-head"><div>
      <div class="page-title">${esc(T('wiki.title'))}</div>
      <div class="page-sub">${esc(T('wiki.sub'))}</div>
    </div><div class="bar">
      <button class="btn sm ghost" id="wkRefresh" title="${esc(T('wiki.refreshHint'))}">⟳ ${esc(T('wiki.refresh'))}</button>
    </div></div>

    ${fxTabsHtml(tabs)}

    <div class="fx-bar">
      <select class="inp wk-sel" id="fxTree">${list.map(t =>
        `<option value="${esc(t.id)}" ${FX.tree === t.id ? 'selected' : ''}>${esc(t.name)}（${t.n}）</option>`).join('')}</select>
      <input class="inp wk-search" id="fxQ" value="${esc(FX.q)}" placeholder="${esc(T(fxIsEu4() ? 'wiki.msSearch' : 'wiki.fxSearch'))}">
      <span class="fx-sp"></span>
      <button class="fx-btn" id="fxOut" title="${esc(T('wiki.fxZoomOut'))}">−</button>
      <span class="fx-zoom" id="fxZoom">100%</span>
      <button class="fx-btn" id="fxIn" title="${esc(T('wiki.fxZoomIn'))}">+</button>
      <button class="fx-btn" id="fxFit">${esc(T('wiki.fxFit'))}</button>
    </div>

    <div class="fx-stage" id="fxStage">
      <div class="fx-world" id="fxWorld">
        <svg class="fx-links" id="fxLinks"></svg>
        <div id="fxNodes"></div>
      </div>
    </div>
    <div class="fx-detail" id="fxDetail"><div class="fx-pd">${esc(T(fxIsEu4() ? 'wiki.msPick' : 'wiki.fxPick'))}</div></div>`;

  bindFxTabs();
  $('#wkRefresh').onclick = async () => { WIKI_ST.force = true; FX.cache = {}; await renderWiki(); };

  const sel = $('#fxTree');
  sel.onchange = () => { FX.tree = sel.value; FX.sel = ''; FX.q = ''; fxLoad(sel.value); };
  const q = $('#fxQ');
  q.oninput = () => { FX.q = q.value.trim().toLowerCase(); fxMark(); };
  $('#fxIn').onclick = () => fxZoom(1.25);
  $('#fxOut').onclick = () => fxZoom(0.8);
  $('#fxFit').onclick = () => fxFit();

  const stage = $('#fxStage');
  stage.addEventListener('mousedown', e => {
    if (e.target.closest('.fx-node')) return;
    FX.drag = true; FX.bx = e.clientX; FX.by = e.clientY;
    FX.px = FX.tx; FX.py = FX.ty;
    stage.classList.add('drag');
  });
  window.addEventListener('mousemove', fxMove);
  window.addEventListener('mouseup', fxUp);
  stage.addEventListener('wheel', e => {
    e.preventDefault();
    const r = stage.getBoundingClientRect();
    const mx = e.clientX - r.left, my = e.clientY - r.top;
    const k = e.deltaY < 0 ? 1.25 : 0.8;
    const ns = Math.min(3, Math.max(0.1, FX.scale * k));
    const f = ns / FX.scale;
    FX.tx = mx - (mx - FX.tx) * f;
    FX.ty = my - (my - FX.ty) * f;
    FX.scale = ns;
    fxApply();
  }, { passive: false });

  await fxLoad(FX.tree);
}

function bindFxTabs() {
  $$('.wk-tab').forEach(b => b.onclick = () => {
    WIKI_ST.tab = b.dataset.tab; WIKI_ST.cat = ''; WIKI_ST.open = '';
    WIKI_ST.area = ''; WIKI_ST.tier = ''; WIKI_ST.flag = ''; WIKI_ST.grp = '';
    paintWiki();
  });
}

function fxMove(e) {
  if (!FX.drag) return;
  FX.tx = FX.px + (e.clientX - FX.bx);
  FX.ty = FX.py + (e.clientY - FX.by);
  fxApply();
}
function fxUp() {
  if (!FX.drag) return;
  FX.drag = false;
  const st = $('#fxStage');
  if (st) st.classList.remove('drag');
}

function fxApply() {
  const w = $('#fxWorld');
  if (!w) return;
  w.style.transform = `translate(${FX.tx}px,${FX.ty}px) scale(${FX.scale})`;
  const z = $('#fxZoom');
  if (z) z.textContent = Math.round(FX.scale * 100) + '%';
  fxIcons();
}

function fxZoom(k) {
  const stage = $('#fxStage');
  if (!stage) return;
  const r = stage.getBoundingClientRect();
  const mx = r.width / 2, my = r.height / 2;
  const ns = Math.min(2.6, Math.max(0.2, FX.scale * k));
  const f = ns / FX.scale;
  FX.tx = mx - (mx - FX.tx) * f;
  FX.ty = my - (my - FX.ty) * f;
  FX.scale = ns;
  fxApply();
}

function fxHome() {
  const t = FX.data, stage = $('#fxStage');
  if (!t || !stage) return;
  const r = stage.getBoundingClientRect();
  const W = (t.x1 - t.x0 + 1) * FX_X;
  FX.scale = Math.min(1, Math.max(0.35, Math.min(0.7, (r.width / W) * 2.2)));
  FX.tx = (r.width - W * FX.scale) / 2;
  FX.ty = 16;
  fxApply();
}

function fxFit() {
  const t = FX.data, stage = $('#fxStage');
  if (!t || !stage) return;
  const r = stage.getBoundingClientRect();
  const W = (t.x1 - t.x0 + 1) * FX_X, H = (t.y1 - t.y0 + 1) * FX_Y;
  const s = Math.min(1.4, Math.max(0.08, Math.min((r.width - 40) / W, (r.height - 40) / H)));
  FX.scale = s;
  FX.tx = (r.width - W * s) / 2;
  FX.ty = (r.height - H * s) / 2;
  fxApply();
}

async function fxLoad(tid) {
  const stage = $('#fxStage');
  if (stage && !FX.data) stage.innerHTML = `<div class="fx-empty">${esc(T('wiki.building'))}</div>`;
  let t = FX.cache[tid];
  if (!t) {
    try {
      t = await api((fxIsEu4() ? '/api/wiki/mission?tree=' : '/api/wiki/focus?tree=') + encodeURIComponent(tid));
    } catch (e) {
      t = null;
    }
    if (!t || !t.ok) {
      if (stage) stage.innerHTML = `<div class="fx-empty">${esc(T('wiki.fxLoadFail'))}</div>`;
      return;
    }
    FX.cache[tid] = t;
  }
  FX.data = t;
  FX.sel = '';
  fxDraw(t);
  fxHome();
  const det = $('#fxDetail');
  if (det) det.innerHTML = `<div class="fx-pd">${esc(T(fxIsEu4() ? 'wiki.msPick' : 'wiki.fxPick'))}</div>`;
}

function fxDraw(t) {
  const stage = $('#fxStage');
  if (!stage) return;
  stage.innerHTML = `<div class="fx-world" id="fxWorld">
      <svg class="fx-links" id="fxLinks"></svg><div id="fxNodes"></div></div>`;
  const W = (t.x1 - t.x0 + 1) * FX_X, H = (t.y1 - t.y0 + 1) * FX_Y;
  const world = $('#fxWorld');
  world.style.width = W + 'px';
  world.style.height = H + 'px';

  const pos = {};
  (t.focuses || []).forEach(n => {
    pos[n.id] = {
      x: (n.x - t.x0) * FX_X + (FX_X - FX_W) / 2,
      y: (n.y - t.y0) * FX_Y,
    };
  });

  // 连线：前置（实线）+ 互斥（红色虚线）
  const paths = [];
  (t.focuses || []).forEach(n => {
    const c = pos[n.id];
    if (!c) return;
    (n.pre || []).forEach(pid => {
      const p = pos[pid];
      if (!p) return;
      const x1 = p.x + FX_W / 2, y1 = p.y + FX_ICON + 6;
      const x2 = c.x + FX_W / 2, y2 = c.y;
      paths.push(`<path d="M${x1} ${y1} L${x1} ${y1 + 26} L${x2} ${y2 - 26} L${x2} ${y2}"/>`);
    });
    (n.mutex || []).forEach(mid => {
      const p = pos[mid];
      if (!p) return;
      if (mid < n.id) return;   // 互斥是双向的，只画一次
      const x1 = p.x + FX_W / 2, y1 = p.y + FX_ICON + 6;
      const x2 = c.x + FX_W / 2, y2 = c.y + FX_ICON + 6;
      paths.push(`<path class="mx" d="M${x1} ${y1} L${x2} ${y2}"/>`);
    });
  });
  $('#fxLinks').innerHTML = paths.join('');

  $('#fxNodes').innerHTML = (t.focuses || []).map(n => {
    const p = pos[n.id];
    return `<div class="fx-node" data-id="${esc(n.id)}" style="left:${p.x}px;top:${p.y}px">
      <div class="fx-ic">${n.icon
        ? `<img alt="" data-src="${fxIsEu4() ? '/api/wiki/mission-icon?n=' : '/api/wiki/focus-icon?n='}${encodeURIComponent(n.icon)}">`
        : `<span class="fx-no">◆</span>`}</div>
      <span class="fx-nm">${esc(n.nm)}</span></div>`;
  }).join('');

  // 图标懒加载：438 个节点一次性请 DDS 转 PNG 会把后端点爆，只加载进视口的。
  // 这里不用 IntersectionObserver —— 节点在 transform 容器里，缩放/平移后
  // 可见性判断不可靠，直接按屏幕坐标自己算。
  const imgs = $$('#fxNodes img');
  imgs.forEach(im => {
    im.onerror = () => {
      const box = im.parentNode;
      im.remove();
      if (box && !box.querySelector('.fx-no')) box.innerHTML = '<span class="fx-no">◆</span>';
    };
  });
  fxIcons(true);

  $$('#fxNodes .fx-node').forEach(el => {
    el.onclick = () => {
      FX.sel = el.dataset.id;
      $$('#fxNodes .fx-node').forEach(x => x.classList.toggle('sel', x === el));
      fxDetail(FX.sel);
    };
    el.ondblclick = () => fxCenter(el.dataset.id);
  });
  if (FX.q) fxMark();
}

let _fxIconTimer = 0;
function fxIcons(now) {
  const stage = $('#fxStage');
  if (!stage) return;
  const run = () => {
    const r = stage.getBoundingClientRect();
    const pad = 80;
    $$('#fxNodes .fx-node').forEach(el => {
      const im = el.querySelector('img');
      if (!im || im.src || !im.dataset.src) return;
      const b = el.getBoundingClientRect();
      if (b.right < r.left - pad || b.left > r.right + pad ||
          b.bottom < r.top - pad || b.top > r.bottom + pad) return;
      im.src = im.dataset.src;
    });
  };
  if (now) { run(); return; }
  clearTimeout(_fxIconTimer);
  _fxIconTimer = setTimeout(run, 120);
}

function fxCenter(id) {
  const t = FX.data, p = (t.focuses || []).find(n => n.id === id);
  const stage = $('#fxStage');
  if (!p || !stage) return;
  const r = stage.getBoundingClientRect();
  const cx = (p.x - t.x0) * FX_X + FX_W / 2;
  const cy = (p.y - t.y0) * FX_Y + FX_ICON / 2;
  FX.tx = r.width / 2 - cx * FX.scale;
  FX.ty = r.height / 2 - cy * FX.scale;
  fxApply();
}

function fxMark() {
  const q = FX.q;
  let first = null;
  $$('#fxNodes .fx-node').forEach(el => {
    const n = (FX.data.focuses || []).find(x => x.id === el.dataset.id);
    if (!n || !q) { el.classList.remove('hit', 'dim'); return; }
    const hit = String(n.nm || '').toLowerCase().includes(q) ||
      String(n.id).toLowerCase().includes(q) ||
      String(n.ds || '').toLowerCase().includes(q) ||
      String(n.cond || '').toLowerCase().includes(q) ||
      String((n.pre || []).join(' ')).toLowerCase().includes(q) ||
      (n.fx || []).some(x => String(x).toLowerCase().includes(q));
    el.classList.toggle('hit', hit);
    el.classList.toggle('dim', !hit);
    if (hit && !first) first = el;
  });
  if (first) fxCenter(first.dataset.id);
}

function fxDetail(id) {
  const t = FX.data;
  const n = (t.focuses || []).find(x => x.id === id);
  const box = $('#fxDetail');
  if (!n || !box) return;
  const byId = {};
  (t.focuses || []).forEach(x => { byId[x.id] = x; });
  const nm = pid => (byId[pid] || {}).nm || pid;
  const pre = (n.pre || []).map(nm);
  const mutex = (n.mutex || []).map(nm);
  const meta = [];
  meta.push(`${esc(T('wiki.fxCost'))} ${esc(n.cost || '—')}`);
  if (n.bypass) meta.push(esc(T('wiki.fxBypass')));
  (n.filters || []).filter(Boolean).forEach(f => meta.push(esc(f)));
  if (fxIsEu4()) {
    const cond = n.cond || [];
    box.innerHTML = `
      <div class="fx-dh"><b>${esc(n.nm)}</b><small>${esc(n.id)}</small></div>
      ${n.ds ? `<div class="fx-desc">${esc(n.ds)}</div>` : ''}
      <div class="fx-h">${esc(T('wiki.msCond'))}</div>
      ${cond.length ? `<ul class="fx-eff">${cond.map(x => `<li>${esc(x)}</li>`).join('')}</ul>`
        : `<div class="fx-pd">${esc(T('wiki.noEffects'))}</div>`}
      ${pre.length ? `<div class="fx-h">${esc(T('wiki.msPre'))}</div>
        <div class="fx-meta">${pre.map(x => `<span>${esc(x)}</span>`).join('')}</div>` : ''}
      <div class="fx-h">${esc(T('wiki.msReward'))}</div>
      ${(n.fx || []).length
        ? `<ul class="fx-eff">${n.fx.map(x => `<li>${esc(x)}</li>`).join('')}</ul>`
        : `<div class="fx-pd">${esc(T('wiki.noEffects'))}</div>`}`;
    return;
  }
  box.innerHTML = `
    <div class="fx-dh"><b>${esc(n.nm)}</b><small>${esc(n.id)}</small></div>
    ${n.ds ? `<div class="fx-desc">${esc(n.ds)}</div>` : ''}
    <div class="fx-meta">${meta.map(m => `<span>${m}</span>`).join('')}</div>
    ${pre.length ? `<div class="fx-h">${esc(T('wiki.fxPre'))}</div>
      <div class="fx-meta">${pre.map(x => `<span>${esc(x)}</span>`).join('')}</div>` : ''}
    ${mutex.length ? `<div class="fx-h">${esc(T('wiki.fxMutex'))}</div>
      <div class="fx-meta">${mutex.map(x => `<span>${esc(x)}</span>`).join('')}</div>` : ''}
    <div class="fx-h">${esc(T('wiki.effects'))}</div>
    ${(n.fx || []).length
      ? `<ul class="fx-eff">${n.fx.map(x => `<li>${esc(x)}</li>`).join('')}</ul>`
      : `<div class="fx-pd">${esc(T('wiki.noEffects'))}</div>`}`;
}

function paintWiki() {
  const d = WIKI_ST.data;
  const isCk3 = d.game !== 'stellaris';
  // tab 由后端下发（CK3 七类 / 群星十类），前端只负责按 layout 选渲染分支
  const tabs = (d.tabs || []).map(t => ({
    id: t.id, icon: t.icon || '', label: wkTabLabel(t), n: t.count,
  }));
  const layout = wkLayout();
  if (layout === 'focus') { paintFocus(tabs); return; }
  const items = wikiItems();
  const wf = wikiFilters();
  /* 只有一个「全部」时不用摆筛选条（维3 的利益集团就 8 条，不分档） */
  const cats = wf.length ? [{ v: '', label: T('wiki.all'), n: null }].concat(wf) : [];
  const sortOpts = [
    { v: 'type', label: T('wiki.sortType') },
    { v: 'damage', label: T('wiki.sortDamage') },
    { v: 'toughness', label: T('wiki.sortToughness') },
    { v: 'pursuit', label: T('wiki.sortPursuit') },
    { v: 'screen', label: T('wiki.sortScreen') },
  ];
  // 分类太多了（教义有 49 组、群星科技 3 类无妨）就换成下拉，别把胶囊铺满屏
  const useSelect = cats.length > 14;
  // 群星科技：领域（物理/社会/工程）→ 子分类（生物学/计算技术…）→ 层级 → 危险/稀有/循环
  const isTech = !isCk3 && WIKI_ST.tab === 'tech' && (d.techAreas || []).length > 0;
  const areaBar = isTech ? `
    <div class="wk-cats wk-areabar">
      ${[{ key: '', label: T('wiki.all'), n: null }].concat(
        (d.techAreas || []).map(a => ({ key: a.key, label: a.label, n: a.n }))).map(a =>
        `<button class="wk-cat ${WIKI_ST.area === a.key ? 'active' : ''}" data-area="${esc(a.key)}">
          ${esc(a.label)}${a.n !== null ? `<b>${a.n}</b>` : ''}</button>`).join('')}
    </div>
    <div class="wk-cats wk-tierbar">
      ${[{ v: '', label: T('wiki.allTier'), n: null }].concat(
        (d.techTiers || []).map(t => ({ v: String(t.v), label: t.v
          ? T('wiki.tierN', { n: t.v }) : T('wiki.tierSpecial'), n: t.n }))).map(t =>
        `<button class="wk-cat wk-cat-sm ${WIKI_ST.tier === t.v ? 'active' : ''}" data-tier="${esc(t.v)}">
          ${esc(t.label)}${t.n !== null ? `<b>${t.n}</b>` : ''}</button>`).join('')}
      ${(d.techFlags || []).map(f =>
        `<button class="wk-cat wk-cat-sm ${WIKI_ST.flag === f.key ? 'active' : ''}" data-flag="${esc(f.key)}">
          ${esc(f.label)}<b>${f.n}</b></button>`).join('')}
    </div>` : '';
  // 欧陆风云4 陆军单位：在兵种筛选下再按科技组分档
  const isEu4Land = d.game === 'eu4' && WIKI_ST.tab === 'landunits'
    && (d.unitGroups || []).length > 0;
  const grpBar = isEu4Land ? `
    <div class="wk-cats wk-grpbar">
      ${[{ key: '', name: T('wiki.all'), n: null }].concat(d.unitGroups || []).map(g =>
        `<button class="wk-cat wk-cat-sm ${WIKI_ST.grp === g.key ? 'active' : ''}" data-grp="${esc(g.key)}">
          ${esc(g.name)}${g.n !== null ? `<b>${g.n}</b>` : ''}</button>`).join('')}
    </div>` : '';
  // 群星传统：选中某棵树时显示这棵树的说明与 AI 权重（对应游戏里树界面左下角）
  const tree = (!isCk3 && WIKI_ST.tab === 'traditions' && WIKI_ST.cat)
    ? ((d.treeInfo || {})[WIKI_ST.cat] || null) : null;
  const treeCard = tree ? `
    <div class="wk-treeinfo">
      <div class="wk-tree-head">
        <b>${esc((cats.find(c => c.v === WIKI_ST.cat) || {}).label || '')}</b>
        ${tree.aiWeight !== null && tree.aiWeight !== undefined
          ? `<span>${esc(T('wiki.aiWeight'))} ${esc(String(tree.aiWeight))}</span>` : ''}
      </div>
      ${(tree.descs || []).map(x => `<div class="wk-tree-desc">
        ${x.cond && x.cond.length ? `<em>${esc(x.cond.join(' / '))}</em>` : ''}
        <span>${esc(x.text)}</span></div>`).join('')}
    </div>` : '';

  $('#view').innerHTML = `
    <div class="page-head"><div>
      <div class="page-title">${esc(T('wiki.title'))}</div>
      <div class="page-sub">${esc(T('wiki.sub'))}${d.gameVer ? ` · ${esc(d.gameName || '')} ${esc(d.gameVer)}` : ''}</div>
    </div><div class="bar">
      ${isCk3 && d.game !== 'eu4' ? `<button class="btn sm ghost" id="wkArmy">${esc(T('wiki.armyPage'))}</button>
      <button class="btn sm ghost" id="wkCulture">${esc(T('wiki.culturePage'))}</button>` : ''}
      <button class="btn sm ghost" id="wkRefresh" title="${esc(T('wiki.refreshHint'))}">⟳ ${esc(T('wiki.refresh'))}</button>
    </div></div>

    <div class="wk-tabs">
      ${tabs.map(t => `<button class="wk-tab ${WIKI_ST.tab === t.id ? 'active' : ''}" data-tab="${t.id}">
        <i>${t.icon}</i><span>${esc(t.label)}</span><b>${t.n}</b></button>`).join('')}
    </div>

    ${layout === 'maa' ? wikiCycleCard() : ''}
    ${areaBar}
    ${grpBar}

    <div class="wk-toolbar">
      <input class="inp wk-search" id="wkQ" value="${esc(WIKI_ST.q)}" placeholder="${esc(T('wiki.search'))}">
      ${layout === 'maa'
        ? `<select class="inp wk-sel wk-sortsel" id="wkSort">${sortOpts.map(o =>
            `<option value="${o.v}" ${WIKI_ST.sort === o.v ? 'selected' : ''}>${esc(o.label)}</option>`).join('')}</select>`
        : ''}
      ${useSelect
        ? `<select class="inp wk-sel" id="wkSel">${cats.map(c =>
            `<option value="${esc(c.v)}" ${WIKI_ST.cat === c.v ? 'selected' : ''}>${esc(c.label)}${c.n !== null ? ' (' + c.n + ')' : ''}</option>`).join('')}</select>`
        : `<div class="wk-cats">${cats.map(c =>
            `<button class="wk-cat ${WIKI_ST.cat === c.v ? 'active' : ''}" data-cat="${esc(c.v)}">
              ${esc(c.label)}${c.n !== null ? `<b>${c.n}</b>` : ''}</button>`).join('')}</div>`}
    </div>

    ${treeCard}

    <div class="wk-count">${esc(T('wiki.found', { n: items.length }))}${
      d.cached === false ? ` <span class="wk-new">${esc(T('wiki.justBuilt'))}</span>` : ''}</div>

    <div class="wk-list" id="wkList">
      ${items.length ? items.map(wikiRow).join('') : `<div class="empty">${esc(T('wiki.empty'))}</div>`}
    </div>`;

  $$('.wk-tab').forEach(b => b.onclick = () => {
    WIKI_ST.tab = b.dataset.tab; WIKI_ST.cat = ''; WIKI_ST.area = ''; WIKI_ST.tier = '';
    WIKI_ST.flag = ''; WIKI_ST.grp = ''; WIKI_ST.open = ''; paintWiki();
  });
  $$('.wk-cat').forEach(b => b.onclick = () => {
    if (b.dataset.cat === undefined) return;   // 领域/层级/标记按钮走各自的绑定
    WIKI_ST.cat = b.dataset.cat; WIKI_ST.open = ''; paintWiki();
  });
  $$('[data-area]').forEach(b => b.onclick = () => {
    WIKI_ST.area = b.dataset.area; WIKI_ST.cat = ''; WIKI_ST.open = ''; paintWiki();
  });
  $$('[data-tier]').forEach(b => b.onclick = () => {
    WIKI_ST.tier = b.dataset.tier; WIKI_ST.open = ''; paintWiki();
  });
  $$('[data-flag]').forEach(b => b.onclick = () => {
    WIKI_ST.flag = (WIKI_ST.flag === b.dataset.flag) ? '' : b.dataset.flag;
    WIKI_ST.open = ''; paintWiki();
  });
  $$('[data-grp]').forEach(b => b.onclick = () => {
    WIKI_ST.grp = b.dataset.grp; WIKI_ST.open = ''; paintWiki();
  });
  const sel = $('#wkSel');
  if (sel) sel.onchange = () => { WIKI_ST.cat = sel.value; WIKI_ST.open = ''; paintWiki(); };
  const so = $('#wkSort');
  if (so) so.onchange = () => { WIKI_ST.sort = so.value; WIKI_ST.open = ''; paintWiki(); };
  const q = $('#wkQ');
  if (q) {
    q.oninput = () => {
      WIKI_ST.q = q.value;
      const cur = wikiItems();
      $('#wkList').innerHTML = cur.length ? cur.map(wikiRow).join('')
        : `<div class="empty">${esc(T('wiki.empty'))}</div>`;
      const cnt = document.querySelector('.wk-count');
      if (cnt) cnt.textContent = T('wiki.found', { n: cur.length });
    };
  }
  $('#wkList').onclick = e => {
    const btn = e.target.closest('[data-wiki]');
    if (btn) { e.stopPropagation(); wikiGoto(WIKI_ST.tab, btn.dataset.wiki); return; }
    const row = e.target.closest('.wk-row');
    if (!row) return;
    WIKI_ST.open = (WIKI_ST.open === row.dataset.k) ? '' : row.dataset.k;
    paintWiki();
  };
  $('#wkRefresh').onclick = async () => {
    WIKI_ST.force = true;
    toast(T('wiki.rebuilding'), 'ok');
    await renderWiki();
  };
  const bArmy = $('#wkArmy'), bCulture = $('#wkCulture');
  if (bArmy) bArmy.onclick = () => post('/api/url', { url: WIKI_SITE + 'Army' });
  if (bCulture) bCulture.onclick = () => post('/api/url', { url: WIKI_SITE + 'Culture' });
}

const ROUTES = {
  home: renderHome, mods: renderMods, playsets: renderPlaysets,
  dlc: renderDlc, saves: renderSaves, workshop: renderWorkshop,
  tools: renderTools, diag: renderDiag, settings: renderSettings,
  gamedata: renderGameData, memory: renderMemory, sponsor: renderSponsor,
  ach: renderAchievements, versions: renderVersions, wiki: renderWiki,
};
function route() {
  cleanupDrag();          // 切页面前先清掉拖拽残影
  // 没有 hash 时进入右侧栏第一项（用户拖拽排序后的第一个）
  const r = (location.hash.replace('#/', '') || firstNavRoute()).split('?')[0];
  S.route = ROUTES[r] ? r : firstNavRoute();
  $$('.nav-item').forEach(a => a.classList.toggle('active', a.dataset.route === S.route));
  render();
}
function render() {
  const fn = ROUTES[S.route] || renderHome;
  $('#view').innerHTML = '';
  fn();
}

/* ---------------- 概览 ---------------- */
async function renderHome() {
  const st = S.state;
  const list = st.playsets || [];
  const act = list.find(p => p.id === st.activePlayset);
  const total = S.mods.length;
  const enabled = S.play.length ? S.play.filter(x => x.enabled).length : (act ? act.enabled : 0);

  let saveCount = 0;
  try { const sv = await api('/api/saves'); saveCount = (sv.items || []).length; } catch (e) {}

  let cons = null;
  try { cons = await api('/api/consistency'); } catch (e) {}
  const hasIssue = cons && cons.ok && (
    (cons.extra && cons.extra.length) || (cons.missing && cons.missing.length) || cons.orderDiff
  );
  const issueHtml = hasIssue ? `
    <div class="card" style="border-color:rgba(210,153,34,.45);background:linear-gradient(180deg,rgba(48,38,16,.55),rgba(20,16,10,.6));margin-bottom:14px">
      <div class="card-title" style="color:#e3b341">⚠ ${esc(T('home.issueTitle'))}</div>
      <div class="card-desc" style="margin:6px 0 10px">
        ${T('home.issueDesc', { want: cons.wantCount, have: cons.haveCount })}
        ${cons.extra?.length ? `<br>${esc(T('home.issueExtra'))}${cons.extra.slice(0, 5).map(x => esc(x.name)).join(T('common.listSep'))}${cons.extra.length > 5 ? T('home.issueMore', { n: cons.extra.length }) : ''}` : ''}
        ${cons.missing?.length ? `<br>${esc(T('home.issueMissing'))}${cons.missing.slice(0, 5).map(x => esc(x.name)).join(T('common.listSep'))}${cons.missing.length > 5 ? T('home.issueMore', { n: cons.missing.length }) : ''}` : ''}
        ${cons.orderDiff ? `<br>${esc(T('home.issueOrder'))}` : ''}
      </div>
      <button class="btn sm primary" id="fixCons">${esc(T('home.fixCons'))}</button>
    </div>` : '';

  const curDef = (st.gameDefs || {})[st.currentGame || 'ck3'] || { name: '十字军之王 III', en: 'Crusader Kings III' };
  $('#view').innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">${esc(T('home.title'))} <span class="sub">${esc(curDef.en)} · ${esc(st.version)}</span></div>
        <div class="page-sub">${st.exeExists ? esc(T('home.exeFound')) : esc(T('home.exeMissing'))} · ${esc(T('home.dataDir'))} ${st.dbExists ? esc(T('home.dbOk')) : esc(T('home.dbMissing'))}</div>
      </div>
      <div class="bar">
        <button class="btn ghost sm" id="hRefresh" title="${esc(T('home.refreshTitle'))}">⟳ ${esc(T('common.refresh'))}</button>
        <button class="btn ghost sm" id="hOpenUser">${esc(T('settings.openDataDir'))}</button>
        <button class="btn primary" id="hLaunch"><span class="play">▶</span> ${esc(T('top.launch'))}</button>
      </div>
    </div>

    <div class="stat-grid">
      <div class="stat"><div class="k">${esc(T('home.statMods'))}</div><div class="v">${total || '—'}<small>${esc(T('common.unit'))}</small></div><div class="f">${esc(T('home.statModsFoot'))}</div></div>
      <div class="stat"><div class="k">${esc(T('home.statEnabled'))}</div><div class="v">${enabled}<small>${esc(T('common.unit'))}</small></div><div class="f">${esc(act ? act.name : T('home.noPlayset'))}</div></div>
      <div class="stat"><div class="k">${esc(T('home.statPlaysets'))}</div><div class="v">${list.length}<small>${esc(T('common.unit'))}</small></div><div class="f">${esc(T('home.statPlaysetsFoot'))}</div></div>
      <div class="stat"><div class="k">${esc(T('home.statSaves'))}</div><div class="v">${saveCount}<small>${esc(T('common.unit'))}</small></div><div class="f">${esc(T('home.statSavesFoot'))}</div></div>
    </div>

    ${issueHtml}

    <div class="card">
      <div class="card-title">${esc(T('top.currentPlayset'))}</div>
      <div class="card-desc" style="margin-bottom:12px">${act ? esc(act.name) + T('home.enabledOf', { e: act.enabled, t: act.total }) : esc(T('home.noActivePlayset'))}</div>
      <div class="bar">
        <button class="btn" id="hMods">${esc(T('home.manageMods'))}</button>
        <select id="hSwitch" class="btn" style="max-width:280px">${list.map(p => `<option value="${p.id}"${p.id === st.activePlayset ? ' selected' : ''}>${esc(p.name)}</option>`).join('')}</select>
        <button class="btn ghost sm" id="hActivate">${esc(T('home.applySelected'))}</button>
      </div>
    </div>

    <div class="card">
      <div class="card-title">${esc(T('home.shortcuts'))}</div>
      <div class="tool-grid" style="margin-top:12px">
        ${tool('☰', T('nav.mods'), T('home.toolModsDesc'), 'goto:mods')}
        ${tool('❏', T('nav.playsets'), T('home.toolPlaysetsDesc'), 'goto:playsets')}
        ${tool('◆', T('nav.dlc'), T('home.toolDlcDesc'), 'goto:dlc')}
        ${tool('⛁', T('nav.saves'), T('home.toolSavesDesc'), 'goto:saves')}
      </div>
    </div>`;

  $('#hLaunch').onclick = () => launch();
  $('#hMods').onclick = () => location.hash = '#/mods';
  $('#hOpenUser').onclick = () => post('/api/open', { path: S.state.userDir });
  // 手动刷新：重新读一遍配置 + 模组索引 + 当前播放集，然后重绘
  $('#hRefresh').onclick = async (ev) => {
    const btn = ev.currentTarget;
    btn.disabled = true;
    btn.textContent = '刷新中…';
    try {
      await refreshState();            // 重新拉 /api/state（目录、版本、播放集列表）
      await reloadGameData(true);      // 强制重扫 mod 索引 + 重载当前播放集
      render();                        // 重绘（按钮随之被替换，不用再恢复文字）
      toast('已刷新', 'ok');
    } catch (e) {
      btn.disabled = false;
      btn.textContent = '⟳ 刷新';
      toast('刷新失败：' + e.message, 'err');
    }
  };
  const fc = $('#fixCons');
  if (fc) fc.onclick = async () => {
    const r = await post('/api/playset/sync', {});
    toast(r.ok ? '已按播放集重写 dlc_load.json' : (r.error || '修复失败'), r.ok ? 'ok' : 'err');
    render();
  };
  // 下拉选完直接生效，不用再点按钮
  const switchTo = async (id, label) => {
    if (!id) return;
    const r = await post('/api/playset/activate', { id });
    if (!r.ok) { toast(r.error || '切换播放集失败', 'err'); return; }
    await refreshState();
    render();
    toast('已切换到：' + (label || '播放集'), 'ok');
  };
  const hs = $('#hSwitch');
  if (hs) hs.onchange = e => switchTo(e.target.value, e.target.selectedOptions[0].textContent);
  const ha = $('#hActivate');
  if (ha) ha.onclick = () => switchTo($('#hSwitch').value);
}

function tool(ic, title, desc, act) {
  return `<button class="tool" data-act="${act}"><div class="ic">${ic}</div>
    <div class="tx"><strong>${esc(title)}</strong><small>${esc(desc)}</small></div></button>`;
}
document.addEventListener('click', e => {
  const t = e.target.closest('[data-act]');
  if (!t) return;
  const a = t.dataset.act;
  if (a.startsWith('goto:')) location.hash = '#/' + a.slice(5);
});

/* 排序方式：自动 / A→Z / Z→A / 名称（中文按拼音）
   自动排序按社区经验分层：数字越小越靠上（越早加载），越靠下优先级越高 */
const AUTO_SORT_GROUPS = [
  { w: 0, re: /(framework|library|lib|core|utils?|api|base|前置|框架|基础|核心库|依赖)/i },
  { w: 1, re: /(overhaul|total[\s_-]*conversion|mega|整合|大型|重制|复兴|重构)/i },
  { w: 3, re: /(compat(?:ibility)?|patch|hotfix|fix|兼容|补丁|修复|修正)/i },
  { w: 4, re: /(translation|locali[sz]ation|l10n|chinese|汉化|翻译|本地化|简中|繁中)/i },
];
function autoSortWeight(m) {
  const s = String((m && (m.name || m.modFile)) || '');
  // 汉化/兼容补丁优先判定：名字里同时带 core 的汉化也应当排到最后
  for (const i of [3, 2, 0, 1]) {
    if (AUTO_SORT_GROUPS[i].re.test(s)) return AUTO_SORT_GROUPS[i].w;
  }
  return 2;
}
function autoSortCompare(a, b) {
  const wa = autoSortWeight(a), wb = autoSortWeight(b);
  if (wa !== wb) return wa - wb;
  return String(a.name || a.modFile || '').localeCompare(String(b.name || b.modFile || ''), 'zh');
}

const SORT_MODES = {
  az: {
    label: '按 A → Z 排序',
    cmp: (a, b) => String(a.name || '').localeCompare(String(b.name || ''), 'en', { sensitivity: 'base' }),
  },
  za: {
    label: '按 Z → A 排序',
    cmp: (a, b) => String(b.name || '').localeCompare(String(a.name || ''), 'en', { sensitivity: 'base' }),
  },
  zh: {
    label: '按名称排序（中文优先）',
    cmp: (a, b) => String(a.name || '').localeCompare(String(b.name || ''), 'zh'),
  },
};

/* 每个游戏的覆盖规则与排序技巧（社区经验，仅供参考） */
const GAME_SORT_RULES = {
  ck3: {
    effect: '排序有效',
    rule: '按播放集顺序从上到下加载，越靠下越晚加载；同名文件由靠下的模组覆盖靠上的。引擎直接使用启动器写入的顺序。',
    tips: '框架/前置和大型改模放上面，兼容补丁、汉化放最下面（后加载才能覆盖）。新增 mod 后进游戏确认一次加载顺序。',
  },
  stellaris: {
    effect: '排序有效',
    rule: '与 CK3 相同：按播放集顺序加载，靠下的后加载并覆盖靠上的同名文件。',
    tips: '大型 overhaul / 框架置顶，补丁与汉化置底；多个改同一文件的小 mod 只保留一个或明确先后。',
  },
  hoi4: {
    effect: '排序有效',
    rule: '播放集顺序就是加载顺序，越靠下越晚加载、同名文件覆盖靠上。',
    tips: '前置框架置顶，兼容补丁/汉化置底；国策、地图类大型 mod 互相冲突时不要同时启用。',
  },
  vic3: {
    effect: '只能部分生效',
    rule: '启停文件是 content_load.json。不同文件名的模组一律按文件名的 ASCII 顺序加载，播放集顺序完全不参与；只有「同名且同路径」的文件，才由靠下的模组覆盖靠上的。GUI 的 type/template 反而先加载者优先（靠上的赢，方向相反）；本地化词条后加载者不覆盖，需放进 localization/<语言>/replace。',
    tips: '想让排序起作用，只能让冲突文件同名同路径后再拖顺序；汉化放最下面并保持原文件名。覆盖不对时先查文件名冲突，别一直拖顺序。点「排序生效」可看到哪些文件真的受顺序影响。⚠ 注意：Vic3 的工坊 mod 走 Steam UGC 后端，启动游戏时 Steam 必须在运行（新版启动器会自动拉起），否则 mod 全部不加载。',
  },
  eu4: {
    effect: '播放集顺序不生效',
    rule: 'EU4 引擎完全忽略启动器的播放集顺序，只按 .mod 描述文件里 name= 的 UTF-8 排序加载，名称靠前的覆盖靠后的（数字、大写字母在前）。',
    tips: '要让排序真正生效：点工具栏「排序生效」→ 开启「强制排序」，启动器会按播放集顺序给 name 加 01/02 前缀；不想用了在同一处一键还原。另外 mod 得先真的进 dlc_load.json：播放集未激活时它一直是空的，游戏里 mod 全没有——「同步到游戏」会自动激活。',
  },
  eu5: {
    effect: '只能部分生效',
    rule: '与维多利亚 3 同为 content_load.json（1.1 格式）。不同文件名按文件名的 ASCII 顺序加载，播放集顺序不参与；同名同路径才靠下覆盖靠上；GUI type/template 先加载者优先；本地化需 localization/<语言>/replace。',
    tips: '先按推荐顺序排，再进游戏验证；覆盖异常优先排查文件名冲突。EU5 的模组用 .metadata/metadata.json 描述，没有 .mod 文件。⚠ EU5 与 Vic3 一样走 Steam UGC 后端：启动时 Steam 必须在运行（新版启动器会自动拉起），否则 mod 全部不加载。',
  },
  ck2: {
    effect: '排序有效',
    rule: '由用户目录 settings.txt 的 last_mods 顺序决定，越靠后越晚加载并覆盖靠前；启动器会把播放集顺序写进去。',
    tips: '大型 mod 在前，小补丁和汉化在后；改完顺序后确认 settings.txt 已更新。',
  },
  imperator: {
    effect: '排序有效',
    rule: '与 HOI4/CK3 同属 Clausewitz 系，按播放集顺序加载，靠下覆盖靠上。',
    tips: '大型 overhaul 与轻量补丁分层摆放，补丁和汉化放最下面。',
  },
  skylines: {
    effect: '排序无效果',
    rule: '没有加载顺序概念，所有模组同时加载；排序只影响列表展示，不影响游戏。',
    tips: '冲突只能靠模组本身的兼容性判断，排序无意义。',
  },
  skylines2: {
    effect: '排序无效果',
    rule: '同样没有传统加载顺序，模组并行加载；排序仅用于整理列表。',
    tips: '关注作者标注的兼容版本，排序不解决冲突。',
  },
};
const GAME_SORT_ORDER = ['ck3', 'stellaris', 'hoi4', 'vic3', 'eu4', 'eu5', 'ck2', 'imperator', 'skylines', 'skylines2'];

function gameSortRule(key) {
  return GAME_SORT_RULES[key] || {
    effect: '未知',
    rule: '该游戏暂无已知的加载顺序规则，请以游戏内实际表现为准。',
    tips: '建议只启用确认兼容的模组，并在游戏内验证。',
  };
}

/* 自动排序前的说明弹窗：写清覆盖规则、技巧与「仅供参考」 */
function openAutoSortDialog() {
  return new Promise(resolve => {
    const cur = S.state.currentGame || 'ck3';
    const curName = gameLabel((S.state.gameDefs || {})[cur]) || (GAME_SORT_RULES[cur] ? cur : cur);
    const curRule = gameSortRule(cur);
    const rows = GAME_SORT_ORDER.map(k => {
      const def = (S.state.gameDefs || {})[k] || {};
      const nm = gameLabel(def) || k;
      const r = gameSortRule(k);
      return `<div class="sr-row${k === cur ? ' cur' : ''}">
        <div><span class="sr-name">${esc(nm)}</span><span class="sr-tag">${esc(r.effect)}</span></div>
        <div><b>覆盖：</b>${esc(r.rule)}</div>
        <div><b>技巧：</b>${esc(r.tips)}</div>
      </div>`;
    }).join('');

    const prevFocus = document.activeElement;
    const mask = document.createElement('div');
    mask.className = 'modal-mask';
    mask.innerHTML = `
      <div class="modal conflict-panel">
        <div class="modal-head">自动排序 · ${esc(curName)}</div>
        <div class="modal-body sort-rule">
          <div class="cf-notice">⚠ 自动排序是按社区经验整理的推荐顺序，仅供参考，不保证游戏内一定按此覆盖。<br>
            从 Steam 启动会被官方启动器用它自己的活跃播放集重写启停文件，请用人联启动器直接启动。</div>
          <div class="cf-summary">
            <b>分层规则：</b>前置/框架/库 → 大型改模 → 普通模组 → 兼容补丁 → 汉化/翻译；同层再按名称排序（中文按拼音）。<br>
            <b>识别方式：</b>按模组名称关键词分组，未命中的一律按普通模组处理。<br>
            <b>注意：</b>重排只调整当前播放集，会覆盖现有顺序，且无法一键还原。
          </div>
          <div class="cf-sec">
            <div class="cf-sec-t">当前游戏：${esc(curName)} <span class="cf-hint">${esc(curRule.effect)}</span></div>
            <div class="sr-row cur">
              <div><b>覆盖规则：</b>${esc(curRule.rule)}</div>
              <div><b>技巧：</b>${esc(curRule.tips)}</div>
            </div>
          </div>
          <div class="cf-sec">
            <div class="cf-sec-t">各游戏覆盖规则与技巧 <span class="cf-hint">当前游戏高亮</span></div>
            ${rows}
          </div>
        </div>
        <div class="modal-foot">
          <button class="btn ghost" data-m="cancel">取消</button>
          <button class="btn primary" data-m="ok">按推荐顺序重排</button>
        </div>
      </div>`;
    document.body.appendChild(mask);
    requestAnimationFrame(() => mask.classList.add('show'));

    const finish = (val) => {
      mask.classList.remove('show');
      document.removeEventListener('keydown', onKey, true);
      setTimeout(() => {
        mask.remove();
        if (prevFocus && prevFocus.focus) { try { prevFocus.focus(); } catch (e) {} }
        resolve(val);
      }, 130);
    };
    const onKey = (e) => {
      if (e.key === 'Escape') { e.stopPropagation(); finish(false); }
    };
    mask.querySelector('[data-m="ok"]').onclick = () => finish(true);
    mask.querySelector('[data-m="cancel"]').onclick = () => finish(false);
    mask.addEventListener('mousedown', e => {
      if (e.target === mask) finish(false);
    });
    document.addEventListener('keydown', onKey, true);
  });
}

function openSortMenu(anchorEl) {
  document.querySelectorAll('.mod-menu').forEach(x => x.remove());
  const menu = document.createElement('div');
  menu.className = 'mod-menu';
  menu.innerHTML = `<div class="mm-title">排序方式</div><div class="mm-sep"></div>`;
  Object.keys(SORT_MODES).forEach(k => {
    const d = document.createElement('div');
    d.className = 'mm-item';
    d.textContent = SORT_MODES[k].label;
    d.onclick = () => { menu.remove(); applySortMode(k); };
    menu.appendChild(d);
  });
  const rect = anchorEl.getBoundingClientRect();
  document.body.appendChild(menu);
  const mw = menu.offsetWidth, mh = menu.offsetHeight;
  let left = rect.left, top = rect.bottom + 6;
  if (left + mw > innerWidth - 10) left = Math.max(8, innerWidth - mw - 10);
  if (top + mh > innerHeight - 10) top = Math.max(8, rect.top - mh - 6);
  menu.style.left = left + 'px';
  menu.style.top = top + 'px';
  const close = ev => {
    if (!menu.contains(ev.target)) {
      document.removeEventListener('mousedown', close);
      menu.remove();
    }
  };
  setTimeout(() => document.addEventListener('mousedown', close), 0);
}

async function applySortMode(mode) {
  const m = SORT_MODES[mode];
  if (!m) return;
  if (S.play.length < 2) { toast(TT('当前播放集里没有可排序的模组'), 'err'); return; }
  const ok = await ui.confirm(
    `${TT(m.label)}\n\n${TT('重排会覆盖你当前这个播放集里的模组顺序，且原顺序没法一键还原（只能自己再拖回去）。')}`,
    { title: TT('确认重新排序？'), okText: TT('继续排序') }
  );
  if (!ok) return;
  S.play.sort(m.cmp);
  paintLists();
  scheduleSave();
  toast(TT('已') + TT(m.label), 'ok');
}

/* 自动排序：先弹窗说明各游戏覆盖规则与技巧，确认后按推荐分层重排 */
async function applyAutoSort() {
  if (S.play.length < 2) { toast(TT('当前播放集里没有可排序的模组'), 'err'); return; }
  const ok = await openAutoSortDialog();
  if (!ok) return;
  S.play.sort(autoSortCompare);
  paintLists();
  scheduleSave();
  toast(TT('已按推荐规则重排；EU4 若已开启强制排序，本次重排会同步写入 name 前缀'), 'ok');
}

/* 排序生效分析：Vic3/EU5 看「同名同路径」冲突，EU4 提供强制排序开关 */
async function openOrderEffectModal() {
  const id = S.state.activePlayset || ((S.state.playsets || [])[0] || {}).id;
  if (!id) { toast(TT('当前没有激活的播放集'), 'err'); return; }
  const game = S.state.currentGame || 'ck3';
  const rule = gameSortRule(game);
  const curName = gameLabel((S.state.gameDefs || {})[game]) || game;

  const mask = document.createElement('div');
  mask.className = 'modal-mask';
  mask.innerHTML = `
    <div class="modal conflict-panel">
      <div class="modal-head">排序生效 · ${esc(curName)}</div>
      <div class="modal-body sort-rule">
        <div class="cf-notice">⚠ 结论由引擎规则 + 当前播放集文件扫描得出，仅供参考。</div>
        <div class="cf-sec">
          <div class="cf-sec-t">当前播放集的排序效果</div>
          <div id="oeScan" class="cf-summary">${esc(TT('正在扫描启用的模组…'))}</div>
        </div>
        <details class="sr-row cur" style="margin-top:12px">
          <summary style="cursor:pointer"><b>引擎规则与建议（想看原理再点开）</b></summary>
          <div style="margin-top:8px"><b>规则：</b>${esc(rule.rule)}</div>
          <div><b>建议：</b>${esc(rule.tips)}</div>
        </details>
        <div class="cf-sec" id="oeEu4Sec" style="display:none">
          <div class="cf-sec-t">EU4 强制排序 <span class="cf-hint">改写 .mod 的 name 前缀，可一键还原</span></div>
          <div id="oeEu4"></div>
        </div>
      </div>
      <div class="modal-foot">
        <button class="btn ghost" data-m="close">${esc(TT('关闭'))}</button>
      </div>
    </div>`;
  document.body.appendChild(mask);
  requestAnimationFrame(() => mask.classList.add('show'));
  const close = () => { mask.classList.remove('show'); setTimeout(() => mask.remove(), 130); };
  mask.querySelector('[data-m="close"]').onclick = close;
  mask.addEventListener('mousedown', e => { if (e.target === mask) close(); });

  const scanEl = mask.querySelector('#oeScan');
  let res = null;
  try {
    res = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => resolve(null), 120000);
      fetchConflictStream(id,
        (s, t) => { if (scanEl) scanEl.textContent = `${TT('已扫描')} ${s} / ${t}`; },
        (r) => { clearTimeout(timer); resolve(r); }
      ).catch(e => { clearTimeout(timer); reject(e); });
    });
  } catch (e) {
    if (scanEl) scanEl.textContent = TT('扫描失败：') + (e && e.message ? e.message : '');
    return;
  }
  if (!scanEl) return;

  const conflicts = (res && res.fileConflicts) || [];
  const help = `<div class="cf-hint" style="line-height:1.8;margin:10px 0 12px">
      <b>怎么看这个表：</b><br>
      · <b>生效</b>：最后真正被游戏读到的那个模组<br>
      · <b>被覆盖</b>：被上面那个盖掉、白加载的模组<br>
      · 想让谁赢，就把它<b>往下拖</b>（越靠下越优先）<br>
      · 名单里没有的模组，跟别人不抢文件，怎么拖都没用
    </div>`;

  if (!res || !res.ok) {
    scanEl.innerHTML = esc(TT('扫描失败：') + ((res && res.error) || ''));
  } else if (game === 'eu4') {
    scanEl.innerHTML = `<div class="cf-notice" style="margin-bottom:10px">EU4 引擎按 <b>.mod 的 name 字母序</b>加载，拖动顺序不参与——想让顺序生效请用下面的「强制排序」。
      另外：排序生效的前提是 mod 真的进了 <b>dlc_load.json</b>——若游戏里模组列表是空的，说明播放集还没激活同步，回模组页点一次「同步到游戏」即可（新版会自动激活未激活的播放集）。</div>
      <div>本次扫描 ${res.scannedMods || 0} 个模组、${res.totalFiles || 0} 个文件。</div>`;
  } else if (!conflicts.length) {
    scanEl.innerHTML = `<div class="cf-notice" style="margin-bottom:10px">✅ <b>排序完全没影响。</b>当前模组之间没有共用同一个文件，怎么拖进游戏都一样。</div>
      ${help}`;
  } else {
    // 按模组汇总：生效几个文件、被覆盖几个文件
    const involved = new Map();
    const bump = (n, k) => {
      if (!involved.has(n)) involved.set(n, { win: 0, lose: 0 });
      involved.get(n)[k]++;
    };
    conflicts.forEach(c => {
      bump(c.winner, 'win');
      (c.losers || []).forEach(n => bump(n, 'lose'));
    });
    const list = [...involved.entries()]
      .sort((a, b) => (b[1].lose - a[1].lose) || (b[1].win - a[1].win))
      .map(([n, c]) => `<div class="sr-row"><div><b>${esc(n)}</b></div>
        <div>生效 ${c.win} 个文件 · 被覆盖 ${c.lose} 个文件</div></div>`).join('');
    const rows = conflicts.slice(0, 20).map(c =>
      `<div class="sr-row"><div><b>${esc(c.rel)}</b></div>
        <div>生效：<b>${esc(c.winner)}</b>（靠下优先）</div>
        <div>被覆盖：${esc((c.losers || []).join('、'))}</div></div>`).join('');
    scanEl.innerHTML = `
      <div class="cf-notice" style="margin-bottom:6px">⚠ 只有 <b>${conflicts.length}</b> 个文件被多个模组同时包含 —— 排序只对下面这些模组有效，其余模组拖顺序没用。</div>
      ${help}
      <div class="cf-sec-t">受顺序影响的模组名单（共 ${involved.size} 个）</div>
      ${list}
      <div class="cf-sec-t" style="margin-top:14px">具体冲突文件（前 20 条）</div>
      ${rows}
      ${conflicts.length > 20 ? `<div class="cf-hint">仅显示前 20 条，完整列表见「冲突排查」。</div>` : ''}`;
  }

  if (game === 'eu4') {
    const sec = mask.querySelector('#oeEu4Sec');
    const box = mask.querySelector('#oeEu4');
    sec.style.display = '';
    const paintEu4 = async () => {
      const st = await api('/api/eu4/force-order');
      box.innerHTML = `
        <div>当前状态：<b>${st.enabled ? '已开启' : '未开启'}</b>，已改写 ${st.count || 0} 个 .mod 的 name。</div>
        <div class="bar" style="margin-top:10px">
          <button class="btn sm ${st.enabled ? 'ghost' : 'primary'}" data-eo="on"${st.enabled ? ' disabled' : ''}>开启强制排序</button>
          <button class="btn sm ghost" data-eo="off"${st.count ? '' : ' disabled'}>一键还原</button>
        </div>
        <div class="cf-hint" style="margin-top:8px">开启后按播放集顺序改写 name（01/02… 前缀）。原始 name 已备份，可随时还原；游戏内模组名会带前缀，联机前建议先还原。</div>`;
      box.querySelectorAll('[data-eo]').forEach(btn => {
        btn.onclick = async () => {
          const on = btn.dataset.eo === 'on';
          btn.disabled = true;
          const r = await post('/api/eu4/force-order',
            on ? { enabled: true } : { enabled: false, restore: true });
          if (!r.ok) { toast(r.error || '操作失败', 'err'); btn.disabled = false; return; }
          toast(on ? `已按播放集顺序改写 ${r.applied || 0} 个 .mod`
                   : `已还原 ${r.restored || 0} 个 .mod 的原名`, 'ok');
          await paintEu4();
        };
      });
    };
    paintEu4().catch(() => {});
  }
}

/* ---------------- MOD 管理（核心） ---------------- */
async function renderMods() {
  const st = S.state;
  $('#view').innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">模组管理</div>
        <div class="page-sub">拖动右侧列表调整加载顺序 —— <b style="color:var(--gold-b)">越靠下优先级越高</b>，会覆盖上面的同名文件。也可以从左栏直接拖进右栏。</div>
      </div>
      <div class="bar">
        <button class="btn ghost sm" id="mRefresh">重新扫描</button>
        <button class="btn ghost sm" id="mOpenMod">打开 MOD 目录</button>
        <button class="btn ghost sm" id="mRegAll" title="创意工坊新订阅的模组，需要生成本地 .mod 描述文件才能被游戏加载。点这里一键生成，省得去开官方启动器。">识别新订阅<sup id="mRegCnt"></sup></button>
      </div>
    </div>

    <div class="mods-toolbar">
      <div class="field" style="width:230px"><input type="text" id="qLeft" placeholder="搜索已识别模组…"><span class="clr" data-clr="qLeft" hidden>×</span></div>
      <div class="bar" id="filters">
        <span class="chip on" data-f="all">全部</span>
        <span class="chip" data-f="enabled">已启用</span>
        <span class="chip" data-f="local">本地</span>
        <span class="chip" data-f="warn">版本不符</span>
      </div>
      <div class="grow"></div>
      <div class="bar">
        <button class="btn sm ghost" id="sortMenu" title="按不同规则重排右侧播放集">排序 ▾</button>
        <button class="btn sm ghost" id="mAllOn">全部启用</button>
        <button class="btn sm ghost" id="mAllOff">全部禁用</button>
        <button class="btn sm ghost" id="mRev">反转顺序</button>
        <button class="btn sm ghost" id="mConflicts" title="检查当前播放集里启用的模组之间有无文件/本地化/依赖冲突">${TT('冲突排查')}</button>
        <button class="btn sm ghost" id="mAutoSort" title="按游戏推荐规则自动重排当前播放集（先说明覆盖规则与技巧，仅供参考）">自动排序</button>
        <button class="btn sm ghost" id="mOrderEffect" title="查看当前排序在引擎里是否真的生效；EU4 可开启强制排序"${['vic3', 'eu5', 'eu4'].includes(S.state.currentGame) ? '' : ' style="display:none"'}>排序生效</button>
        <button class="btn sm ghost" id="mExport" title="把当前播放集导出为 JSON 文件">导出播放集</button>
        <button class="btn sm ghost" id="mImportPs" title="从 JSON 文件导入播放集，本地缺失的工坊模组会自动订阅">导入播放集</button>
        <button class="btn sm ghost" id="mImportZip" title="选择压缩包安装本地 Mod（也可以直接把压缩包拖进窗口）">导入 Mod</button>
      </div>
    </div>
    <div class="col-bar" id="colBar"></div>

    <div class="mods-split">
      <div class="modcol">
        <div class="modcol-head">
          <div class="modcol-title">已识别模组 <span class="cnt gray" id="cntLeft">0</span></div>
          <div class="grow" style="flex:1"></div>
          <div class="field" style="width:150px"><input type="text" id="qLeft2" placeholder="筛选…"></div>
        </div>
        <div class="modcol-body" id="leftBody"></div>
      </div>
      <div class="modcol">
        <div class="modcol-head cur">
          <div class="modcol-title">当前播放集 <span class="cnt" id="cntRight">0</span></div>
          <div style="flex:1"></div>
          <div class="field" style="width:130px"><input type="text" id="qRight" placeholder="搜索…"></div>
        </div>
        <div class="modcol-body" id="rightBody"></div>
      </div>
    </div>`;

  // 事件
  $('#mRefresh').onclick = async () => {
    saveState('重新扫描中…', true);
    const d = await api('/api/mods?force=1');
    S.mods = d.items || [];
    S.modByKey = new Map(S.mods.map(m => [m.modFile.toLowerCase(), m]));
    saveState(T('home.loaded', { n: S.mods.length }));
    paintLists();
    toast('扫描完成：' + S.mods.length + ' 个模组', 'ok');
  };
  $('#mOpenMod').onclick = () => post('/api/open', { path: S.state.workshopDir || (S.state.userDir + '/mod') });
  $('#mRegAll').onclick = async () => {
    const btn = $('#mRegAll');
    const oldHtml = btn.innerHTML;
    btn.disabled = true;
    btn.innerHTML = TT('识别中…');
    try {
      const r = await post('/api/register-all', {});
      const ok = (r.registered || []).length;
      const bad = r.failed || 0;
      if (r.error) toast(TT('部分失败：') + r.error, 'err');
      if (ok) {
        toast(TT('已注册 {} 个新订阅').replace('{}', ok) +
              (bad ? '，' + bad + ' ' + TT('失败') : ''), bad ? '' : 'ok');
        $('#mRefresh').click();
      } else if (!r.error) {
        toast(TT('没有需要注册的新订阅'));
      }
    } catch (e) {
      toast(TT('识别失败：') + e.message, 'err');
    } finally {
      btn.disabled = false;
      btn.innerHTML = oldHtml;
      paintLists();
    }
  };
  const onSearch = (val) => { S.qLeft = val; paintLists(); };
  $('#qLeft').oninput = e => { onSearch(e.target.value); $('#qLeft2').value = e.target.value; e.target.nextElementSibling.hidden = !e.target.value; };
  $('#qLeft2').oninput = e => { onSearch(e.target.value); $('#qLeft').value = e.target.value; };
  // 搜索框里的 × 清除按钮
  $$('[data-clr]').forEach(el => el.onclick = () => {
    const inp = $('#' + el.dataset.clr);
    if (inp) {
      inp.value = '';
      inp.dispatchEvent(new Event('input', { bubbles: true }));
    }
    el.hidden = true;
  });
  $$('#filters .chip').forEach(c => c.onclick = () => {
    $$('#filters .chip').forEach(x => x.classList.remove('on'));
    c.classList.add('on'); S.filter = c.dataset.f; paintLists();
  });
  $('#qRight').oninput = e => { S.qRight = e.target.value; paintLists(); };
  $('#sortMenu').onclick = e => { e.stopPropagation(); openSortMenu(e.currentTarget); };
  $('#mRev').onclick = () => { S.play.reverse(); paintLists(); scheduleSave(); };
  $('#mConflicts').onclick = openConflictModal;
  $('#mAutoSort').onclick = applyAutoSort;
  { const oe = $('#mOrderEffect'); if (oe) oe.onclick = openOrderEffectModal; }
  $('#mAllOn').onclick = () => { S.play.forEach(x => x.enabled = true); paintLists(); scheduleSave(); };
  $('#mAllOff').onclick = () => { S.play.forEach(x => x.enabled = false); paintLists(); scheduleSave(); };

  // 导出播放集（复用播放集页的导出逻辑）
  $('#mExport').onclick = () => exportPlayset(S.state.activePlayset);
  // 导入播放集（与播放集页同一个流程，缺失工坊模组会自动订阅）
  $('#mImportPs').onclick = importPlayset;

  // 导入 Mod 压缩包（拖放不灵时的备用入口）
  $('#mImportZip').onclick = async () => {
    const r = await post('/api/mod/pick-archive', {});
    if (r.cancelled) return;
    if (!r.ok) { toast(r.error || '导入失败', 'err'); return; }
    toast('已安装：' + (r.name || r.modFile || '新 Mod'), 'ok');
    await refreshMods();
  };

  // ---- 收藏夹（标签组）----
  const paintColBar = () => {
    const bar = $('#colBar');
    if (!bar) return;
    bar.innerHTML = `
      <span class="col-label">收藏夹</span>
      <span class="chip col-chip${!S.colFilter ? ' on' : ''}" data-col="">全部</span>
      ${S.collections.map(c =>
        `<span class="chip col-chip${S.colFilter === c.name ? ' on' : ''}" data-col="${esc(c.name)}" title="点击筛选 · 右键重命名/删除">
          ${esc(c.name)} <sup>${c.count}</sup>
        </span>`).join('')}
      <button class="col-add" id="colAdd" title="新建收藏夹">＋</button>`;
    // 点击筛选
    $$('#colBar [data-col]').forEach(el => el.onclick = () => {
      S.colFilter = el.dataset.col || '';
      paintColBar(); paintLists();
    });
    // 右键：重命名 / 删除
    $$('#colBar [data-col]').forEach(el => {
      if (!el.dataset.col) return;
      el.oncontextmenu = async (e) => {
        e.preventDefault();
        const name = el.dataset.col;
        const act = await ui.prompt(
          '输入新名字 = 重命名\n输入 del = 删除\n留空 = 取消',
          name, { title: `收藏夹「${name}」` }
        );
        if (act === null || act.trim() === '') return;
        if (act.trim().toLowerCase() === 'del') {
          const ok = await ui.confirm(
            `确定删除收藏夹「${name}」吗？\n\n只删这个分类，里面的模组不会被删。`,
            { title: '删除收藏夹', okText: '删除', danger: true }
          );
          if (!ok) return;
          await post('/api/collection/delete', { name });
          if (S.colFilter === name) S.colFilter = '';
          toast('收藏夹已删除', 'ok');
        } else {
          const r = await post('/api/collection/rename', { old: name, new: act.trim() });
          if (r.ok) { if (S.colFilter === name) S.colFilter = act.trim(); toast('已重命名', 'ok'); }
          else toast('重命名失败（可能重名）', 'err');
        }
        await loadCollections();
        paintLists();
      };
    });
    // 新建
    const addBtn = bar.querySelector('#colAdd');
    if (addBtn) addBtn.onclick = async () => {
      const name = await ui.prompt('输入名字（比如 1、2、3，随便起）：', '', { title: TT('新建收藏夹') });
      if (!name) return;
      const r = await post('/api/collection/create', { name: name.trim() });
      if (r.ok) { toast('收藏夹已创建', 'ok'); await loadCollections(); }
      else toast(r.error || '创建失败', 'err');
    };
  };
  const loadCollections = async () => {
    try {
      const r = await api('/api/collections');
      S.collections = r.items || [];
      paintColBar();
    } catch (e) {}
  };

  await loadCollections();

  paintLists();
  loadPlayset();
}

async function loadPlayset() {
  const id = S.state.activePlayset || ((S.state.playsets || [])[0] || {}).id;
  if (!id) { S.play = []; paintLists(); return; }
  try {
    const d = await api('/api/playset?id=' + encodeURIComponent(id));
    if (!d.ok) { S.play = []; paintLists(); return; }
    S.play = (d.items || []).map(it => ({
      uid: it.uid || it.modFile, modId: it.modId || null,
      modFile: it.modFile, enabled: it.enabled, name: it.name, thumb: it.thumb,
      steamId: it.steamId, supportedVersion: it.supportedVersion,
      compat: it.compat, source: it.source, known: it.known,
    }));
  } catch (e) { S.play = []; }
  paintLists();
}

/* 导出播放集：菜单、播放集页、播放集列表三处共用，文件为 P 社官方格式 */
async function exportPlayset(id) {
  if (!id) { toast('没有激活的播放集', 'err'); return; }
  const r = await post('/api/playset/save-as', { id });
  if (!r.ok) { toast(r.error || '导出失败', 'err'); return; }
  if (r.cancelled) { toast('已取消导出'); return; }
  toast('已导出到：' + r.path, 'ok');
  const skipped = (r.skippedLocal || []).length;
  if (!r.exported) toast('这个播放集里没有工坊模组，官方格式的文件是空的', 'err');
  else if (skipped) toast(`有 ${skipped} 个本地模组无法写进官方格式，已跳过`, 'err');
}

/* 导入播放集：播放集页与模组管理页工具栏共用同一套流程 */
async function importPlayset() {
  const r = await post('/api/playset/open-file', {});
  if (r.cancelled) return;
  if (!r.ok) { toast(r.error || '导入失败', 'err'); return; }
  const data = r.data || {};
  if (!data.mods || !data.mods.length) { toast('这个文件里没有模组记录', 'err'); return; }
  const res = await post('/api/playset/import', {
    data, name: data.name || '导入的播放集',
  });
  if (!res.ok) { toast(res.error || '导入失败', 'err'); return; }
  await refreshState(); render();
  const sub = res.subscribe || {};
  let msg = `导入成功：${res.imported} 个模组${res.missing?.length ? '，' + res.missing.length + ' 个缺失' : ''}`;
  if (sub.subscribed) msg += `，已让 Steam 订阅 ${sub.subscribed} 个（下载完成后自动生效）`;
  toast(msg, 'ok');
  const gone = (res.uninstalled || []).length;
  if (gone && !sub.subscribed) toast(`${gone} 个模组本地文件已丢失，且没有工坊 ID，无法自动补回`, 'err');
  if (sub.error) toast('自动订阅：' + sub.error, 'err');
  else if (sub.failed?.length) toast(`${sub.failed.length} 个模组订阅被 Steam 拒绝`, 'err');
}

/* 重载「当前游戏」的模组索引 + 当前播放集。
   切换游戏、点概览页刷新按钮都走这里，保证概览上的数字立刻对上。 */
async function reloadGameData(force = false) {
  try {
    const d = await api('/api/mods' + (force ? '?force=1' : ''));
    S.mods = d.items || [];
    S.modByKey = new Map(S.mods.map(m => [m.modFile.toLowerCase(), m]));
  } catch (e) {
    S.mods = [];
    S.modByKey = new Map();
  }
  // 重新读当前播放集的模组列表（不在模组页时 paintLists 会自动跳过）
  await loadPlayset();
}

function compatTag(m) {
  if (m.pending) return '<span class="tag pending">未识别</span>';
  if (m.compat === 'bad') return '<span class="tag bad">缺失</span>';
  if (m.compat === 'warn') return '<span class="tag warn">版本</span>';
  return '<span class="tag ok">兼容</span>';
}
const srcTag = m => m.source === 'steam'
  ? '<span class="tag steam">工坊</span>'
  : (m.source === 'local' ? '<span class="tag local">本地</span>' : '');

function leftRow(m, inPlay) {
  const t = thumbUrl(m.thumb);
  return `<div class="modrow${inPlay ? ' dim' : ''}" data-key="${esc(m.modFile)}" data-src="left" data-drag="${inPlay ? 0 : 1}">
    <span class="grip">⋮⋮</span>
    ${t ? `<img class="thumb" src="${t}" loading="lazy" onerror="this.style.visibility='hidden'">` : `<div class="thumb ph">${esc(((m.name||"?").replace(/^[~\-\s\[\]()（）_|]+/,""))[0]||"?")}</div>`}
    <div class="info">
      <div class="nm">${esc(m.name)}</div>
      <div class="mt">${srcTag(m)}${compatTag(m)}<span>${esc(m.supportedVersion || '—')}</span></div>
    </div>
    ${inPlay ? '<span class="icon-btn" style="color:#3fb950;cursor:default">✓</span>'
             : `<button class="icon-btn add" data-add="${esc(m.modFile)}" title="加入播放集">＋</button>`}
    <button class="icon-btn more" data-more="${esc(m.modFile)}" title="更多操作">⋯</button>
  </div>`;
}

function rightRow(m, i) {
  const t = thumbUrl(m.thumb);
  return `<div class="modrow ${m.enabled ? '' : 'dim'}" data-key="${esc(m.uid || m.modFile)}" data-src="right" data-drag="1">
    <span class="idx">${i + 1}</span>
    ${t ? `<img class="thumb" src="${t}" loading="lazy" onerror="this.style.visibility='hidden'">` : `<div class="thumb ph">${esc(((m.name||"?").replace(/^[~\-\s\[\]()（）_|]+/,""))[0]||"?")}</div>`}
    <div class="info">
      <div class="nm" title="${esc(m.name)}">${esc(m.name)}</div>
      <div class="mt">${srcTag(m)}${compatTag(m)}<span>${esc(m.supportedVersion || '—')}</span></div>
    </div>
    <div class="toggle ${m.enabled ? 'on' : ''}" data-tg="${i}"></div>
    <button class="icon-btn more" data-more="${esc(m.modFile || m.uid)}" title="更多操作">⋯</button>
    <button class="icon-btn del" data-rm="${i}" title="移出播放集">×</button>
  </div>`;
}

function paintLists() {
  const leftBody = $('#leftBody'), rightBody = $('#rightBody');
  if (!leftBody) return;

  const playKeys = new Set(S.play.map(x => x.modFile.toLowerCase()));
  const q = S.qLeft.trim().toLowerCase();
  // 收藏夹筛选：拿当前收藏夹的 modFile 集合
  let colMods = null;
  if (S.colFilter) {
    const col = S.collections.find(c => c.name === S.colFilter);
    colMods = new Set();
    if (col) {
      // 需要从后端拿该收藏夹的 modFile 列表（懒加载）
    }
  }
  let left = S.mods.filter(m => {
    if (q && !m.name.toLowerCase().includes(q) && !(m.steamId || '').includes(q)) return false;
    if (S.colFilter) {
      // 收藏夹筛选：只显示在该收藏夹里的 mod（用 mod.collections 标记）
      if (!(m.collections || []).includes(S.colFilter)) return false;
    }
    if (S.filter === 'enabled') return playKeys.has(m.modFile.toLowerCase());
    if (S.filter === 'local') return m.source === 'local';
    if (S.filter === 'warn') return m.compat === 'warn' || m.compat === 'bad' || m.pending;
    return true;
  });
  // 未加入的排前面，方便添加
  left.sort((a, b) => {
    const ia = playKeys.has(a.modFile.toLowerCase()), ib = playKeys.has(b.modFile.toLowerCase());
    if (ia !== ib) return ia - ib;
    return a.name.localeCompare(b.name, 'zh');
  });
  leftBody.innerHTML = left.length
    ? left.map(m => leftRow(m, playKeys.has(m.modFile.toLowerCase()))).join('')
    : '<div class="empty"><div class="big">☰</div>没有匹配的模组</div>';

  const rq = S.qRight.trim().toLowerCase();
  const rows = S.play.map((m, i) => ({ m, i })).filter(({ m }) => !rq || m.name.toLowerCase().includes(rq));
  rightBody.innerHTML = rows.length
    ? rows.map(({ m, i }) => rightRow(m, i)).join('')
    : '<div class="empty"><div class="big">❏</div>播放集是空的<br><small>从左边拖进来，或点 ＋ 添加</small></div>';

  $('#cntLeft').textContent = left.length;
  $('#cntRight').textContent = S.play.filter(x => x.enabled).length + ' / ' + S.play.length;

  // 「识别新订阅」按钮：有未识别的模组时显示数量，没有就禁用
  const pend = S.mods.filter(m => m.pending).length;
  const regBtn = $('#mRegAll');
  const regCnt = $('#mRegCnt');
  if (regBtn) {
    if (regCnt) regCnt.textContent = pend > 0 ? ` ${pend}` : '';
    regBtn.disabled = pend === 0;
    regBtn.title = pend > 0
      ? `有 ${pend} 个创意工坊新订阅还没生成本地描述文件（列表里标为「未识别」），点这里一键生成`
      : '没有需要处理的新订阅';
  }

  bindListEvents();
}

function bindListEvents() {
  // 加入
  $$('[data-add]').forEach(b => b.onclick = e => {
    e.stopPropagation();
    const key = b.dataset.add;
    if (S.play.some(x => x.modFile === key)) return;
    const m = S.modByKey.get(key.toLowerCase());
    if (!m) return;
    S.play.push({
      uid: 'tmp:' + m.modFile, modId: null, modFile: m.modFile, enabled: true,
      name: m.name, thumb: m.thumb, steamId: m.steamId,
      supportedVersion: m.supportedVersion, compat: m.compat,
      source: m.source, pending: m.pending,
    });
    paintLists();
    scheduleSave();
    // 滚动到新加的
    const row = $(`#rightBody .modrow[data-key="${CSS.escape(key)}"]`);
    row && row.scrollIntoView({ block: 'nearest' });
  });

  // 开关 / 移除
  $$('[data-tg]').forEach(t => t.onclick = e => {
    e.stopPropagation();
    const i = +t.dataset.tg;
    S.play[i].enabled = !S.play[i].enabled;
    t.classList.toggle('on', S.play[i].enabled);
    $('#cntRight').textContent = S.play.filter(x => x.enabled).length + ' / ' + S.play.length;
    paintLists();
    scheduleSave();
  });
  $$('[data-rm]').forEach(b => b.onclick = e => {
    e.stopPropagation();
    S.play.splice(+b.dataset.rm, 1);
    paintLists();
    scheduleSave();
  });

  // 「⋯」更多操作菜单
  $$('[data-more]').forEach(b => b.onclick = e => {
    e.stopPropagation();
    const key = b.dataset.more;
    // 从 mod 索引或播放集里找该 mod 的元数据
    let m = S.modByKey.get(key.toLowerCase());
    if (!m) m = S.play.find(x => (x.modFile || x.uid || '').toLowerCase() === key.toLowerCase()) || null;
    if (!m) return;
    openModMenu(m, b);
  });

  initDrag();
}

/* ---------------- mod 更多操作菜单 ---------------- */
function openModMenu(m, anchorEl) {
  // 关闭已存在的菜单
  document.querySelectorAll('.mod-menu').forEach(x => x.remove());

  const menu = document.createElement('div');
  menu.className = 'mod-menu';
  const steamId = m.steamId || '';

  // 标题 + 创意工坊 ID
  menu.innerHTML = `
    <div class="mm-title">${esc(m.name)}</div>
    ${steamId ? `<div class="mm-id">创意工坊 ID：<span class="mm-idv">${esc(steamId)}</span></div>` : `<div class="mm-id">本地模组（无创意工坊 ID）</div>`}
    <div class="mm-sep"></div>`;

  const addItem = (label, fn, cls = '') => {
    const d = document.createElement('div');
    d.className = 'mm-item ' + cls;
    d.textContent = TT(label);
    d.onclick = () => { menu.remove(); fn(); };
    menu.appendChild(d);
  };

  addItem('修改显示名称', async () => {
    const v = await ui.prompt(
      '只改启动器里显示的名字，不动 .mod 文件、不影响游戏内显示。\n想还原就用下面的「恢复原始名称」。',
      m.name, { title: TT('修改显示名称') }
    );
    if (!v) return;
    const alias = v.trim();
    const r = await post('/api/mod/rename', { modFile: m.modFile, alias });
    if (r.ok) {
      toast('已修改显示名称', 'ok');
      await refreshMods();
    } else {
      toast(r.error || '修改失败', 'err');
    }
  });

  if (m.aliased) {
    addItem('恢复原始名称', async () => {
      const r = await post('/api/mod/rename', { modFile: m.modFile, alias: '' });
      if (r.ok) { toast('已恢复原始名称', 'ok'); await refreshMods(); }
      else toast(r.error || '恢复失败', 'err');
    });
  }

  // 复制模组显示名称（本地/工坊都有，直接复制列表里看到的名字）
  addItem('复制 MOD 名称', () => {
    copyText(m.name);
    toast('已复制 MOD 名称：' + m.name, 'ok');
  });

  if (steamId) {
    addItem('复制 MOD ID', () => {
      copyText(steamId);
      toast('已复制 MOD ID：' + steamId, 'ok');
    });
  }

  addItem('打开 MOD 目录', () => {
    const p = m.dirPath || (S.state.userDir + '/mod');
    post('/api/open', { path: p });
  });

  // 打包成可分享的 zip（结构与本启动器的「拖入安装」兼容）
  addItem('打包此 MOD（导出 zip）', () => packMod(m));

  // 收藏到收藏夹（子菜单）
  addItem('收藏到收藏夹 ▶', () => {
    openColSubmenu(m, anchorEl);
  });

  // 来源标签手动修复
  const curSrc = m.source || '';
  if (curSrc === 'steam') {
    addItem('标记为本地', async () => {
      const r = await post('/api/mod/source', { modFile: m.modFile, source: 'local' });
      if (r.ok) { toast('已标记为本地', 'ok'); await refreshMods(); }
      else toast(r.error || '标记失败', 'err');
    });
  } else if (curSrc === 'local') {
    addItem('标记为工坊', async () => {
      const r = await post('/api/mod/source', { modFile: m.modFile, source: 'steam' });
      if (r.ok) { toast('已标记为工坊', 'ok'); await refreshMods(); }
      else toast(r.error || '标记失败', 'err');
    });
  }
  if (m.sourceOverridden) {
    addItem('恢复自动判定来源', async () => {
      const r = await post('/api/mod/source', { modFile: m.modFile, source: '' });
      if (r.ok) { toast('已恢复自动判定', 'ok'); await refreshMods(); }
      else toast(r.error || '恢复失败', 'err');
    });
  }

  if (steamId) {
    addItem('打开创意工坊页面', () => {
      post('/api/url', { url: `https://steamcommunity.com/sharedfiles/filedetails/?id=${steamId}` });
    });
  }

  // 删除此 MOD（红色，走回收站可还原）
  const sep = document.createElement('div');
  sep.className = 'mm-sep';
  menu.appendChild(sep);
  addItem('删除此 MOD', async () => {
    const tip = (m.source === 'steam')
      ? '\n\n注意：这是创意工坊模组，删掉后只要 Steam 里还订阅着，下次启动游戏会被重新下载回来。'
      : '';
    const ok = await ui.confirm(
      `${m.name}${tip}\n\n${T('描述文件和内容会移到回收站，可以还原。')}`,
      { title: TT('删除这个 MOD？'), okText: TT('删除'), danger: true }
    );
    if (!ok) return;
    const r = await post('/api/mod/delete', { modFile: m.modFile });
    if (!r.ok) { toast(r.error || '删除失败', 'err'); return; }
    // 顺手从当前播放集里摘掉，免得播放集引用已删除的文件
    S.play = S.play.filter(x => (x.modFile || '').toLowerCase() !== (m.modFile || '').toLowerCase());
    paintLists();
    scheduleSave();
    toast('已删除（移到回收站，可还原）', 'ok');
    await refreshMods();
  }, 'danger');

  // 定位到按钮旁
  const rect = anchorEl.getBoundingClientRect();
  document.body.appendChild(menu);
  const mw = menu.offsetWidth, mh = menu.offsetHeight;
  let x = rect.right - mw;
  let y = rect.bottom + 4;
  if (x < 8) x = 8;
  if (y + mh > window.innerHeight - 8) y = rect.top - mh - 4;
  if (y < 8) y = 8;
  menu.style.left = x + 'px';
  menu.style.top = y + 'px';

  // 点击其他地方关闭
  const close = (ev) => {
    if (!menu.contains(ev.target)) {
      document.removeEventListener('mousedown', close);
      menu.remove();
    }
  };
  setTimeout(() => document.addEventListener('mousedown', close), 0);
}

/* 收藏到收藏夹子菜单 */
function openColSubmenu(m, anchorEl) {
  document.querySelectorAll('.mod-menu').forEach(x => x.remove());
  const menu = document.createElement('div');
  menu.className = 'mod-menu';
  menu.innerHTML = `<div class="mm-title">${esc(TT('收藏到收藏夹'))}</div><div class="mm-sep"></div>`;

  const addItem = (label, fn, cls = '') => {
    const d = document.createElement('div');
    d.className = 'mm-item ' + cls;
    d.textContent = TT(label);
    d.onclick = () => { menu.remove(); fn(); };
    menu.appendChild(d);
  };

  const myCols = m.collections || [];
  S.collections.forEach(c => {
    const inIt = myCols.includes(c.name);
    addItem(`${inIt ? '✓ ' : ''}${c.name} (${c.count})`, async () => {
      const r = await post('/api/collection/toggle', { name: c.name, modFile: m.modFile });
      if (r.ok) {
        toast(r.added ? `已收藏到「${c.name}」` : `已从「${c.name}」移除`, 'ok');
        await refreshMods();
        await loadCollectionsForMods();
      } else toast('操作失败', 'err');
    });
  });

  addItem('＋ 新建收藏夹', async () => {
    const name = await ui.prompt('输入名字（比如 1、2、3，随便起）：', '', { title: TT('新建收藏夹') });
    if (!name) return;
    const r = await post('/api/collection/create', { name: name.trim() });
    if (r.ok) { toast('收藏夹已创建', 'ok'); await loadCollectionsForMods(); }
    else toast(r.error || '创建失败', 'err');
  });

  const rect = anchorEl.getBoundingClientRect();
  document.body.appendChild(menu);
  const mw = menu.offsetWidth, mh = menu.offsetHeight;
  let x = rect.right - mw;
  let y = rect.bottom + 4;
  if (x < 8) x = 8;
  if (y + mh > window.innerHeight - 8) y = rect.top - mh - 4;
  if (y < 8) y = 8;
  menu.style.left = x + 'px';
  menu.style.top = y + 'px';
  const close = (ev) => {
    if (!menu.contains(ev.target)) {
      document.removeEventListener('mousedown', close);
      menu.remove();
    }
  };
  setTimeout(() => document.addEventListener('mousedown', close), 0);
}

// 刷新收藏夹列表（供 mod 菜单收藏后刷新标签栏）
async function loadCollectionsForMods() {
  try {
    const r = await api('/api/collections');
    S.collections = r.items || [];
  } catch (e) {}
}

async function refreshMods() {
  const d = await api('/api/mods?force=1');
  S.mods = d.items || [];
  S.modByKey = new Map(S.mods.map(m => [m.modFile.toLowerCase(), m]));
  // 同步播放集里的名字
  const byKey = new Map(S.mods.map(m => [m.modFile.toLowerCase(), m]));
  S.play.forEach(p => {
    const m = byKey.get((p.modFile || '').toLowerCase());
    if (m) { p.name = m.name; p.thumb = m.thumb; p.steamId = m.steamId; p.source = m.source; }
  });
  paintLists();
}

function copyText(text) {
  try {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).catch(() => fallbackCopy(text));
    } else {
      fallbackCopy(text);
    }
  } catch (e) { fallbackCopy(text); }
}

function fallbackCopy(text) {
  const ta = document.createElement('textarea');
  ta.value = text;
  ta.style.position = 'fixed';
  ta.style.opacity = '0';
  document.body.appendChild(ta);
  ta.select();
  try { document.execCommand('copy'); } catch (e) {}
  document.body.removeChild(ta);
}

/* ---------------- 导出 Mod 压缩包（带进度） ---------------- */

function openPackPanel(m) {
  const mask = document.createElement('div');
  mask.className = 'modal-mask';
  mask.innerHTML = `
    <div class="modal pack-panel">
      <div class="modal-head">导出 Mod 压缩包</div>
      <div class="modal-body">
        <div class="pack-name">${esc(m.name || m.modFile || '')}</div>
        <div class="pack-bar indet"><i></i></div>
        <div class="pack-row">
          <span id="pkPhase">正在准备…</span>
          <span><b id="pkPct">0%</b><span id="pkCnt"></span></span>
        </div>
        <div class="pack-file" id="pkFile"></div>
      </div>
      <div class="modal-foot">
        <button class="btn ghost" id="pkClose" disabled>关闭</button>
      </div>
    </div>`;
  document.body.appendChild(mask);
  requestAnimationFrame(() => mask.classList.add('show'));

  const bar = mask.querySelector('.pack-bar');
  const fill = bar.querySelector('i');
  const elPhase = mask.querySelector('#pkPhase');
  const elPct = mask.querySelector('#pkPct');
  const elCnt = mask.querySelector('#pkCnt');
  const elFile = mask.querySelector('#pkFile');
  const elClose = mask.querySelector('#pkClose');
  let closed = false;

  const close = () => {
    if (closed) return;
    closed = true;
    mask.classList.remove('show');
    setTimeout(() => mask.remove(), 160);
  };
  elClose.onclick = close;

  return {
    close,
    phase(text) { elPhase.textContent = text; },
    indeterminate(on) { bar.classList.toggle('indet', !!on); },
    progress(done, total, cur) {
      const pct = total > 0 ? Math.min(100, Math.round(done * 100 / total)) : 0;
      bar.classList.remove('indet');
      fill.style.width = pct + '%';
      elPct.textContent = pct + '%';
      elCnt.textContent = total ? ` · ${done} / ${total} 个文件` : '';
      if (cur) elFile.textContent = cur;
    },
    done(r) {
      bar.classList.remove('indet');
      fill.style.width = '100%';
      fill.style.background = 'linear-gradient(90deg,#2f7a45,#4ec97a)';
      elPct.textContent = '100%';
      elPhase.textContent = '打包完成';
      elPhase.className = 'pack-done';
      elCnt.textContent = ` · ${r.fileCount} 个文件 · ${((r.sizeBytes || 0) / 1048576).toFixed(1)}MB`;
      elFile.textContent = r.path || '';
      elClose.disabled = false;
      elClose.textContent = '完成';
    },
    fail(msg) {
      bar.classList.remove('indet');
      fill.style.width = '100%';
      fill.style.background = 'linear-gradient(90deg,#7a2f2f,#e5534b)';
      elPhase.textContent = msg || '打包失败';
      elPhase.className = 'pack-err';
      elClose.disabled = false;
      elClose.textContent = '关闭';
    },
  };
}

async function packMod(m) {
  const p = openPackPanel(m);
  p.phase('正在统计文件…（随后会弹出保存位置）');

  let prep;
  try {
    prep = await post('/api/mod/pack/prepare', { modFile: m.modFile });
  } catch (e) {
    p.fail('准备失败：' + e.message);
    return;
  }
  if (prep.cancelled) { p.close(); return; }
  if (!prep.ok) { p.fail(prep.error || '打包失败'); return; }

  p.indeterminate(true);
  p.phase('正在压缩…');
  p.progress(0, prep.fileCount, '');

  // 轮询后端真实进度
  let stopped = false;
  const timer = setInterval(async () => {
    if (stopped) return;
    try {
      const st = await api('/api/mod/pack/progress');
      if (st && st.phase === 'packing') {
        p.progress(st.done || 0, st.total || prep.fileCount, st.cur || '');
      }
    } catch (e) { /* 轮询失败无所谓，最后结果以 run 的返回为准 */ }
  }, 200);

  let r;
  try {
    r = await post('/api/mod/pack/run', { modFile: m.modFile, path: prep.path });
  } catch (e) {
    r = { ok: false, error: e.message };
  }
  stopped = true;
  clearInterval(timer);

  if (!r.ok) { p.fail(r.error || '打包失败'); return; }

  p.done(r);
  toast(`已导出 ${r.fileCount} 个文件（${((r.sizeBytes || 0) / 1048576).toFixed(1)}MB）到：${r.path}`, 'ok');
  setTimeout(() => { if (r.path) post('/api/open', { path: r.path, reveal: true }); }, 250);
  setTimeout(() => p.close(), 6000);
}

/* ---------------- 拖拽排序 ---------------- */
let drag = null;

function initDrag() {
  ['leftBody', 'rightBody'].forEach(id => {
    const body = $('#' + id);
    if (!body) return;
    body.addEventListener('pointerdown', onDown);
  });
}

function onDown(e) {
  if (e.button !== 0) return;
  if (e.target.closest('button, .toggle, .icon-btn, input')) return;
  const row = e.target.closest('.modrow');
  if (!row) return;
  const fromRight = row.dataset.src === 'right';
  if (row.dataset.drag !== '1') return;

  cleanupDrag();   // 先清掉上一次可能残留的状态

  drag = {
    row, fromRight, key: row.dataset.key,
    startX: e.clientX, startY: e.clientY,
    started: false, line: null, offsetY: e.clientY - row.getBoundingClientRect().top,
  };
  // 捕获指针：鼠标移出窗口再松开也能收到 pointerup，否则监听会永远挂着
  try { row.setPointerCapture(e.pointerId); } catch (_) {}
  document.addEventListener('pointermove', onMove);
  document.addEventListener('pointerup', onUp);
  document.addEventListener('pointercancel', onUp);
}

/* 彻底清除拖拽状态：幽灵、指示线、监听器、高亮 —— 切页面时也要调 */
function cleanupDrag() {
  document.removeEventListener('pointermove', onMove);
  document.removeEventListener('pointerup', onUp);
  document.removeEventListener('pointercancel', onUp);
  const g = $('#ghost');
  if (g) {
    g.hidden = true;
    g.innerHTML = '';
    g.style.left = '-9999px';
    g.style.top = '-9999px';
  }
  if (drag) {
    if (drag.row) drag.row.classList.remove('dragging');
    if (drag.line && drag.line.parentNode) drag.line.remove();
  }
  $$('.modcol-body').forEach(b => b.classList.remove('drop-hot'));
  drag = null;
}

function onMove(e) {
  if (!drag) return;
  if (!drag.started) {
    if (Math.abs(e.clientY - drag.startY) < 4 && Math.abs(e.clientX - drag.startX) < 4) return;
    startDrag();
  }
  e.preventDefault();
  const g = $('#ghost');
  g.style.left = (e.clientX + 14) + 'px';
  g.style.top = (e.clientY - 18) + 'px';
  updateDropTarget(e.clientX, e.clientY);
  autoScroll(e.clientY);
}

function startDrag() {
  drag.started = true;
  drag.row.classList.add('dragging');
  const g = $('#ghost');
  const t = drag.row.querySelector('.thumb');
  const name = drag.row.querySelector('.nm')?.textContent || drag.key;
  g.innerHTML = (t && t.tagName === 'IMG' ? `<img src="${t.src}">` : '') + `<span>${esc(name)}</span>`;
  g.hidden = false;
  if (!drag.line) {
    drag.line = document.createElement('div');
    drag.line.className = 'drop-line';
  }
}

function updateDropTarget(x, y) {
  const rightBody = $('#rightBody');
  const leftBody = $('#leftBody');
  if (!rightBody) return;
  const rb = rightBody.getBoundingClientRect();
  const inside = x >= rb.left - 40 && x <= rb.right + 40 && y >= rb.top - 20 && y <= rb.bottom + 20;

  [rightBody, leftBody].forEach(b => b && b.classList.toggle('drop-hot', inside && b === rightBody));

  if (!inside) {
    if (drag.line && drag.line.parentNode) drag.line.remove();
    drag.target = null;
    return;
  }
  const rows = [...rightBody.querySelectorAll('.modrow')].filter(r => r !== drag.row);
  let before = null;
  for (const r of rows) {
    const box = r.getBoundingClientRect();
    if (y < box.top + box.height / 2) { before = r; break; }
  }
  drag.target = { before };
  if (before) rightBody.insertBefore(drag.line, before);
  else rightBody.appendChild(drag.line);
}

function autoScroll(y) {
  const body = $('#rightBody');
  if (!body) return;
  const b = body.getBoundingClientRect();
  if (y < b.top + 40) body.scrollTop -= 14;
  else if (y > b.bottom - 40) body.scrollTop += 14;
}

function onUp() {
  if (!drag) { cleanupDrag(); return; }
  const willReorder = drag.started && drag.target && drag.fromRight;
  const willAdd = drag.started && drag.target && !drag.fromRight;
  const line = drag.line;
  const row = drag.row;
  const key = (drag.key || '').toLowerCase();
  const beforeEl = drag.target ? drag.target.before : null;

  if (willReorder) {
    const rb = $('#rightBody');
    if (line && line.parentNode === rb) line.replaceWith(row);
    else if (row) rb.appendChild(row);
  }

  // 先清理拖拽 UI，再做数据更新，避免中途异常导致幽灵残留
  cleanupDrag();

  if (willReorder) {
    const order = $$('#rightBody .modrow').map(r => r.dataset.key);
    const map = new Map(S.play.map(x => [String(x.uid || x.modFile), x]));
    const next = order.map(k => map.get(k)).filter(Boolean);
    if (next.length === S.play.length) S.play = next;
    paintLists();
    scheduleSave();
  } else if (willAdd) {
    const m = S.modByKey.get(key);
    if (m && !S.play.some(x => (x.modFile || '').toLowerCase() === key)) {
      let insertAt = S.play.length;
      if (beforeEl) {
        const idx = S.play.findIndex(x => String(x.uid || x.modFile) === beforeEl.dataset.key);
        if (idx >= 0) insertAt = idx;
      }
      S.play.splice(insertAt, 0, {
        uid: 'tmp:' + m.modFile, modId: null, modFile: m.modFile, enabled: true,
        name: m.name, thumb: m.thumb, steamId: m.steamId,
        supportedVersion: m.supportedVersion, compat: m.compat,
        source: m.source, pending: m.pending,
      });
      paintLists();
      scheduleSave();
    }
  }
}

/* ---------------- 保存 ---------------- */
function scheduleSave() {
  clearTimeout(S.dirtyTimer);
  saveState('有改动，等待保存…', true);
  S.dirtyTimer = setTimeout(savePlayset, 700);
}

async function savePlayset() {
  if (S.saving) { scheduleSave(); return; }
  const id = S.state.activePlayset || ((S.state.playsets || [])[0] || {}).id;
  if (!id) return;
  S.saving = true;
  saveState('保存中…', true);
  try {
    const r = await post('/api/playset/save', {
      id,
      items: S.play.map(x => ({ modId: x.modId || null, modFile: x.modFile, enabled: x.enabled })),
    });
    if (!r.ok) { toast(r.error || '保存失败', 'err'); saveState('保存失败', false); return; }
    saveState(`已保存 ${r.saved} 个模组${r.synced ? ' · 已同步到游戏' : ' · 未写入游戏（播放集未激活）'}`);
    refreshStateQuiet();
    // 新加入的条目拿回真实 modId，避免下次保存重复创建记录
    if (S.play.some(x => !x.modId)) {
      try {
        const d = await api('/api/playset?id=' + encodeURIComponent(id));
        if (d.ok && d.items.length === S.play.length) {
          d.items.forEach((it, i) => {
            if (S.play[i]) { S.play[i].modId = it.modId; S.play[i].uid = it.uid || it.modFile; }
          });
        }
      } catch (e) {}
    }
  } catch (e) {
    toast('保存失败：' + e.message, 'err');
    saveState('保存失败', false);
  } finally {
    S.saving = false;
  }
}

async function refreshState() { S.state = await api('/api/state'); paintTop(); }
async function refreshStateQuiet() { try { S.state = await api('/api/state'); paintTop(); } catch (e) {} }

/* ---------------- Mod 冲突排查 ---------------- */

function cfProgressHtml() {
  return '<div class="cf-progress">' +
    '<div class="cf-pg-bar"><div class="cf-pg-fill" id="cfPgFill"></div></div>' +
    '<div class="cf-pg-text" id="cfPgText">' + esc(TT('正在扫描启用的模组…')) + ' 0 / 0</div>' +
    '</div>';
}
function updateCfProgress(scanned, total) {
  const fill = document.getElementById('cfPgFill');
  const txt = document.getElementById('cfPgText');
  if (fill) fill.style.width = (total ? Math.round((scanned / total) * 100) : 0) + '%';
  if (txt) txt.textContent = TT('已扫描') + ' ' + scanned + ' / ' + TT('共') + ' ' + total;
}
async function fetchConflictStream(playsetId, onProgress, onDone) {
  const resp = await fetch('/api/mod/conflicts/stream?playset=' + encodeURIComponent(playsetId));
  if (!resp.ok) throw new Error('HTTP ' + resp.status);
  const reader = resp.body.getReader();
  const dec = new TextDecoder('utf-8');
  let buf = '';
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let nl;
    while ((nl = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, nl).trim();
      buf = buf.slice(nl + 1);
      if (!line) continue;
      let obj;
      try { obj = JSON.parse(line); } catch (e) { continue; }
      if (obj.type === 'progress') onProgress(obj.scanned | 0, obj.total | 0);
      else if (obj.type === 'done') onDone(obj.result || {});
      else if (obj.type === 'error') throw new Error(obj.error || 'stream error');
    }
  }
}

async function openConflictModal() {
  const id = S.state.activePlayset || ((S.state.playsets || [])[0] || {}).id;
  if (!id) { toast(TT('当前没有激活的播放集'), 'err'); return; }

  const mask = document.createElement('div');
  mask.className = 'modal-mask';
  mask.innerHTML = `
    <div class="modal conflict-panel">
      <div class="modal-head">${esc(TT('冲突排查'))} · <span id="cfPsName"></span></div>
      <div class="modal-body">
        <div class="cf-notice">⚠ ${esc(TT('检测结果由自动比对生成，仅供参考，请以游戏内实际表现为准'))}</div>
        <div class="cf-summary" id="cfSummary">${esc(TT('正在扫描启用的模组…'))}</div>
        <div id="cfContent"></div>
      </div>
      <div class="modal-foot">
        <button class="btn ghost" data-m="close">${esc(TT('关闭'))}</button>
      </div>
    </div>`;
  document.body.appendChild(mask);
  requestAnimationFrame(() => mask.classList.add('show'));
  const close = () => { mask.classList.remove('show'); setTimeout(() => mask.remove(), 130); };
  mask.querySelector('[data-m="close"]').onclick = close;
  mask.addEventListener('mousedown', e => { if (e.target === mask) close(); });

  const cfContent = mask.querySelector('#cfContent');
  const cfSummary = mask.querySelector('#cfSummary');

  async function run() {
    cfSummary.innerHTML = cfProgressHtml();
    cfContent.innerHTML = '';
    const bindSwap = () => cfContent.querySelectorAll('[data-swap]').forEach(btn => {
      btn.onclick = async () => {
        const parts = (btn.dataset.swap || '').split('|');
        if (parts.length < 2) return;
        btn.disabled = true; btn.textContent = TT('调整中…');
        await prioritizeMod(parts[0], parts[1]);
        await run();
      };
    });
    const bindCfTools = () => {
      cfContent.querySelectorAll('.cf-filter button').forEach(b => {
        b.onclick = () => {
          cfContent.querySelectorAll('.cf-filter button').forEach(x => x.classList.remove('on'));
          b.classList.add('on');
          const f = b.dataset.f;
          cfContent.querySelectorAll('.cf-pair').forEach(p => {
            p.style.display = (f === 'all' || p.dataset.sev === f) ? '' : 'none';
          });
        };
      });
      cfContent.querySelectorAll('.cf-toggle').forEach(b => {
        b.onclick = () => {
          const body = b.closest('.cf-pair').querySelector('.cf-pair-b');
          const open = body.style.display !== 'none';
          body.style.display = open ? 'none' : '';
          b.textContent = (open ? TT('展开') : TT('收起')) + ' ' + (b.dataset.n || '');
        };
      });
    };
    try {
      await fetchConflictStream(id,
        (s, t) => updateCfProgress(s, t),
        (r) => {
          if (!r.ok) { cfSummary.innerHTML = TT('排查失败：') + esc(r.error || ''); return; }
          const nm = mask.querySelector('#cfPsName'); if (nm) nm.textContent = r.playset ? r.playset.name : '';
          cfSummary.innerHTML = cfSummaryHtml(r);
          cfContent.innerHTML = renderConflicts(r);
          bindSwap();
          bindCfTools();
        });
    } catch (e) {
      cfSummary.innerHTML = TT('排查失败：') + esc((e && e.message) || e);
    }
  }
  await run();
}

function renderConflicts(r) {
  let h = '';
  const fc = r.fileConflicts || [];
  h += '<div class="cf-sec"><div class="cf-sec-t">' + TT('文件冲突') + ' (' + fc.length + ') ' +
    '<span class="cf-hint">' + TT('按「获胜 ▸ 被覆盖」配对合并，每组列出所有被覆盖的文件') + '</span></div>';
  if (!fc.length) h += '<div class="cf-empty">' + TT('未发现文件冲突') + '</div>';
  else {
    const groups = {};
    for (const c of fc) {
      const w = c.winner || '';
      const wId = c.winnerModId || '';
      const sev = c.severity === '高' ? 'hi' : c.severity === '中' ? 'mid' : 'lo';
      const rank = sev === 'hi' ? 3 : sev === 'mid' ? 2 : 1;
      (c.losers || []).forEach((L, i) => {
        const lId = (c.loserModIds || [])[i] || '';
        const key = w + '\u0000' + L;
        if (!groups[key]) groups[key] = { winner: w, winnerModId: wId, loser: L, loserModId: lId, files: [], rank: 0 };
        groups[key].files.push({ rel: c.rel, sev: sev });
        if (rank > groups[key].rank) groups[key].rank = rank;
      });
    }
    const arr = Object.values(groups).sort((a, b) => b.rank - a.rank || b.files.length - a.files.length);
    const cnt = { hi: 0, mid: 0, lo: 0 };
    arr.forEach(g => { const s = g.rank === 3 ? 'hi' : g.rank === 2 ? 'mid' : 'lo'; cnt[s]++; });
    h += '<div class="cf-filter">';
    h += '<button data-f="all" class="on">' + TT('全部') + ' ' + arr.length + '</button>';
    h += '<button data-f="hi">' + TT('高') + ' ' + cnt.hi + '</button>';
    h += '<button data-f="mid">' + TT('中') + ' ' + cnt.mid + '</button>';
    h += '<button data-f="lo">' + TT('低') + ' ' + cnt.lo + '</button>';
    h += '</div>';
    h += '<div class="cf-list">';
    for (const g of arr) {
      const sev = g.rank === 3 ? 'hi' : g.rank === 2 ? 'mid' : 'lo';
      const expanded = sev === 'hi';
      h += '<div class="cf-pair sev-' + sev + '" data-sev="' + sev + '">';
      h += '<div class="cf-pair-h">';
      h += '<div class="cf-who">';
      h += '<div class="cf-win"><span class="cf-tag">' + TT('获胜') + '</span><span class="cf-name">' + esc(g.winner) + '</span></div>';
      h += '<div class="cf-lose"><span class="cf-tag">' + TT('被覆盖') + '</span><span class="cf-name">' + esc(g.loser) + '</span></div>';
      h += '</div>';
      h += '<button class="btn sm ghost cf-toggle" data-n="' + g.files.length + '">' + (expanded ? TT('收起') : TT('展开')) + ' ' + g.files.length + '</button>';
      h += '</div>';
      h += '<div class="cf-pair-b"' + (expanded ? '' : ' style="display:none"') + '>';
      g.files.slice(0, 500).forEach(f => {
        h += '<div class="cf-file-row sev-' + f.sev + '"><span class="cf-dot"></span><span class="cf-frel">' + esc(f.rel) + '</span></div>';
      });
      if (g.files.length > 500) h += '<div class="cf-empty">...' + TT('仅显示前 500 个文件') + '</div>';
      h += '</div>';
      h += '<div class="cf-act"><button class="btn sm" data-swap="' + esc(g.loserModId) + '|' + esc(g.winnerModId) + '">' +
        TT('让') + ' ' + esc(g.loser) + ' ' + TT('优先') + '</button></div>';
      h += '</div>';
    }
    h += '</div>';
  }
  h += '</div>';
  const lc = r.locConflicts || [];
  h += '<div class="cf-sec"><div class="cf-sec-t">' + TT('本地化键冲突') + ' (' + lc.length + ') ' +
    '<span class="cf-hint">' + TT('软冲突：后加载者覆盖前者，多为正常覆盖') + '</span></div>';
  if (!lc.length) h += '<div class="cf-empty">' + TT('未发现本地化键冲突') + '</div>';
  else {
    h += '<div class="cf-list">';
    for (const c of lc.slice(0, 200)) {
      h += '<div class="cf-row sev-lo"><div class="cf-rel">' + esc(c.key) + '</div>' +
        '<div class="cf-own">' + c.mods.map(x => esc(x)).join('、') + '</div></div>';
    }
    if (lc.length > 200) h += '<div class="cf-empty">…' + TT('仅显示前 200 条') + '</div>';
    h += '</div>';
  }
  h += '</div>';

  const md = r.missingDeps || [];
  h += '<div class="cf-sec"><div class="cf-sec-t">' + TT('依赖缺失') + ' (' + md.length + ') ' +
    '<span class="cf-hint">' + TT('尽力匹配，可能有误报') + '</span></div>';
  if (!md.length) h += '<div class="cf-empty">' + TT('未发现依赖缺失') + '</div>';
  else {
    h += '<div class="cf-list">';
    for (const c of md) {
      h += '<div class="cf-row sev-mid"><div class="cf-rel">' + esc(c.mod) + '</div>' +
        '<div class="cf-own">' + TT('缺少') + ': ' + c.deps.map(x => esc(x)).join('、') + '</div></div>';
    }
    h += '</div>';
  }
  h += '</div>';

  const rp = r.replacePaths || [];
  if (rp.length) {
    h += '<div class="cf-sec"><div class="cf-sec-t">' + TT('replace_path 提示') + ' (' + rp.length + ') ' +
      '<span class="cf-hint">' + TT('整目录替换原版，可能清除其它 mod 的同类文件') + '</span></div>';
    h += '<div class="cf-list">';
    for (const c of rp) {
      h += '<div class="cf-row sev-mid"><div class="cf-rel">' + esc(c.mod) + '</div>' +
        '<div class="cf-own">replace_path: ' + c.paths.map(x => esc(x)).join('、') + '</div></div>';
    }
    h += '</div></div>';
  }
  return h;
}

// 顶部摘要：扫描了多少模组/文件 + 各类冲突计数（内联样式，自包含不依赖额外 CSS）
function cfSummaryHtml(r) {
  const fc = r.fileConflicts || [], lc = r.locConflicts || [],
        md = r.missingDeps || [], rp = r.replacePaths || [];
  let h = '';
  h += '<div style="font-size:calc(13px*var(--fs-scale));margin-bottom:8px">' + TT('已扫描') + ' <b>' + (r.scannedMods || 0) +
    '</b> ' + TT('个启用模组') + ' · ' + TT('共') + ' <b>' + (r.totalFiles || 0) + '</b> ' + TT('个文件') + '</div>';
  const stat = (label, n, bg, fg) =>
    '<span style="font-size:calc(12px*var(--fs-scale));padding:2px 8px;border-radius:10px;margin-right:6px;background:' + bg + ';color:' + fg + '">' +
    label + ' <b>' + n + '</b></span>';
  h += '<div>';
  h += stat(TT('文件冲突'), fc.length, '#c0392b', '#fff');
  h += stat(TT('本地化键冲突'), lc.length, '#f0d98a', '#8a6d00');
  h += stat(TT('依赖缺失'), md.length, '#e7f0ff', '#2a6fdb');
  if (rp.length) h += stat(TT('replace_path'), rp.length, '#eeeeee', '#666666');
  h += '</div>';
  return h;
}

async function prioritizeMod(loserMf, winnerMf) {
  const norm = s => (s || '').toLowerCase();
  const match = (x, v) => x && (norm(x.modId) === norm(v) || norm(x.modFile) === norm(v));
  const wi = S.play.findIndex(x => match(x, winnerMf));
  const li = S.play.findIndex(x => match(x, loserMf));
  if (wi < 0 || li < 0) { toast(TT('未在播放集中找到对应模组'), 'err'); return; }
  const [loser] = S.play.splice(li, 1);
  const wi2 = S.play.findIndex(x => match(x, winnerMf));
  S.play.splice(wi2 + 1, 0, loser);   // 把被覆盖方移到获胜方之后 → 它变成获胜方
  paintLists();
  await savePlayset();
  toast(TT('已调整加载顺序，请重新进游戏生效'), 'ok');
}

/* ---------------- 播放集 ---------------- */
function renderPlaysets() {
  const list = S.state.playsets || [];
  $('#view').innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">播放集</div>
        <div class="page-sub">播放集就是一套「启用哪些模组 + 加载顺序」的配置，可导入导出与朋友分享。</div>
      </div>
      <div class="bar">
        <button class="btn" id="psNew">＋ 新建</button>
        <button class="btn ghost" id="psImport">导入播放集</button>
        <button class="btn ghost" id="psExport" ${list.length ? '' : 'disabled'}>导出当前</button>
      </div>
    </div>
    <div class="ps-list" id="psList">
      ${list.map(p => `
        <div class="ps-row ${p.isActive ? 'active' : ''}" data-id="${p.id}">
          <span class="dot"></span>
          <div class="nm">
            <strong>${esc(p.name)}</strong>
            <small>${p.total} 个模组 · ${p.enabled} 启用 · 更新于 ${fmtTime(p.updatedOn)}</small>
          </div>
          <div class="acts">
            <button class="btn sm ghost" data-a="use">${p.isActive ? '当前' : '启用'}</button>
            <button class="btn sm ghost" data-a="clone">克隆</button>
            <button class="btn sm ghost" data-a="rename">重命名</button>
            <button class="btn sm ghost" data-a="export">导出</button>
            <button class="btn sm danger" data-a="del">删除</button>
          </div>
        </div>`).join('') || '<div class="empty"><div class="big">❏</div>还没有播放集</div>'}
    </div>`;

  $('#psNew').onclick = async () => {
    const name = await ui.prompt('给新播放集起个名字', '我的播放集 ' + (list.length + 1));
    if (!name) return;
    const clone = await ui.confirm('是否复制当前播放集的模组与顺序？\n\n确定 = 连同模组一起复制\n取消 = 创建一个空白播放集', {
      title: '创建播放集', okText: '复制当前', cancelText: '空白创建',
    });
    const r = await post('/api/playset/create', { name, cloneFrom: clone ? S.state.activePlayset : null });
    if (!r.ok) { toast(r.error || '新建播放集失败', 'err'); return; }
    await refreshState(); render(); toast('已创建：' + name, 'ok');
  };
  $('#psExport').onclick = () => exportPlayset(S.state.activePlayset);
  $('#psImport').onclick = importPlayset;

  $$('#psList .ps-row').forEach(row => {
    const id = row.dataset.id;
    $$('button[data-a]', row).forEach(b => b.onclick = async () => {
      const a = b.dataset.a;
      if (a === 'use') {
        const r = await post('/api/playset/activate', { id });
        if (!r.ok) { toast(r.error || '切换播放集失败', 'err'); return; }
        await refreshState(); render(); toast('已切换并同步到游戏', 'ok');
      } else if (a === 'clone') {
        const base = row.querySelector('strong').textContent;
        const copySuffix = i18nLang() === 'zh' ? ' 的副本' : T('的副本');
        const name = await ui.prompt(T('克隆为'), base + copySuffix, { title: T('克隆播放集') });
        if (!name) return;
        const r = await post('/api/playset/create', { name, cloneFrom: id });
        if (!r.ok) { toast(r.error || '克隆播放集失败', 'err'); return; }
        await refreshState(); render(); toast('已克隆', 'ok');      } else if (a === 'rename') {
        const name = await ui.prompt('新的名称', row.querySelector('strong').textContent, { title: '重命名' });
        if (!name) return;
        const r = await post('/api/playset/rename', { id, name });
        if (!r.ok) { toast(r.error || '重命名失败', 'err'); return; }
        await refreshState(); render();
      } else if (a === 'export') {
        await exportPlayset(id);
      } else if (a === 'del') {
        const ok = await ui.confirm(`确定删除播放集「${row.querySelector('strong').textContent}」？\n\n删除前会自动备份 launcher-v2.sqlite，可从备份目录恢复。`, {
          title: '删除播放集', okText: '删除', danger: true,
        });
        if (!ok) return;
        const r = await post('/api/playset/delete', { id });
        if (!r.ok) { toast(r.error || '删除播放集失败', 'err'); return; }
        await refreshState(); render(); toast('已删除', 'ok');
      }
    });
  });
}

/* ---------------- DLC ---------------- */
// 开关自检结果面板：逐个列出「期望状态 vs 文件实际状态」不一致的 DLC
function showDlcVerify(r, quiet) {
  const el = $('#dlcVerify');
  if (!el) return;
  if (!r || r.ok === false) {
    el.innerHTML = `<div class="patch-card patch-warn"><span class="patch-dot"></span>
      <div class="patch-tx"><b>开关自检失败</b><small>${esc((r && r.error) || '未知错误')}</small></div></div>`;
    if (!quiet) toast((r && r.error) || '开关自检失败', 'err');
    return;
  }
  if (!r.mismatched) {
    el.innerHTML = `<div class="patch-card patch-none"><span class="patch-dot"></span>
      <div class="patch-tx"><b>开关自检通过</b><small>检查了 ${r.checked} 个已安装 DLC，实际开关状态与播放集设置完全一致。</small></div></div>`;
    if (!quiet) toast(`自检通过：${r.checked} 个 DLC 开关均已生效`, 'ok');
    return;
  }
  const list = (r.mismatches || []).slice(0, 30).map((m) => `
    <div style="padding:5px 0;border-top:1px solid rgba(255,255,255,.07)">
      <div style="color:var(--fg)">${esc(m.name)} <span style="color:var(--dim-2);font-size:calc(10px*var(--fs-scale))">${esc(m.dir)}</span></div>
      <div style="color:#ff9a94">期望${m.desired ? '启用' : '关闭'} · 实际${m.actual ? '启用' : '关闭'} · ${esc(m.reason || '')}</div>
    </div>`).join('');
  el.innerHTML = `<div class="patch-card patch-warn" style="align-items:flex-start">
    <span class="patch-dot"></span>
    <div class="patch-tx"><b>自检未通过：${r.mismatched} / ${r.checked} 个 DLC 的开关没有生效</b>
      <small>常见原因：游戏或官方启动器仍在运行（文件被占用）、游戏目录没有写权限，或目录里同时存在 .dlc 和 .dlc.disabled 重复文件。</small>
      <div style="margin-top:8px;max-height:220px;overflow:auto;font-size:calc(11px*var(--fs-scale));line-height:1.6">${list}</div>
      ${r.mismatches.length > 30 ? `<small>仅显示前 30 个，共 ${r.mismatches.length} 个。</small>` : ''}
    </div>
    <button class="btn sm ghost" id="dFix">按期望状态修复</button>
  </div>`;
  if (!quiet) toast(`自检发现 ${r.mismatched} 个 DLC 开关未生效`, 'err');
  const fx = el.querySelector('#dFix');
  if (fx) fx.onclick = async () => {
    fx.disabled = true;
    const map = {};
    (r.items || []).forEach((x) => { map[x.popsId] = x.desired; });
    try {
      const rr = await post('/api/dlcs/set', { dlcs: map });
      if (rr.ok && !rr.dlcFilesFailed) {
        toast('已按期望状态重新写入，正在复查…', 'ok');
      } else if (rr.ok) {
        toast(`修复完成，但仍有 ${rr.dlcFilesFailed} 个没能改动：${(rr.failed && rr.failed[0] && rr.failed[0].reason) || '未知原因'}`, 'err');
      } else {
        toast(rr.error || '修复失败', 'err');
      }
      const r2 = await post('/api/dlcs/verify', {});
      showDlcVerify(r2);
      render();
    } catch (err) {
      toast('修复失败：' + err.message, 'err');
      fx.disabled = false;
    }
  };
}

async function renderDlc() {
  $('#view').innerHTML = `<div class="page-head"><div><div class="page-title">DLC 管理</div>
    <div class="page-sub">DLC 清单、封面、介绍和「是否已购买」的判定结果，<b>校验一次就会存到本地</b>，
      之后打开都直接读本地、不再问 Steam。想重新核对（比如刚在 Steam 买了新 DLC），点「重新校验」。</div></div>
    <div class="bar">
    <button class="btn sm ghost" id="dOpenDir" title="在资源管理器里打开本机 DLC 文件目录">打开 DLC 目录</button>
    <button class="btn sm ghost" id="dVerify" title="逐个检查 DLC 的实际开关是否与播放集设置一致">开关自检</button>
    <button class="btn sm ghost" id="dOn">全部开启</button>
    <button class="btn sm ghost" id="dOff">全部关闭</button>
    <button class="btn sm ghost" id="dRecheck" title="重新扫描本地 DLC 文件，并重新向 Steam 核对是否已购买">一键复检</button>
    <button class="btn sm ghost" id="dNames" title="自己添加或修改 DLC 的中文名（打开本地对照表文件）">自定义中文名</button>
    <button class="btn sm ghost" id="dStore">打开商店</button></div></div>
    <div id="dlcDegrade"></div>
    <div class="dlc-ov-title">校验概览</div>
    <div class="dlc-overview" id="dlcStat"></div>
    <div id="dlcPatch"></div>
    <div id="dlcNote"></div>
    <div id="dlcVerify"></div>
    <div id="dlcFilter"></div>
    <div id="dlcWrap"><div class="empty"><div class="big">◆</div>读取中…</div></div>`;

  let d;
  try { d = await api('/api/dlcs'); } catch (e) { $('#dlcWrap').innerHTML = '<div class="empty">读取失败</div>'; return; }
  const items = d.items || [];
  const patch = d.patch || {};

  // ---- Steam 不可用：顶部明确提示已降级为本地文件检测 ----
  const degradeEl = $('#dlcDegrade');
  if (degradeEl) {
    degradeEl.innerHTML = d.steamAvailable === false
      ? `<div class="dlc-degrade"><span class="dg-ic">⚠</span>
          <div><b>未能读取 Steam 库，已自动改用本地文件检测</b>
          <small>${esc(d.steamMsg || 'Steam 未安装、注册表缺失或 appmanifest 无法读取；本地 dlc 目录里有 .dlc 文件的会被判定为已拥有。')}</small></div></div>`
      : '';
  }

  // ---- 补丁状态卡（置顶）----
  const patchEl = $('#dlcPatch');
  if (patchEl) {
    if (patch.present && !patch.suspicious) {
      patchEl.innerHTML = `<div class="patch-card patch-ok">
        <span class="patch-dot"></span>
        <div class="patch-tx"><b>检测到第三方 DLC 解锁补丁</b><small>${esc(patch.type)} · 解锁 ${patch.unlockedCount} 个 DLC · 本页不锁定</small></div>
        <button class="btn sm ghost" id="pOpen">打开补丁目录</button>
      </div>`;
    } else if (patch.present && patch.suspicious) {
      patchEl.innerHTML = `<div class="patch-card patch-warn">
        <span class="patch-dot"></span>
        <div class="patch-tx"><b>补丁可能已失效</b><small>${esc(patch.detail)}</small></div>
        <button class="btn sm ghost" id="pOpen">打开补丁目录</button>
      </div>`;
    } else {
      patchEl.innerHTML = `<div class="patch-card patch-none">
        <span class="patch-dot"></span>
        <div class="patch-tx"><b>未检测到解锁补丁</b><small>DLC 按 Steam 授权正常判定</small></div>
      </div>`;
    }
    const po = patchEl.querySelector('#pOpen');
    if (po) po.onclick = () => {
      const bin = (patch.binDir || (S.state.gameRoot ? S.state.gameRoot + '/binaries' : ''));
      if (bin) post('/api/open', { path: bin });
    };
  }

  // ---- Steam 未开提示（新版已由顶部降级提示接管）----
  if (d.steamAvailable === false) {
    // 顶部已有降级提示，这里不再重复「正常判定」的文案
    $('#dlcNote').innerHTML = `<div class="hint" style="margin-bottom:12px;line-height:1.6">
      ${TT('关掉某个 DLC 会真正生效：本启动器会把它的 xxx.dlc 文件改名为 xxx.dlc.disabled，游戏就扫不到它了；重新启用会改回来。 只改扩展名、不删内容，随时可逆。')}
    </div>`;
  } else if (!d.steamOk && !patch.present) {
    const bySrc = d.bySource || {};
    const fileN = bySrc.file || 0;
    const unkN = bySrc.unknown || 0;
    const filePart = fileN ? TT('（当前 {} 个）', { v: fileN }) : '';
    const unkPart = unkN ? TT('{} 个标「无法校验」', { v: unkN }) : '';
    $('#dlcNote').innerHTML = `<div class="box warn" style="margin-bottom:12px">
      <p><b>${TT('这次没能向 Steam 校验 DLC 拥有情况')}</b>${d.steamMsg ? '：' + esc(d.steamMsg) : ''}。</p>
      <p>${TT('已回退到本地文件判断：本地 dlc 目录里有 .dlc 文件的记为「文件已存在」')}${filePart}${TT('，可正常启用；本地也没有的')}${unkPart}${TT('标「无法校验」。')}</p>
      <p>${TT('把 Steam 打开（登录状态）后点右上角「重新校验」，即可用真实授权替代文件判断。')}</p>
    </div>`;
  } else {
    $('#dlcNote').innerHTML = `<div class="hint" style="margin-bottom:12px;line-height:1.6">
      ${TT('关掉某个 DLC 会真正生效：本启动器会把它的 xxx.dlc 文件改名为 xxx.dlc.disabled，游戏就扫不到它了；重新启用会改回来。 只改扩展名、不删内容，随时可逆。')}
    </div>`;
  }

  // ---- 二维筛选状态（拥有校验 / 分类）----
  const F = { status: 'all', type: 'all', q: '' };

  // 拥有校验筛选：全部 / 已拥有 / 未拥有 / 手动标记 / 冲突
  const STATUS = [
    ['all', '全部'],
    ['owned', '已拥有'],
    ['unowned', '未拥有'],
    ['manual', '手动标记'],
    ['conflict', '冲突'],
  ];

  // 是否带用户手动标记（true / false 都算）
  const isManual = (x) => x.userOwned === true || x.userOwned === false;
  // 分类筛选：根据当前游戏实际出现的 DLC 分类动态生成（每个游戏分类不一样）
  const typeOrder = new Map();
  items.forEach(x => {
    if (!typeOrder.has(x.dlcType)) typeOrder.set(x.dlcType, dlcTypeLabel(x.dlcType, x.typeCn || x.dlcType));
  });
  const TYPES = [['all', '全部'], ...[...typeOrder.entries()]];

  const match = (x) => {
    if (F.status === 'owned' && !x.owned) return false;
    if (F.status === 'unowned' && x.owned) return false;
    if (F.status === 'manual' && !isManual(x)) return false;
    if (F.status === 'conflict' && !x.conflict) return false;
    if (F.type !== 'all' && x.dlcType !== F.type) return false;
    if (F.q) {
      const hay = (x.nameCn || '') + ' ' + (x.name || '') + ' ' + (x.dir || '');
      if (!hay.toLowerCase().includes(F.q)) return false;
    }
    return true;
  };

  const count = (fn) => items.filter(fn).length;

  const paintFilter = () => {
    const el = $('#dlcFilter');
    if (!el) return;
    const chip = (list, key) => list.map(([v, label]) => {
      const n = v === 'all' ? items.length : count(x => {
        if (key === 'status') {
          if (v === 'owned') return !!x.owned;
          if (v === 'unowned') return !x.owned;
          if (v === 'manual') return isManual(x);
          if (v === 'conflict') return !!x.conflict;
          return true;
        }
        return x.dlcType === v;
      });
      const act = F[key] === v;
      return `<button class="chip ${act ? 'active' : ''}" data-${key}="${v}">${label}<sup>${n}</sup></button>`;
    }).join('');
    el.innerHTML = `
      <div class="filter-row"><span class="filter-lab">校验</span>${chip(STATUS, 'status')}</div>
      <div class="filter-row"><span class="filter-lab">分类</span>${chip(TYPES, 'type')}
        <input class="field flt-q" type="text" placeholder="搜索名字…" value="${esc(F.q)}"></div>`;

    el.querySelectorAll('[data-status]').forEach(b => b.onclick = () => {
      F.status = b.dataset.status; paintFilter(); paint();
    });
    el.querySelectorAll('[data-type]').forEach(b => b.onclick = () => {
      F.type = b.dataset.type; paintFilter(); paint();
    });
    const qi = el.querySelector('.flt-q');
    if (qi) qi.oninput = (e) => { F.q = e.target.value.trim().toLowerCase(); paint(); };
  };

  // ---- 卡片 ----
  // 检测来源只有三种：Steam 清单 / 本地文件 / 未检测到（补丁不参与归因）
  const srcText = (x) => {
    if (x.detectSource === 'steam') return '来源：Steam 清单';
    if (x.detectSource === 'local') return '来源：本地文件';
    return '来源：未检测到';
  };

  const fmtSize = (b) => {
    if (!b || b <= 0) return '';
    if (b >= 1048576) return (b / 1048576).toFixed(1) + ' MB';
    return Math.round(b / 1024) + ' KB';
  };
  const metaLine = (x) => {
    const parts = [];
    if (x.releaseDate) parts.push(TT('发布') + ' ' + fmtDate(x.releaseDate));
    const s = fmtSize(x.sizeBytes);
    if (s) parts.push(s);
    return parts.length ? parts.join(' · ') : '';
  };

  // ---- 手动判定（拥有 / 未拥有 / 跟随检测）----
  // 统一四类状态：已拥有(绿) / 未拥有(灰) / 手动标记(金) / 手动标记与检测冲突(橙)
  const statePill = (x) => {
    if (x.conflict) {
      return `<span class="pill conflict" title="手动标记与检测结果不一致">手动 · ${x.userOwned ? '已拥有' : '未拥有'}</span>`;
    }
    if (x.owned) return `<span class="pill ok">已拥有${isManual(x) ? ' · 手动' : ''}</span>`;
    return `<span class="pill muted">未拥有${isManual(x) ? ' · 手动' : ''}</span>`;
  };

  // 用接口返回的新条目替换本地条目（保留 enabled 等前端字段）
  const applyItem = (it) => {
    const i = items.findIndex(y => y.id && it && y.id === it.id);
    if (i >= 0) items[i] = Object.assign({}, items[i], it);
  };

  // 保存手动判定；若与检测结果冲突，按用户选择保留或清除覆盖
  const setDlcOverride = async (x, owned) => {
    let r;
    try { r = await post('/api/dlc/override', { id: x.id, owned }); }
    catch (e) { toast('保存判定失败：' + e.message, 'err'); return null; }
    if (!r.ok) { toast(r.error || '保存判定失败', 'err'); return null; }
    applyItem(r.item);
    paint(); paintFilter();

    if (r.item.conflict) {
      const msg = r.item.userOwned === false
        ? '本地检测到该 DLC 文件，是否仍标记为未拥有？'
        : '本地未检测到该 DLC 的文件，是否仍以你的选择为准？';
      const keep = await ui.confirm(msg, {
        title: '判定与检测结果冲突',
        okText: '以我的选择为准',
        cancelText: '以检测结果为准',
      });
      if (!keep) {
        try {
          const r2 = await post('/api/dlc/override', { id: x.id, owned: null });
          if (r2.ok) {
            applyItem(r2.item); paint(); paintFilter();
            toast('已改为以检测结果为准', 'ok');
          } else toast(r2.error || '恢复检测结果失败', 'err');
        } catch (e) { toast('恢复检测结果失败：' + e.message, 'err'); }
        return null;
      }
      toast('已按你的选择保存', 'ok');
      return r.item;
    }

    toast(owned === null ? '已恢复为跟随检测' : '判定已保存', 'ok');
    return r.item;
  };

  // 小面板：拥有 / 未拥有 / 跟随检测
  const openOverridePanel = (x) => {
    const mask = document.createElement('div');
    mask.className = 'modal-mask';
    const cur = x.userOwned === true ? '拥有' : x.userOwned === false ? '未拥有' : '跟随检测';
    mask.innerHTML = `
      <div class="modal dlc-own-modal">
        <div class="modal-head">DLC 判定</div>
        <div class="modal-body">
          <div class="modal-msg">${esc(dlcName(x))}</div>
          <div class="modal-msg" style="color:var(--dim)">检测结果：${x.detected ? '拥有' : '未检测到'} · 当前判定：${cur}${x.conflict ? ' · 与检测结果冲突' : ''}</div>
          <div class="dlc-own-panel">
            <button class="btn ghost${x.userOwned === true ? ' cur' : ''}" data-m="own-yes">拥有（手动标记）</button>
            <button class="btn ghost${x.userOwned === false ? ' cur' : ''}" data-m="own-no">未拥有（手动标记）</button>
            <button class="btn ghost${x.userOwned == null ? ' cur' : ''}" data-m="own-auto">跟随检测结果</button>
          </div>
        </div>
        <div class="modal-foot"><button class="btn ghost" data-m="close">取消</button></div>
      </div>`;
    document.body.appendChild(mask);
    requestAnimationFrame(() => mask.classList.add('show'));

    const close = () => {
      mask.classList.remove('show');
      setTimeout(() => mask.remove(), 130);
    };
    mask.querySelector('[data-m="close"]').onclick = close;
    mask.addEventListener('mousedown', (e) => { if (e.target === mask) close(); });

    const bind = (sel, val) => {
      mask.querySelector(sel).onclick = async () => {
        close();
        await setDlcOverride(x, val);
      };
    };
    bind('[data-m="own-yes"]', true);
    bind('[data-m="own-no"]', false);
    bind('[data-m="own-auto"]', null);
  };

  const paint = () => {
    const st = $('#dlcStat');
    if (st) {
      // 校验概览：五个数字块，点击即按对应条件筛选
      const totalN = items.length;
      const ownN = count(x => x.owned);
      const unownN = totalN - ownN;
      const manualN = count(isManual);
      const conflictN = count(x => x.conflict);
      const blk = (key, label, n, cls) =>
        `<button class="dlc-ov ${cls || ''} ${F.status === key ? 'active' : ''}" data-ov="${key}">
          <span class="ov-k">${label}</span><span class="ov-v">${n}</span></button>`;
      st.innerHTML =
        blk('all', '总数', totalN) +
        blk('owned', '已拥有', ownN, 'ov-ok') +
        blk('unowned', '未拥有', unownN, 'ov-unowned') +
        blk('manual', '手动标记', manualN, 'ov-manual') +
        blk('conflict', '冲突', conflictN, 'ov-conflict');
      st.querySelectorAll('[data-ov]').forEach(b => b.onclick = () => {
        F.status = b.dataset.ov; paintFilter(); paint();
      });
    }

    const vis = items.filter(match);
    if (!vis.length) {
      $('#dlcWrap').innerHTML = '<div class="empty">没有符合条件的 DLC</div>';
      return;
    }
    $('#dlcWrap').innerHTML = `<div class="dlc-grid">${vis.map((x) => {
      const gi = items.indexOf(x);
      // 最终是否拥有以 owned 为准（可能是手动标记覆盖了检测结果）
      const locked = !x.owned;
      const cls = locked ? 'locked' : (x.enabled ? 'on' : 'off');
      // 封面图：Steam CDN（header）直连优先，本地 thumb 兜底，都没有则用占位色块
      const cover = x.header ? x.header : (x.thumb ? thumbUrl(x.thumb) : '');
      // 占位：取 DLC 名称首字母缩写（英文单词首字母，最多 3 个）
      const initials = (() => {
        const n = (x.name || x.nameCn || '').trim();
        const words = n.split(/[\s:：\-–—]+/).filter(w => /[A-Za-z0-9\u4e00-\u9fff]/.test(w));
        // 过滤掉常见无意义前缀词
        const skip = new Set(['the', 'of', 'and', 'for', 'a', 'an', 'pack', 'dlc', 'crusader', 'kings', 'expansion', 'europa', 'universalis', 'hearts', 'iron', 'victoria', 'imperator', 'rome', 'stellaris', 'content', 'unit', 'music', 'portraits', 'portrait', 'songs', 'song']);
        let parts = words.filter(w => !skip.has(w.toLowerCase()));
        if (!parts.length) parts = words;
        return parts.slice(0, 3).map(w => w[0].toUpperCase()).join('');
      })();
      return `
      <div class="dlc-card ${cls}">
        <div class="dlc-pic ${cover ? '' : 'nocover'}" style="${cover ? `background-image:url('${cover}')` : ''}">
          ${cover ? '' : `<span class="dlc-initials">${esc(initials)}</span>`}
          <span class="type-tag type-${x.dlcType}">${esc(dlcTypeLabel(x.dlcType, x.typeCn))}</span>
          ${locked
            ? `<div class="lock-tag">🔒 未拥有</div>`
            : `<div class="chk" data-i="${gi}" title="${x.enabled ? '点击停用' : '点击启用'}">${x.enabled ? '✓' : ''}</div>`}
        </div>
        <div class="dlc-body">
          <div class="dlc-name">${esc(dlcName(x))}${i18nLang() === 'zh' && x.nameCn && x.name && x.nameCn !== x.name ? `<div style="font-size:calc(10px*var(--fs-scale));color:var(--dim-2);font-weight:400;margin-top:2px">${esc(x.name)}</div>` : ''}</div>
          ${metaLine(x) ? `<div class="dlc-meta">${esc(metaLine(x))}</div>` : ''}
          <div class="dlc-foot">
            <small>${esc(x.dir)}</small>
            ${locked
              ? `<span class="lock-note">未拥有</span>`
              : `<div class="toggle ${x.enabled ? 'on' : ''}" data-i="${gi}" title="${x.enabled ? '已启用' : '已停用'}"></div>`}
          </div>
          <div class="dlc-src-note" title="${esc(x.evidence || '')}">${srcText(x)}</div>
          <div class="dlc-ownrow">
            ${statePill(x)}
            <button class="btn sm ghost dlc-own-btn" data-own="${gi}" title="手动判定这个 DLC 是否拥有（可覆盖检测结果）">判定</button>
          </div>
          <button class="btn sm ghost dlc-detail-btn" data-detail="${esc(x.steamId || '')}" ${x.steamId ? '' : 'disabled'}>详情</button>
        </div>
      </div>`;
    }).join('')}</div>`;

    $$('#dlcWrap [data-detail]').forEach(b => b.onclick = () => {
      const sid = b.dataset.detail;
      if (sid) openDlcDetail(sid, items);
    });

    $$('#dlcWrap [data-own]').forEach(b => b.onclick = () => {
      const x = items[+b.dataset.own];
      if (x) openOverridePanel(x);
    });

    $$('#dlcWrap [data-i]').forEach(el => el.onclick = () => {
      const i = +el.dataset.i;
      if (!items[i].owned) {
        toast('这个 DLC 判定为未拥有，可先点「判定」手动标记为拥有');
        return;
      }
      items[i].enabled = !items[i].enabled;
      paint();
      saveDlc(items);
    });
  };

  paintFilter();
  paint();

  $('#dOn').onclick = () => {
    items.forEach(x => { if (x.owned) x.enabled = true; });
    paint(); saveDlc(items);
  };
  $('#dOff').onclick = () => {
    items.forEach(x => { if (x.owned) x.enabled = false; });
    paint(); saveDlc(items);
  };
  $('#dRecheck').onclick = async (e) => {
    const b = e.currentTarget;
    if (b.disabled) return;
    b.disabled = true;
    const old = b.textContent;
    b.textContent = '刷新中…';
    try {
      const r = await post('/api/dlc/recheck', {});
      if (!r.ok) {
        toast(r.error || '重新检测失败', 'err');
        b.disabled = false;
        b.textContent = old;
        return;
      }
      const list = r.items || [];
      const ownN = list.filter(x => x.owned).length;
      const srcText = r.source === 'steam' ? 'Steam 清单'
        : r.source === 'local' ? '本地文件'
        : '未检测到';
      if (r.steamAvailable) {
        toast(`已重新校验：共 ${list.length} 个 DLC，已拥有 ${ownN} 个 · 来源：${srcText}`, 'ok');
      } else {
        toast(`已重新校验：共 ${list.length} 个 DLC，已拥有 ${ownN} 个 · 来源：${srcText}（Steam 库不可读，已自动降级）`, 'ok');
      }
      render();
    } catch (err) {
      toast('重新校验失败：' + (err && err.message ? err.message : '本地服务无响应'), 'err');
      b.disabled = false;
      b.textContent = old;
    }
  };
  $('#dStore').onclick = () => post('/api/url', { url: S.state.browserDlcUrl });

  // 打开本机 dlc 目录（资源管理器）
  $('#dOpenDir').onclick = async () => {
    const r = await post('/api/dlcs/open-dir', {});
    if (!r.ok) toast(r.error || '打开 DLC 目录失败', 'err');
    else if (!r.error) toast('已在资源管理器中打开 DLC 目录', 'ok');
  };

  // 开关自检：逐个对比「设置里的期望开关」与「.dlc 文件真实状态」
  $('#dVerify').onclick = async (e) => {
    const b = e.currentTarget;
    if (b.disabled) return;
    b.disabled = true;
    const old = b.textContent;
    b.textContent = '自检中…';
    try {
      const r = await post('/api/dlcs/verify', {});
      showDlcVerify(r);
    } catch (err) {
      toast('开关自检失败：' + err.message, 'err');
    }
    b.disabled = false;
    b.textContent = old;
  };


  // 打开本地 DLC 中文名对照表（不存在会自动生成带示例的模板）
  $('#dNames').onclick = async () => {
    const r = await post('/api/dlc/names-file', {});
    if (!r.ok) { toast(r.error || '打不开文件', 'err'); return; }
    if (!r.opened) { toast(r.error || '文件已生成，请手动打开', 'err'); return; }
    toast('改完保存，回这里点「重新校验」就生效', 'ok');
  };
}

// 把 Steam 商店的 HTML 描述转成「结构化的安全 HTML」：
// 保留标题(h2/h3)、段落(p)、列表(ul/ol/li)、加粗(b/strong)、换行(br)，
// 剥离其余标签和所有属性，杜绝脚本/样式注入。
function htmlToDesc(html) {
  if (!html) return '';
  // 允许保留的标签集合（白名单）
  const ALLOWED = { h2: 1, h3: 1, p: 1, ul: 1, ol: 1, li: 1, b: 1, strong: 1 };

  // 1) 逐标签扫描：白名单标签去掉属性保留；br 转 \n；其余标签（含 video/img/a/span/div）
  //    只删标签本身、保留内部文本（video 里没有要显示的文字，自然被丢弃）
  let s = html.replace(/<[^>]*>/g, (tag) => {
    const m = tag.match(/^<\s*\/?\s*([a-zA-Z0-9]+)/);
    const name = m ? m[1].toLowerCase() : '';
    if (name === 'br') return '\n';
    if (ALLOWED[name]) {
      const closing = /^\s*<\s*\//.test(tag);
      return closing ? `</${name}>` : `<${name}>`;
    }
    return '';
  });

  // 2) 还原常见 HTML 实体
  s = s
    .replace(/&nbsp;/gi, ' ')
    .replace(/&amp;/gi, '&')
    .replace(/&lt;/gi, '<')
    .replace(/&gt;/gi, '>')
    .replace(/&#39;|&apos;/gi, "'")
    .replace(/&quot;/gi, '"')
    .replace(/&#(\d+);/g, (_, n) => String.fromCharCode(+n));

  // 3) 清理：去掉空的 <p>、空的 <ul>/<ol>，合并多余换行，压缩空白
  s = s
    .replace(/<p>\s*<\/p>/g, '')
    .replace(/<(ul|ol)>\s*<\/(ul|ol)>/g, '')
    .replace(/\n{3,}/g, '\n\n')
    .replace(/>\s+</g, '><')
    .trim();

  return s;
}



async function openDlcDetail(steamId, items) {
  const local = (items || []).find(x => String(x.steamId) === String(steamId)) || {};
  const mask = document.createElement('div');
  mask.className = 'modal-mask';
  mask.innerHTML = `
    <div class="dlc-detail">
      <div class="dlc-detail-head">
        <span class="dd-label">DLC 详情</span>
        <button class="btn sm ghost" data-m="close">✕</button>
      </div>
      <div class="dlc-detail-body">
        <div class="empty" style="padding:40px"><div class="big">◆</div>正在从 Steam 商店加载详情…</div>
      </div>
    </div>`;
  document.body.appendChild(mask);
  requestAnimationFrame(() => mask.classList.add('show'));

  const close = () => {
    mask.classList.remove('show');
    setTimeout(() => mask.remove(), 130);
  };
  mask.querySelector('[data-m="close"]').onclick = close;
  mask.addEventListener('mousedown', e => { if (e.target === mask) close(); });

  let d;
  try { d = await api('/api/dlc/detail?id=' + encodeURIComponent(steamId)); }
  catch (e) { d = { ok: false, error: e.message }; }

  const body = mask.querySelector('.dlc-detail-body');
  const sizeTxt = (() => {
    const b = local.sizeBytes || 0;
    if (!b) return '';
    return b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.round(b / 1024) + ' KB';
  })();

  if (!d.ok) {
    body.innerHTML = `
      <div class="empty" style="padding:40px">
        <div class="big">◆</div>
        <div>${esc(d.error || '加载失败')}</div>
        ${local.nameCn ? `<div style="margin-top:12px"><b>${esc(dlcName(local))}</b>${i18nLang() === 'zh' && local.name && local.name !== local.nameCn ? ` · ${esc(local.name)}` : ''}</div>` : ''}
        ${local.releaseDate || sizeTxt ? `<div style="color:var(--dim-2);margin-top:6px">${esc([local.releaseDate ? TT('发布') + ' ' + fmtDate(local.releaseDate) : '', sizeTxt].filter(Boolean).join(' · '))}</div>` : ''}
        <button class="btn sm primary" style="margin-top:16px" data-m="retry">重试</button>
        <button class="btn sm ghost" style="margin-top:8px" data-m="store">打开 Steam 商店页</button>
      </div>`;
    body.querySelector('[data-m="retry"]').onclick = () => { mask.remove(); openDlcDetail(steamId, items); };
    body.querySelector('[data-m="store"]').onclick = () => post('/api/url', { url: `https://store.steampowered.com/app/${steamId}` });
    return;
  }

  const shots = (d.screenshots || []).slice(0, 8);
  const cleanDesc = htmlToDesc(d.descriptionHtml || '');

  body.innerHTML = `
    <div class="dd-hero">
      ${d.headerImage ? `<img class="dd-hero-img" src="${esc(d.headerImage)}" alt="">` : ''}
    </div>
    <div class="dd-title-row">
      <h3 class="dd-title">${esc(i18nLang() === 'zh' ? (local.nameCn || d.name || local.name || '') : (local.nameEn || d.name || local.name || ''))}</h3>
      <button class="btn sm primary" data-m="store">查看 / 购买</button>
    </div>
    <div class="dd-meta">
      ${d.releaseDate || local.releaseDate ? `<span>${esc(TT('发布'))} ${esc(fmtDate(d.releaseDate || local.releaseDate))}</span>` : ''}
      ${sizeTxt ? `<span>${esc(sizeTxt)}</span>` : ''}
    </div>
    ${d.shortDescription ? `<p class="dd-short">${esc(d.shortDescription)}</p>` : ''}
    ${cleanDesc ? `<div class="dd-desc">${cleanDesc}</div>` : ''}
    ${shots.length ? `
      <div class="dd-shots-title">内容截图</div>
      <div class="dd-shots">${shots.map(u => `<img src="${esc(u)}" alt="">`).join('')}</div>` : ''}
    <div class="dd-source">数据来源：Steam 商店 · 实时获取</div>`;
  body.querySelector('[data-m="store"]').onclick = () => post('/api/url', { url: `https://store.steampowered.com/app/${steamId}` });
}

let dlcTimer = null;
function saveDlc(items) {
  clearTimeout(dlcTimer);
  saveState('保存 DLC 设置…', true);
  dlcTimer = setTimeout(async () => {
    const map = {};
    items.forEach(x => map[x.popsId] = x.enabled);
    try {
      const r = await post('/api/dlcs/set', { dlcs: map });
      saveState(r.ok ? '已保存 DLC 设置' : (r.error || '保存失败'));
      if (!r.ok) {
        toast(r.error || '保存失败', 'err');
      } else if (r.dlcFilesFailed) {
        // 设置已写进播放集，但有 DLC 的 .dlc 文件没能改名 → 明确提示原因并自动自检
        const first = (r.failed && r.failed[0]) || {};
        toast(`设置已保存，但有 ${r.dlcFilesFailed} 个 DLC 的开关没能生效：${first.reason || '未知原因'}（已自动运行开关自检）`, 'err');
        try {
          const v = await post('/api/dlcs/verify', {});
          showDlcVerify(v, true);
        } catch (e) {}
      } else if (r.synced === false) {
        // 设置只写进了播放集，没有落到游戏读取的启停清单（多为播放集未激活）
        toast('设置已保存，但当前播放集未激活，未写入游戏文件', 'err');
      } else {
        toast('已保存，游戏内 DLC 开关已同步', 'ok');
      }
    } catch (e) { toast('保存失败：' + e.message, 'err'); }
    refreshStateQuiet();
  }, 600);
}

/* ---------------- 存档 ---------------- */
async function renderSaves() {
  $('#view').innerHTML = `<div class="page-head"><div><div class="page-title">存档管理</div>
    <div class="page-sub">来自 save games 目录。删除会先移入 Windows 回收站，可撤销。</div></div>
    <div class="bar"><button class="btn sm ghost" id="sOpen">打开存档目录</button>
    <button class="btn sm danger" id="sDel">移入回收站</button></div></div>
    <div class="card" id="saveWrap"><div class="empty">读取中…</div></div>`;

  const d = await api('/api/saves');
  const items = d.items || [];
  if (!items.length) {
    $('#saveWrap').innerHTML = '<div class="empty"><div class="big">⛁</div>没有找到存档</div>';
  } else {
    $('#saveWrap').innerHTML = `<table class="tbl"><thead><tr>
      <th style="width:34px"><input type="checkbox" id="sAll"></th>
      <th>存档</th><th>所在集</th><th>大小</th><th>修改时间</th><th style="width:110px"></th></tr></thead><tbody>
      ${items.map((s, i) => `<tr>
        <td><input type="checkbox" data-i="${i}"></td>
        <td>${esc(s.name)}<div style="font-size:calc(10.5px*var(--fs-scale));color:var(--dim-2)">${esc(s.file)}</div></td>
        <td class="mono">${s.group ? esc(s.group) : '—'}</td>
        <td class="mono">${fmtSize(s.size)}</td>
        <td class="mono">${fmtTime(s.mtime)}</td>
        <td><button class="btn sm ghost" data-locate="${esc(s.path)}">定位</button></td>
      </tr>`).join('')}</tbody></table>`;
  }
  $('#sOpen').onclick = () => post('/api/open', { path: S.state.userDir + '/save games' });
  $$('[data-locate]').forEach(b => b.onclick = () => post('/api/open', { path: b.dataset.locate, reveal: true }));
  const all = $('#sAll');
  if (all) all.onchange = () => $$('[data-i]', $('#saveWrap')).forEach(c => c.checked = all.checked);
  $('#sDel').onclick = async () => {
    const sel = $$('tbody [data-i]:checked').map(c => items[+c.dataset.i].path);
    if (!sel.length) { toast('先勾选要删除的存档'); return; }
    const ok = await ui.confirm(`确定把 ${sel.length} 个存档移入回收站？\n\n删除后可以从 Windows 回收站还原。`, {
      title: '删除存档', okText: '移入回收站', danger: true,
    });
    if (!ok) return;
    const r = await post('/api/saves/delete', { paths: sel });
    toast(`已移入回收站 ${r.done?.length || 0} 个`, r.ok ? 'ok' : 'err');
    renderSaves();
  };
}

/* 在启动器内置 WebView2 面板里打开 Steam 创意工坊页面（不跳外部浏览器）。
   左侧是 Steam 官网原生页面（详情/hover/分类/排序/下载量全是官方数据），
   右侧「已订阅 MOD」数据由这里带过去。 */
function subscribedPayload() {
  return (S.mods || []).filter(m => m.source === 'steam' && m.steamId).map(m => ({
    steamId: m.steamId,
    name: m.name || m.modFile,
    version: m.version || '',
    supportedVersion: m.supportedVersion || '',
    thumb: m.thumb || '',
  }));
}
let _wsSyncing = false;
async function syncWorkshopSubscribed() {
  if (_wsSyncing) return;
  _wsSyncing = true;
  try { await post('/api/browser/subscribed', { mods: subscribedPayload() }); }
  catch (e) { }
  finally { _wsSyncing = false; }
}
function workshopHomeUrl() {
  const raw = (S.state && S.state.browserModUrl) || 'https://steamcommunity.com/app/1158310/workshop/';
  return raw + (raw.includes('?') ? '&' : '?') + 'l=schinese';
}
async function openWorkshopTab(url) {
  if (!url) return;
  try {
    const r = await post('/api/browser/open', { url, mods: subscribedPayload() });
    if (!r.ok) toast(r.error || '打开失败', 'err');
  } catch (e) { toast('打开失败：' + (e.message || e), 'err'); }
}
// 供 WPF 工坊面板的工具条回调（上传 / 检查更新后刷新右侧列表）
window.__openWorkshopUpload = () => openWorkshopUploadDialog();
window.__wsRescan = async () => {
  try {
    const d = await api('/api/mods?force=1');
    S.mods = d.items || [];
    S.modByKey = new Map(S.mods.map(m => [m.modFile.toLowerCase(), m]));
    await syncWorkshopSubscribed();
    toast('已重新扫描本机订阅', 'ok');
  } catch (e) { toast('扫描失败：' + (e.message || e), 'err'); }
};

/* 上传本地 mod 到 Steam 创意工坊：首选 Steam 直连（官方游戏内同款，免密码），SteamCMD 为备用 */
async function openWorkshopUploadDialog() {
  const mask = document.createElement('div');
  mask.className = 'modal-mask';
  document.body.appendChild(mask);
  requestAnimationFrame(() => mask.classList.add('show'));

  let poll = null;
  const close = () => {
    if (poll) clearInterval(poll);
    mask.classList.remove('show');
    setTimeout(() => mask.remove(), 130);
  };
  mask.addEventListener('mousedown', e => { if (e.target === mask) close(); });

  let info = {};
  try { info = await api('/api/workshop/upload-steam-info') || {}; } catch (e) { }
  const mods = S.mods.filter(m => m.dirPath && m.source !== 'steam');
  const def = mods[0] || {};

  const steamOk = info.steamRunning && info.dllReady;
  const steamHint = !info.steamRunning
    ? `<span style="color:var(--orange)">⚠ 未检测到 Steam 客户端在运行——直连上传需要先启动并登录 Steam</span>`
    : !info.dllReady
      ? `<span style="color:var(--orange)">⚠ 未找到 steam_api64.dll（请确认游戏路径设置正确）</span>`
      : `<span style="color:var(--green)">Steam客户端已连接，可上传mod</span>`;

  const modal = document.createElement('div');
  modal.className = 'modal steam-modal';
  modal.innerHTML = `
    <div class="modal-head">上传本地 Mod 到 Steam 创意工坊</div>
    <div class="modal-body">
      <div class="hint" style="line-height:1.7;margin-bottom:10px">${steamHint}</div>
      <div class="form-row"><div class="lab">本地 Mod</div><div class="val">
        <select id="wuMod" class="inp">${mods.map(m =>
          `<option value="${esc(m.modFile)}">${esc(m.name || m.modFile)}</option>`).join('') || '<option>没有本地 Mod</option>'}</select>
      </div></div>
      <div class="form-row"><div class="lab">标题</div><div class="val">
        <input type="text" id="wuTitle" value="${esc(def.name || '')}"></div></div>
      <div class="form-row"><div class="lab">描述</div><div class="val">
        <textarea id="wuDesc" class="inp" rows="3" style="height:auto;padding:8px;resize:vertical">${esc(def.name || '')}</textarea>
      </div></div>
      <div class="form-row"><div class="lab">预览图</div><div class="val">
        <input type="text" id="wuPreview" value="${esc(def.thumb || '')}" placeholder="留空则用 Mod 目录里的 thumbnail.png">
        <button class="btn sm ghost" id="wuPickPreview" title="自动读取不到时手动选择一张图片">选择图片…</button>
      </div></div>
      <div class="form-row"><div class="lab">可见性</div><div class="val">
        <select id="wuVis" class="inp">
          <option value="2">私有（仅自己，推荐先私有测试）</option><option value="1">好友可见</option><option value="0">公开</option>
        </select></div></div>
      <div class="form-row"><div class="lab">已有物品 ID</div><div class="val">
        <input type="text" id="wuPid" value="0" style="max-width:120px" placeholder="首次填 0；更新时填工坊数字 ID">
        <span class="hint" style="flex:1">更新老 Mod 时填创意工坊页面地址里的数字 ID</span></div></div>
      <div class="form-row"><div class="lab">变更说明</div><div class="val">
        <input type="text" id="wuNote" placeholder="选填，会显示在工坊更新记录里"></div></div>

      <div id="wuProgress" style="display:none;margin:12px 0 4px">
        <div class="ws-up-bar"><div class="ws-up-fill" id="wuFill"></div></div>
        <div class="hint" id="wuPhase" style="margin:6px 0">准备中…</div>
      </div>
      <pre id="wuLog" style="max-height:150px;overflow:auto;background:var(--panel-2);border:1px solid var(--line);border-radius:6px;padding:8px;font-size:calc(11px*var(--fs-scale));line-height:1.5;white-space:pre-wrap;display:none"></pre>

      <details style="margin-top:10px">
        <summary class="hint" style="cursor:pointer">备用方案：SteamCMD 上传（需要 Steam 账号密码，直连不可用时用）</summary>
        <div class="form-row" style="margin-top:10px"><div class="lab">steamcmd.exe</div><div class="val">
          <input type="text" id="wuCmd" value="${esc(info.steamcmd || '')}" placeholder="${info.found ? '' : '未找到，请手动指定 steamcmd.exe 路径'}"></div></div>
        <div class="form-row"><div class="lab">Steam 账号</div><div class="val">
          <input type="text" id="wuUser" autocomplete="off"></div></div>
        <div class="form-row"><div class="lab">Steam 密码</div><div class="val">
          <input type="password" id="wuPass" autocomplete="new-password"></div></div>
        <div class="hint" style="margin-top:6px">密码仅本次进程使用，不写入任何文件。</div>
        <div class="bar" style="margin-top:8px">
          <button class="btn sm ghost" data-w="prep">生成 SteamCMD 配置</button>
          <button class="btn sm ghost" data-w="run2">用 SteamCMD 上传</button>
        </div>
        <pre id="wuLog2" style="max-height:130px;overflow:auto;background:var(--panel-2);border:1px solid var(--line);border-radius:6px;padding:8px;font-size:calc(11px*var(--fs-scale));line-height:1.5;white-space:pre-wrap;margin-top:8px"></pre>
      </details>
    </div>
    <div class="modal-foot">
      <button class="btn ghost" data-w="open">打开工坊页</button>
      <button class="btn ghost" data-w="close">关闭</button>
      <button class="btn primary" data-w="runSteam" ${steamOk ? '' : 'disabled'}>上传</button>
    </div>`;
  mask.appendChild(modal);

  const $w = id => modal.querySelector('#' + id);
  const log = $w('wuLog'), log2 = $w('wuLog2');
  const say = s => { log.style.display = 'block'; log.textContent = s; log.scrollTop = log.scrollHeight; };
  const say2 = s => { log2.style.display = 'block'; log2.textContent = s; log2.scrollTop = log2.scrollHeight; };
  const pick = () => mods.find(m => m.modFile === $w('wuMod').value) || {};
  $w('wuMod').onchange = () => {
    const m = pick();
    $w('wuTitle').value = m.name || '';
    $w('wuPreview').value = m.thumb || '';
  };
  // 自动读不到 thumbnail 时，手动挑一张图片
  $w('wuPickPreview').onclick = async () => {
    try {
      const r = await post('/api/workshop/pick-preview', {});
      if (!r.ok) { if (!r.cancelled) toast(r.error || '选择图片失败', 'err'); return; }
      $w('wuPreview').value = r.path;
    } catch (e) { toast(String(e.message || e), 'err'); }
  };
  modal.querySelector('[data-w="close"]').onclick = close;
  modal.querySelector('[data-w="open"]').onclick = () =>
    openWorkshopTab(`https://steamcommunity.com/app/${info.appid || '1158310'}/workshop/`);

  const form = () => ({
    modKey: $w('wuMod').value, title: $w('wuTitle').value.trim(),
    description: $w('wuDesc').value.trim(), preview: $w('wuPreview').value.trim(),
    visibility: $w('wuVis').value, publishedId: $w('wuPid').value.trim() || '0',
    changenote: $w('wuNote').value.trim(),
  });

  // —— 直连上传（Steamworks，免密码）——
  const PHASES = { preparing: '准备上传内容…', creating: '创建工坊物品…', editing: '填写物品信息…', uploading: '上传中…', processing: 'Steam 处理中…', committing: '提交变更…' };
  modal.querySelector('[data-w="runSteam"]').onclick = async () => {
    const btn = modal.querySelector('[data-w="runSteam"]');
    btn.disabled = true;
    try {
      const r = await post('/api/workshop/upload-steam-start', form());
      if (!r.ok) { toast(r.error || '启动失败', 'err'); say(r.error || '启动失败'); btn.disabled = false; return; }
      $w('wuProgress').style.display = 'block';
      toast('直连上传已启动，正在借 Steam 客户端免密码上传', 'ok');
      if (poll) clearInterval(poll);
      poll = setInterval(async () => {
        try {
          const s = await api('/api/workshop/upload-steam-status');
          say((s.lines || []).join('\n'));
          const pct = Math.max(0, Math.min(100, s.pct || 0));
          $w('wuFill').style.width = pct + '%';
          $w('wuPhase').textContent = (PHASES[s.phase] || '进行中…') +
            (s.phase === 'uploading' && s.total > 0 ? ` ${pct}%（${fmtSize(s.done)} / ${fmtSize(s.total)}）` : '');
          if (s.state !== 'running') {
            clearInterval(poll); poll = null; btn.disabled = false;
            if (s.state === 'done' && s.itemId) {
              $w('wuFill').style.width = '100%';
              $w('wuPhase').innerHTML = `<b style="color:var(--green)">✔ 上传完成！物品 ID：${s.itemId}（新物品默认私密，可到工坊页改公开）</b>`;
              toast('上传完成！', 'ok');
            } else if (s.state === 'error') {
              $w('wuPhase').innerHTML = `<b style="color:var(--red)">✖ ${esc(s.error || '上传失败')}</b>`;
            }
          }
        } catch (e) { clearInterval(poll); poll = null; btn.disabled = false; }
      }, 800);
    } catch (e) { toast(String(e.message || e), 'err'); btn.disabled = false; }
  };

  // —— 备用：SteamCMD ——
  const prepareCmd = async () => {
    const f = form();
    const r = await post('/api/workshop/upload-prepare', {
      modKey: f.modKey, title: f.title, description: f.description,
      preview: f.preview, visibility: f.visibility, publishedId: f.publishedId,
    });
    if (!r.ok) { toast(r.error || '生成失败', 'err'); say2(r.error || '生成失败'); return null; }
    say2('配置已生成：' + r.vdf);
    return r;
  };
  modal.querySelector('[data-w="prep"]').onclick = async () => {
    try { await prepareCmd(); } catch (e) { toast(String(e.message || e), 'err'); }
  };
  modal.querySelector('[data-w="run2"]').onclick = async () => {
    const btn = modal.querySelector('[data-w="run2"]');
    btn.disabled = true;
    try {
      if (!(await prepareCmd())) { btn.disabled = false; return; }
      const r = await post('/api/workshop/upload-run', {
        steamcmd: $w('wuCmd').value.trim(), user: $w('wuUser').value.trim(), pass: $w('wuPass').value,
      });
      if (!r.ok) { toast(r.error || '启动失败', 'err'); say2(r.error || '启动失败'); btn.disabled = false; return; }
      toast('SteamCMD 已启动，日志在下方', 'ok');
      if (poll) clearInterval(poll);
      poll = setInterval(async () => {
        try {
          const s = await api('/api/workshop/upload-status');
          say2((s.lines || []).join('\n'));
          if (s.state !== 'running') {
            clearInterval(poll); poll = null; btn.disabled = false;
            toast(s.state === 'done' ? '上传进程已结束，请到工坊页确认' : '上传结束', 'ok');
          }
        } catch (e) { clearInterval(poll); poll = null; btn.disabled = false; }
      }, 1500);
    } catch (e) { toast(String(e.message || e), 'err'); btn.disabled = false; }
  };
}

/* ---------------- 创意工坊 ---------------- */
/* ================= 创意工坊（懒狗式：启动器页面内直接铺 MOD 网格） ================= */
const WS_SORTS = [
  ['trend', '本周热门'], ['mostrecent', '最近更新'], ['mostsubscribed', '最多订阅'],
  ['totaluniquesubscribers', '历史订阅'], ['toprated', '最高好评'],
];
const WS_CATS = [
  ['', '全部分类'], ['Graphics', '图形'], ['Historical', '历史'], ['Gameplay', '游戏玩法'],
  ['Interface', '界面'], ['Utilities', '实用工具'], ['Translation', '翻译'],
  ['Total Conversion', '全面转换'], ['Alternative History', '架空历史'],
  ['Map', '地图'], ['Military', '军事'], ['Religion', '宗教'],
  ['Character Interactions', '角色互动'], ['Events', '事件'], ['Decisions', '决议'],
  ['Balance', '平衡性'], ['Culture', '文化'], ['Sound', '音效'],
];
const WS = { sort: 'trend', category: '', search: '', page: 1, view: 'list', cur: null };

function wsFmtSize(n) {
  n = Number(n) || 0;
  if (n < 1024) return n + ' B';
  if (n < 1048576) return (n / 1024).toFixed(0) + ' KB';
  if (n < 1073741824) return (n / 1048576).toFixed(1) + ' MB';
  return (n / 1073741824).toFixed(2) + ' GB';
}
function wsFmtDate(sec) {
  if (!sec) return '—';
  const d = new Date(Number(sec) * 1000);
  return isNaN(d.getTime()) ? '—' :
    `${d.getFullYear()}/${String(d.getMonth() + 1).padStart(2, '0')}/${String(d.getDate()).padStart(2, '0')}`;
}
function wsFmtNum(n) { return (Number(n) || 0).toLocaleString('zh-CN'); }
function wsDesc(d) {
  let t = String(d || '');
  // 去掉 Steam BBCode 图片/链接，取纯文本前 160 字
  t = t.replace(/\[img\][\s\S]*?\[\/img\]/gi, ' ')
       .replace(/\[url[^\]]*\][\s\S]*?\[\/url\]/gi, ' ')
       .replace(/\[[^\]]+\]/g, ' ');
  t = t.replace(/https?:\/\/\S+/g, ' ').replace(/\s+/g, ' ').trim();
  return t.length > 160 ? t.slice(0, 160) + '…' : t;
}
function wsThumb(url) {
  if (!url) return '';
  // Steam 图床本地化：images.steamusercontent.com 已是直链
  return url;
}

function renderWorkshop() {
  WS.view = 'list';
  paintWorkshopShell();
  wsLoadBrowse();
}

function paintWorkshopShell() {
  const steamMods = (S.mods || []).filter(m => m.source === 'steam');
  const curDef = (S.state.gameDefs || {})[S.state.currentGame || 'ck3'] || { name: '十字军之王 III' };
  $('#view').innerHTML = `
    <div class="page-head">
      <div><div class="page-title">创意工坊</div>
      <div class="page-sub">正在浏览 ${esc(curDef.name || '当前游戏')} 的 Steam 创意工坊 · 点卡片看详情，右侧是本机已订阅</div></div>
      <div class="bar">
        <button class="btn sm ghost" id="wRescan">↻ 重新扫描</button>
        <button class="btn sm ghost" id="wUpload">上传本地 Mod</button>
        <button class="btn sm" id="wSteamOpen">在 Steam 打开</button>
      </div>
    </div>
    <div class="card ws-hero" style="padding:12px 14px;margin-bottom:10px">
      <div class="ws-toolbar">
        <input type="text" class="inp" id="wsQ" placeholder="搜索 MOD 名称 / ID…" value="${esc(WS.search)}" style="flex:1;min-width:180px">
        <button class="btn sm primary" id="wsGo">搜索</button>
        <select class="inp" id="wsSort" style="width:auto">
          ${WS_SORTS.map(([v, t]) => `<option value="${v}" ${v === WS.sort ? 'selected' : ''}>${t}</option>`).join('')}
        </select>
        <select class="inp" id="wsCat" style="width:auto">
          ${WS_CATS.map(([v, t]) => `<option value="${esc(v)}" ${v === WS.category ? 'selected' : ''}>${t}</option>`).join('')}
        </select>
      </div>
    </div>
    <div class="ws-layout">
      <div class="ws-main">
        <div class="ws-bar" id="wsBar"><span class="hint" id="wsInfo">正在加载创意工坊…</span></div>
        <div class="ws-grid" id="wsGrid"><div class="empty" style="grid-column:1/-1">加载中…</div></div>
        <div class="ws-pager" id="wsPager"></div>
      </div>
      <aside class="ws-side">
        <div class="ws-side-h">已订阅 MOD <span class="cnt" id="wsCnt2">${steamMods.length}</span></div>
        <div class="ws-side-search"><input type="text" class="inp" id="wsLocal" placeholder="搜索名称或 ID"></div>
        <div class="ws-side-list" id="wsSideList"></div>
      </aside>
    </div>`;

  const go = () => {
    WS.search = ($('#wsQ')?.value || '').trim();
    WS.sort = $('#wsSort')?.value || 'trend';
    WS.category = $('#wsCat')?.value || '';
    WS.page = 1;
    wsLoadBrowse();
  };
  $('#wsGo').onclick = go;
  $('#wsQ').onkeydown = e => { if (e.key === 'Enter') go(); };
  $('#wsSort').onchange = go;
  $('#wsCat').onchange = go;
  $('#wUpload').onclick = openWorkshopUploadDialog;
  $('#wSteamOpen').onclick = () => {
    const aid = ((S.state.gameDefs || {})[S.state.currentGame || 'ck3'] || {}).appid || '1158310';
    post('/api/url', { url: `https://steamcommunity.com/app/${aid}/workshop/` });
  };
  $('#wRescan').onclick = async () => {
    try {
      const d = await api('/api/mods?force=1');
      S.mods = d.items || [];
      S.modByKey = new Map(S.mods.map(m => [m.modFile.toLowerCase(), m]));
      paintWorkshopShell();
      wsLoadBrowse();
      toast('已重新扫描本机订阅', 'ok');
    } catch (e) { toast('扫描失败：' + (e.message || e), 'err'); }
  };
  $('#wsLocal').oninput = wsPaintSubscribed;
  wsPaintSubscribed();
}

function wsPaintSubscribed() {
  const box = $('#wsSideList');
  if (!box) return;
  const q = ($('#wsLocal')?.value || '').trim().toLowerCase();
  const list = (S.mods || []).filter(m => m.source === 'steam' && !q ||
    (m.name || '').toLowerCase().includes(q) || String(m.steamId || '').includes(q));
  box.innerHTML = list.length ? list.map(m => `
    <div class="ws-item" data-detail="${esc(m.steamId || '')}">
      <div class="ws-item-ic">${m.thumb ? `<img src="${esc(thumbUrl(m.thumb))}" loading="lazy">` : '📦'}</div>
      <div class="ws-item-tx">
        <div class="ws-item-nm">${esc(m.name || m.modFile)}</div>
        <div class="ws-item-sub">${esc(m.supportedVersion || '')} · ${esc(m.version || '')}</div>
      </div>
    </div>`).join('') : `<div class="empty" style="padding:18px;font-size:calc(12px*var(--fs-scale))">没有匹配项</div>`;
  $$('#wsSideList [data-detail]').forEach(el => el.onclick = () => {
    const id = el.getAttribute('data-detail');
    if (!id) { toast('该订阅项没有工坊 ID，无法打开详情（可点「重新扫描」修复）', 'err'); return; }
    const local = (S.mods || []).find(x => String(x.steamId) === id);
    wsShowDetail({
      id, title: local ? (local.name || local.modFile) : id,
      desc: '', thumb: local?.thumb ? thumbUrl(local.thumb) : '',
      size: 0, updated: 0, created: 0, subs: 0, fav: 0, views: 0, comments: 0, rating: 0,
      tags: local ? [local.supportedVersion].filter(Boolean) : [],
      local: local || null,
    });
  });
}

/* 骨架屏：首次进入工坊时的灰色占位卡，让等待"看得见进度"而不是白屏 */
function wsSkeleton(n) {
  let h = '';
  for (let i = 0; i < (n || 12); i++) h += `
    <div class="ws-card ws-skel" aria-hidden="true">
      <div class="ws-card-pic skel"></div>
      <div class="ws-card-body">
        <div class="skel skel-line" style="width:80%"></div>
        <div class="skel skel-line" style="width:55%"></div>
        <div class="skel skel-line" style="width:92%"></div>
      </div>
    </div>`;
  return h;
}

async function wsLoadBrowse(opts) {
  const info = $('#wsInfo'), grid = $('#wsGrid'), pager = $('#wsPager');
  // 翻页/刷新时不清屏：保留旧卡片，只在顶部提示；首次进入显示骨架屏
  const hasOld = grid && grid.querySelector('.ws-card');
  if (info) info.innerHTML = hasOld ? '正在刷新…' : '正在加载创意工坊…';
  if (grid && !hasOld) grid.innerHTML = wsSkeleton();
  const paint = (items, stale) => {
    if (info) info.innerHTML = `共 <b>${wsFmtNum(WS._total)}</b> 个 MOD · 第 ${WS._pg}/${WS._pgs} 页` +
      (WS.search ? ` · 搜索「${esc(WS.search)}」` : '') +
      (stale ? ' · <span style="color:var(--green)">已显示缓存，正在更新最新数据…</span>' : '');
    if (grid) grid.innerHTML = items.length ? items.map(m => `
      <div class="ws-card" data-detail="${esc(m.id)}" title="${esc(wsDesc(m.desc))}">
        <div class="ws-card-pic">
          ${m.thumb ? `<img src="${esc(wsThumb(m.thumb))}" loading="lazy" decoding="async">` : `<span>${esc((m.title || '?')[0])}</span>`}
          <div class="ws-card-subs">↓ ${wsFmtNum(m.subs)}</div>
        </div>
        <div class="ws-card-body">
          <div class="ws-card-name">${esc(m.title || m.id)}</div>
          <div class="ws-card-meta"><span>${wsFmtDate(m.updated)}</span><span>${wsFmtSize(m.size)}${m.rating >= 1 && m.ratingsCount > 0 ? `<b class="ws-card-rate ${m.rating >= 4 ? 'good' : 'bad'}">${({ 1: 30, 2: 50, 3: 70, 4: 88, 5: 99 })[Math.min(5, Math.max(1, Math.round(m.rating)))]}%</b>` : ''}</span></div>
          <div class="ws-card-desc">${esc(wsDesc(m.desc))}</div>
        </div>
      </div>`).join('') : `<div class="empty" style="grid-column:1/-1;padding:28px"><div class="big">✧</div>没有找到 MOD</div>`;
    $$('#wsGrid [data-detail]').forEach(el => el.onclick = () => {
      const id = el.getAttribute('data-detail');
      const d0 = (WS._browse || []).find(x => String(x.id) === id);
      if (d0) wsShowDetail(d0);
    });
    if (pager) {
      const tp = Math.min(WS._pgs, 999);
      pager.innerHTML = `<button class="btn sm ghost" id="wsPrev" ${WS.page <= 1 ? 'disabled' : ''}>← 上一页</button>` +
        `<span class="hint">${WS.page} / ${tp}</span>` +
        `<button class="btn sm ghost" id="wsNext" ${WS.page >= tp ? 'disabled' : ''}>下一页 →</button>`;
      const pv = $('#wsPrev'), nx = $('#wsNext');
      if (pv) pv.onclick = () => { if (WS.page > 1) { WS.page--; wsLoadBrowse(); } };
      if (nx) nx.onclick = () => { if (WS.page < tp) { WS.page++; wsLoadBrowse(); } };
    }
  };
  try {
    const aid = ((S.state.gameDefs || {})[S.state.currentGame || 'ck3'] || {}).appid || '1158310';
    const q = new URLSearchParams({
      sort: WS.sort, category: WS.category, search: WS.search, page: String(WS.page),
      appid: aid,
    });
    const d = await api('/api/workshop/browse?' + q.toString());
    if (!d.ok) throw new Error(d.error || '加载失败');
    const items = d.items || [];
    WS._browse = items; WS._total = d.totalCount; WS._pg = d.page; WS._pgs = d.totalPages || 1;
    if (d.stale) {
      // stale-while-revalidate：先渲染缓存数据（秒开），2 秒后自动补一次真数据
      paint(items, true);
      setTimeout(() => {
        if (S.route === 'workshop' && WS.view === 'list') wsLoadBrowse({ noStaleRetry: true });
      }, 2000);
      return;
    }
    paint(items, false);
  } catch (e) {
    if (info) info.innerHTML = `<span style="color:var(--orange)">加载失败：${esc(e.message || e)}</span>`;
    if (grid && !hasOld) grid.innerHTML = `<div class="empty" style="grid-column:1/-1;padding:28px">无法连接 Steam 创意工坊，请检查网络</div>`;
    else if (grid) toast('工坊数据刷新失败，仍显示上次缓存', 'err');
  }
}

function wsShowDetail(d, noFetch) {
  WS.view = 'detail'; WS.cur = d;
  const isSubscribed = !!(d.local || (S.mods || []).find(m => String(m.steamId) === String(d.id)));
  const descText = d.full || d.desc || '';
  const descHtml = descText ? esc(descText).replace(/\r?\n/g, '<br>') : '';
  const tags = (d.tags || []).map(t => `<span class="tag">${esc(t)}</span>`).join('');
  // 评价数据（懒狗同款）：Steam 工坊只提供星级 + 总票数，好评率按 Steam 官方星级档位换算——
  // 5星=97~100% → 99%，4星=80~96.9% → 88%，3星=60~79.9% → 70%，2星=40~59.9% → 50%，1星=<40% → 30%
  const star = Math.round(Number(d.rating) || 0);
  const votes = Number(d.ratingsCount) || 0;
  const RATE = { 1: 30, 2: 50, 3: 70, 4: 88, 5: 99 };
  const pct = (star >= 1 && star <= 5 && votes > 0) ? RATE[star] : 0;
  const up = pct ? Math.round(votes * pct / 100) : 0;
  const down = pct ? votes - up : 0;
  // 评价档位文案（Steam 同款）+ 红绿大字横幅（懒狗同款，显眼）
  const good = pct >= 70;
  const verdict = pct >= 95 ? '好评如潮' : pct >= 80 ? '特别好评' : pct >= 70 ? '多半好评'
    : pct >= 40 ? '褒贬不一' : '差评如潮';
  const banner = pct ? `
        <div class="ws-rate-banner ${good ? 'good' : 'bad'}">
          <div class="ws-rate-pct">${pct}<small>%</small></div>
          <div class="ws-rate-mid">
            <div class="ws-rate-verdict">${verdict}</div>
            <div class="ws-rate-counts"><span class="up">👍 好评 ${wsFmtNum(up)}</span><span class="down">👎 差评 ${wsFmtNum(down)}</span></div>
          </div>
          <div class="ws-rate-stars">${'★'.repeat(star)}${'☆'.repeat(5 - star)}<small>${wsFmtNum(votes)} 个评价</small></div>
        </div>` : '';
  $('#view').innerHTML = `
    <div class="page-head">
      <div><div class="page-title">MOD 详情</div><div class="page-sub">${esc(d.title || d.id || '…')}</div></div>
      <div class="bar"><button class="btn sm ghost" id="wsBack">← 返回列表</button></div>
    </div>
    <div class="ws-detail">
      <div class="ws-detail-main card">
        ${banner}
        <div class="ws-detail-pic">${d.thumb ? `<img src="${esc(wsThumb(d.thumb))}">` : '<span>无预览图</span>'}</div>
        <div class="ws-detail-body">
          <h2>${esc(d.title || d.id || '…')}</h2>
          <div class="ws-detail-stats">
            <span>↓ 订阅 <b>${wsFmtNum(d.subs)}</b></span>
            <span>♥ 收藏 <b>${wsFmtNum(d.fav)}</b></span>
            <span>👁 浏览 <b>${wsFmtNum(d.views)}</b></span>
            <span>💬 评论 <b>${wsFmtNum(d.comments)}</b></span>
            ${pct ? `<span style="color:${good ? 'var(--green)' : 'var(--red)'};font-weight:700">${verdict}（${pct}%）</span>` : ''}
            ${votes ? `<span>📝 评价 <b>${wsFmtNum(votes)}</b></span>` : '<span>📝 评价 <b>暂无</b></span>'}
            ${star >= 1 ? `<span>★ 评分 <b>${star}/5</b></span>` : ''}
          </div>
          <div class="ws-detail-tags">${tags}</div>
          <div class="ws-detail-desc">${descHtml || `<span class="hint">${d.id ? '正在加载完整介绍…' : '该 MOD 暂无简介。'}</span>`}</div>
        </div>
      </div>
      <aside class="card ws-detail-side">
        <div class="card-title">信息</div>
        <div class="kv"><span>大小</span><b>${wsFmtSize(d.size)}</b></div>
        <div class="kv"><span>更新于</span><b>${wsFmtDate(d.updated)}</b></div>
        <div class="kv"><span>发布于</span><b>${wsFmtDate(d.created)}</b></div>
        ${pct ? `<div class="kv"><span>好评 / 差评</span><b>👍 ${wsFmtNum(up)} · 👎 ${wsFmtNum(down)}</b></div>
        <div class="kv"><span>好评率</span><b style="color:${good ? 'var(--green)' : 'var(--red)'}">${pct}%</b></div>` : ''}
        ${star >= 1 ? `<div class="kv"><span>社区评分</span><b>${'★'.repeat(star)}${'☆'.repeat(5 - star)} ${star}/5${votes ? ' · ' + wsFmtNum(votes) + ' 个评价' : ''}</b></div>`
          : `<div class="kv"><span>社区评分</span><b>☆☆☆☆☆ ${votes ? wsFmtNum(votes) + ' 个评价（评价不足）' : '暂无评价'}</b></div>`}
        <div class="kv"><span>工坊 ID</span><b>${esc(d.id || '—')}</b></div>
        <div class="kv"><span>本地状态</span><b>${isSubscribed ? '已订阅' : '未订阅'}</b></div>
        <div class="bar" style="margin-top:14px;flex-wrap:wrap">
          ${d.id ? `<button class="btn primary" id="wsSub">📥 订阅 / 下载</button>
          <button class="btn ghost" id="wsInSteam">在 Steam 打开</button>` : ''}
          ${d.local ? `<button class="btn ghost" id="wsAddSet">加入播放集</button>` : ''}
        </div>
        <div class="hint" style="margin-top:10px;font-size:calc(11px*var(--fs-scale))">订阅后回到本启动器点「重新扫描」即可注册进播放集。</div>
      </aside>
    </div>`;
  $('#wsBack').onclick = () => { WS.view = 'list'; paintWorkshopShell(); wsLoadBrowse(); };
  if (d.id) {
    $('#wsSub').onclick = () => wsSubscribe(d);
    $('#wsInSteam').onclick = async () => {
      const r = await post('/api/steam/open-filepage', { id: d.id });
      if (!r || !r.ok) post('/api/url', { url: `https://steamcommunity.com/sharedfiles/filedetails/?id=${d.id}&l=schinese` });
    };
    if ($('#wsAddSet')) $('#wsAddSet').onclick = () => {
      // 交给模组管理页处理，这里仅提示
      toast('请到「模组管理」启用该 MOD', 'ok');
    };
  }
  // 自动补全：本地订阅列表点进来的数据不全（无封面/大小/介绍），从详情接口拉完整数据
  if (!noFetch && d.id) {
    api('/api/workshop/detail?id=' + encodeURIComponent(d.id)).then(r => {
      if (!r || !r.ok || !r.item) {
        if (WS.view === 'detail' && WS.cur && String(WS.cur.id) === String(d.id) && !(d.full || d.desc))
          WS.cur.local = d.local;
        return;
      }
      // 只在还停在这个 MOD 的详情页时才刷新；标题/简介/完整介绍按中文字符数择优（浏览页数据是中文版）
      if (WS.view !== 'detail' || !WS.cur || String(WS.cur.id) !== String(r.item.id)) return;
      const cjk = s => (String(s || '').match(/[\u4e00-\u9fff]/g) || []).length;
      const pick = (a, b) => (a && cjk(a) >= cjk(b || '') ? a : (b || a || ''));
      // 完整介绍若拉到的是英文（Steam 限流时拿不到中文页），而浏览页的中文短简介更长，则用中文简介代替
      const full = (r.item.full && cjk(r.item.full) >= cjk(d.desc || '')) ? r.item.full : (d.desc || r.item.full || '');
      const merged = {
        ...r.item,
        title: pick(d.title, r.item.title) || r.item.title,
        desc: pick(d.desc, r.item.desc) || r.item.desc,
        full,
        local: d.local || (S.mods || []).find(m => String(m.steamId) === String(r.item.id)) || null,
      };
      wsShowDetail(merged, true);
      // stale：刚才拿到的是磁盘缓存旧数据（秒开用），1.5 秒后自动补拉一次真数据；
      // 第二次若仍 stale（后台刷新未完成）只渲染不再循环
      if (r.item.stale && !d._staleRetry) {
        merged._staleRetry = true;
        setTimeout(() => {
          if (WS.view === 'detail' && WS.cur && String(WS.cur.id) === String(merged.id))
            wsShowDetail(merged, false);
        }, 1500);
      }
    }).catch(() => { });
  }
}

/* —— 订阅 / 下载：三层策略 ——
   ① SteamCMD 匿名直连下载（启动器托管，无需用户操作）
   ② Steam 客户端在运行 → 客户端内打开 MOD 页订阅，启动器监视下载目录
   ③ 都不行 → 弹窗询问是否跳转 Steam 网页手动订阅 */
async function wsSubscribe(d) {
  if (!d || !d.id) { toast('没有工坊 ID，无法下载', 'err'); return; }
  toast('正在准备下载…');
  let r;
  try { r = await post('/api/workshop/download/start', { id: d.id }); }
  catch (e) { r = { ok: false, error: String(e.message || e), mode: 'web' }; }
  if (r && r.ok && r.mode === 'steamcmd') { wsOpenDownloadLog(); return; }
  if (r && r.mode === 'client') {
    toast('已打开 Steam 客户端：请在该 MOD 页面点「订阅」，下载完成后启动器会自动识别', 'ok', 6000);
    wsWatchDir(d.id);
    return;
  }
  wsFailAskWeb(d, r && r.error);
}

function wsFailAskWeb(d, why) {
  const mask = document.createElement('div');
  mask.className = 'modal-mask';
  document.body.appendChild(mask);
  requestAnimationFrame(() => mask.classList.add('show'));
  const close = () => { mask.classList.remove('show'); setTimeout(() => mask.remove(), 130); };
  mask.addEventListener('mousedown', e => { if (e.target === mask) close(); });
  const modal = document.createElement('div');
  modal.className = 'modal';
  modal.innerHTML = `
    <div class="modal-head">启动器下载失败</div>
    <div class="modal-body">
      <div class="hint" style="line-height:1.7">${esc(why || '无法通过 SteamCMD 直接下载（网络受限或 SteamCMD 不可用）。')}</div>
      <div style="margin-top:10px">是否跳转 Steam 网页手动订阅下载？</div>
    </div>
    <div class="modal-foot">
      <button class="btn ghost" data-w="no">取消</button>
      <button class="btn primary" data-w="yes">跳转 Steam 网页</button>
    </div>`;
  mask.appendChild(modal);
  modal.querySelector('[data-w="no"]').onclick = close;
  modal.querySelector('[data-w="yes"]').onclick = () => {
    close();
    post('/api/url', { url: `https://steamcommunity.com/sharedfiles/filedetails/?id=${d.id}&l=schinese` });
  };
}

function wsOpenDownloadLog() {
  const mask = document.createElement('div');
  mask.className = 'modal-mask';
  document.body.appendChild(mask);
  requestAnimationFrame(() => mask.classList.add('show'));
  let poll = null, failed = false;
  const close = () => { if (poll) clearInterval(poll); mask.classList.remove('show'); setTimeout(() => mask.remove(), 130); };
  mask.addEventListener('mousedown', e => { if (e.target === mask) close(); });
  const modal = document.createElement('div');
  modal.className = 'modal steam-modal';
  modal.innerHTML = `
    <div class="modal-head">下载 MOD（SteamCMD 直连）</div>
    <div class="modal-body">
      <pre id="wdLog" style="max-height:220px;overflow:auto;background:var(--panel-2);border:1px solid var(--line);border-radius:6px;padding:8px;font-size:calc(11px*var(--fs-scale));line-height:1.5;white-space:pre-wrap">正在连接 Steam…</pre>
    </div>
    <div class="modal-foot">
      <button class="btn ghost" data-w="rescan" disabled>重新扫描</button>
      <button class="btn primary" data-w="close">关闭</button>
    </div>`;
  mask.appendChild(modal);
  const log = modal.querySelector('#wdLog');
  const say = s => { log.textContent = s; log.scrollTop = log.scrollHeight; };
  const rescanBtn = modal.querySelector('[data-w="rescan"]');
  rescanBtn.onclick = async () => {
    try {
      const d = await api('/api/mods?force=1');
      S.mods = d.items || [];
      S.modByKey = new Map(S.mods.map(m => [m.modFile.toLowerCase(), m]));
      toast('已重新扫描本机订阅', 'ok');
    } catch (e) { toast('扫描失败：' + (e.message || e), 'err'); }
  };
  modal.querySelector('[data-w="close"]').onclick = close;
  poll = setInterval(async () => {
    try {
      const s = await api('/api/workshop/download-status');
      say((s.lines || []).join('\n') || '正在连接 Steam…');
      if (s.downloaded) rescanBtn.disabled = false;
      if (s.state !== 'running') {
        clearInterval(poll); poll = null;
        if (s.state === 'done') toast('下载完成！点「重新扫描」注册进播放集', 'ok', 6000);
        else { failed = true; wsFailAskWeb({ id: s.id || '' }, 'SteamCMD 下载未完成，可能是网络受限。'); }
      }
    } catch (e) { clearInterval(poll); poll = null; }
  }, 1500);
}

function wsWatchDir(id) {
  const t0 = Date.now();
  const timer = setInterval(async () => {
    if (Date.now() - t0 > 10 * 60 * 1000) { clearInterval(timer); return; }
    try {
      const r = await api('/api/workshop/download-check?id=' + encodeURIComponent(id));
      if (r && r.exists) {
        clearInterval(timer);
        toast('检测到 MOD 已下载完成！点「重新扫描」即可注册进播放集', 'ok', 8000);
      }
    } catch (e) { }
  }, 3000);
}

/* ---------------- 文件目录 ---------------- */
async function renderTools() {
  const d = await api('/api/paths');
  const p = d.paths || {};
  const rows = [
    ['游戏根目录', 'binaries / game 所在位置', p.game],
    ['游戏数据目录', 'game 文件夹（common / events / gui）', p.gameData],
    ['游戏 common', 'defines、脚本、事件定义', p.common],
    ['游戏 events', '事件脚本', p.events],
    ['游戏本地化', 'localization 语言文件', p.localization],
    ['游戏 GUI', 'interface 界面定义', p.gui],
    ['用户数据目录', '存档 / 日志 / 配置所在', p.user],
    ['本地 MOD 目录', 'mod/*.mod 与内容文件夹', p.mod],
    ['创意工坊目录', 'workshop/content/1158310', p.workshop],
    ['存档目录', 'save games', p.saves],
    ['日志目录', 'logs（error.log 在这里）', p.logs],
    ['dlc_load.json', '游戏真正读取的启用列表', p.dlcLoad],
    ['launcher-v2.sqlite', '官方启动器播放集数据库', p.sqlite],
    ['自动备份目录', '每次写盘前的备份', p.backup],
  ];
  $('#view').innerHTML = `
    <div class="page-head"><div><div class="page-title">文件目录</div>
      <div class="page-sub">一键打开各种游戏目录与数据文件，排查问题很方便。</div></div>
      <div class="bar"><button class="btn sm ghost" id="tBackup">立即备份数据库</button></div></div>
    <div class="card">
      <div class="tool-grid">
        ${rows.map(([t, s, path]) => `
          <button class="tool" data-open="${esc(path || '')}">
            <div class="ic">▸</div>
            <div class="tx"><strong>${esc(t)}</strong><small>${esc(s)}</small></div>
          </button>`).join('')}
      </div>
    </div>
    <div class="card">
      <div class="card-title">调试提示</div>
      <div class="hint" style="margin-top:8px">
        · 游戏报错时先看 <b>logs/error.log</b>，模组冲突通常会在里面留下 script error。<br>
        · 修改排序后如果游戏里没生效，检查 <b>dlc_load.json</b> 的 enabled_mods 顺序是否与播放集一致。<br>
        · 本启动器每次写入都会把 launcher-v2.sqlite 备份到 backups 目录，最多保留 30 份。
      </div>
    </div>`;
  $$('[data-open]').forEach(b => b.onclick = () => {
    const path = b.dataset.open;
    if (!path) { toast('路径未配置'); return; }
    post('/api/open', { path }).then(r => { if (!r.ok) toast('打开失败：' + path, 'err'); });
  });
  $('#tBackup').onclick = async () => {
    const r = await post('/api/backup', {});
    if (r.ok) toast('已备份：' + (r.files || []).join(T('common.listSep')), 'ok');
    else toast('备份失败', 'err');
  };
}

/* ---------------- 设置 ---------------- */
/* ---------------- 游戏文件 ---------------- */
async function renderGameData() {
  const d = await api('/api/gamedata');
  if (!d.ok) { $('#view').innerHTML = '<div class="empty">读取失败</div>'; return; }

  $('#view').innerHTML = `
    <div class="page-head"><div>
      <div class="page-title">游戏文件</div>
      <div class="page-sub">游戏本体在哪、存档和配置存在哪、模组从哪读。</div>
    </div><div class="bar">
      <button class="btn sm ghost" id="gdRescan">重新扫描</button>
    </div></div>

    <div class="card">
      <div class="card-title">◈ 游戏本体</div>
      <div class="flex-line">
        <div class="fl-main">
          <div class="fl-t">${esc(d.versionFull || d.version || '未知版本')}</div>
          <div class="fl-p mono">${esc(d.gameRoot || '未找到')}</div>
        </div>
        <span class="pill ${d.exeExists ? 'ok' : 'bad'}">${d.exeExists ? '✓ 可用' : '✗ 未找到'}</span>
        <button class="btn sm ghost" data-open="${esc(d.gameRoot)}">打开位置</button>
      </div>
    </div>

    <div class="card">
      <div class="card-title">◈ 存档与配置位置</div>
      <div class="card-desc" style="margin-bottom:10px">
        存档、设置、播放集都放在这里。换目录可以做到「一个游戏，多套独立存档」。
      </div>
      <div class="flex-line">
        <div class="fl-main">
          <div class="fl-t">${d.isDefaultUserDir ? '游戏默认位置' : '自定义位置'}</div>
          <div class="fl-p mono">${esc(d.userDir || '')}</div>
          <div class="fl-s">存档 ${d.saveCount} 个 · 模组 ${d.modCount} 个</div>
        </div>
        <button class="btn sm ghost" data-open="${esc(d.userDir)}">打开</button>
        <button class="btn sm" id="gdChangeUser">更改位置…</button>
      </div>
      ${d.isDefaultUserDir ? '' : `
      <div class="bar" style="margin-top:10px">
        <button class="btn sm ghost" id="gdResetUser">恢复游戏默认位置</button>
      </div>`}
      <div class="hint" style="margin-top:10px">
        改完之后，启动器读写的就是新目录；如果希望游戏本体也读这个目录，
        可以在下面打开「启动时传 -dataPath」。
      </div>
    </div>

    <div class="card">
      <div class="card-title">◈ 模组位置</div>
      <div class="flex-line">
        <div class="fl-main">
          <div class="fl-t">本地模组目录</div>
          <div class="fl-p mono">${esc(d.modDir)}</div>
          <div class="fl-s">本地 ${d.localModCount} 个 · 工坊 ${d.steamModCount} 个</div>
        </div>
        <button class="btn sm ghost" data-open="${esc(d.modDir)}">打开</button>
      </div>
      <div class="flex-line" style="margin-top:12px;border-top:1px solid var(--line-soft);padding-top:12px">
        <div class="fl-main">
          <div class="fl-t">创意工坊下载目录</div>
          <div class="fl-p mono">${esc(d.workshopDir || '未找到')}</div>
          <div class="fl-s">Steam 订阅的模组下载到这里</div>
        </div>
        <button class="btn sm ghost" data-open="${esc(d.workshopDir)}">打开</button>
        <button class="btn sm" id="gdChangeWork">更改位置…</button>
      </div>
    </div>

    <div class="card">
      <div class="card-title">◈ 启动参数</div>
      <label class="chk-row">
        <input type="checkbox" id="gdDataPath" ${d.useDataPathArg ? 'checked' : ''}>
        <span>启动游戏时传 <code>-dataPath</code>
          <small>让游戏本体也去读你上面设置的那个存档目录</small></span>
      </label>
      <div class="hint" style="margin-top:10px">
        这是 Paradox 游戏的通用启动参数，作用是<strong>告诉游戏「用户数据目录」在哪</strong>。<br><br>
        <strong style="color:var(--gold-b)">什么时候要开：</strong>你把上面的「存档与配置位置」改到了别的目录，
        希望<strong>游戏本体</strong>也跟着去新目录读写。<br>
        <strong>什么时候不用开：</strong>用默认位置就行，或者你只是想换个地方放备份、并不打算让游戏跟着走。<br><br>
        不是所有游戏版本都认这个参数。开了之后如果游戏读不到存档，关掉它即可。
      </div>
    </div>`;

  $$('[data-open]').forEach(b => b.onclick = () => post('/api/open', { path: b.dataset.open }));
  $('#gdRescan').onclick = () => { render(); toast('已刷新'); };

  $('#gdChangeUser').onclick = async () => {
    const ok = await ui.confirm(
      '更改存档 / 配置位置会影响存档读取。\n\n' +
      '建议先在旧目录里把存档复制过去，再切换。\n\n继续吗？',
      { title: '更改数据位置', okText: '选择新目录' });
    if (!ok) return;
    const r = await post('/api/pick-dir', { initial: d.userDir });
    if (!r.path) return;
    const r2 = await post('/api/config', { userDir: r.path });
    if (r2.ok) {
      await refreshState();
      S.mods = [];
      const mm = await api('/api/mods?force=1');
      S.mods = mm.items || [];
      S.modByKey = new Map(S.mods.map(x => [x.modFile.toLowerCase(), x]));
      render();
      toast('数据位置已切换，重新扫描到 ' + S.mods.length + ' 个模组', 'ok');
    }
  };

  $('#gdChangeWork').onclick = async () => {
    const r = await post('/api/pick-dir', { initial: d.workshopDir });
    if (!r.path) return;
    const r2 = await post('/api/config', { workshopDir: r.path });
    if (r2.ok) {
      await refreshState();
      const mm = await api('/api/mods?force=1');
      S.mods = mm.items || [];
      S.modByKey = new Map(S.mods.map(x => [x.modFile.toLowerCase(), x]));
      render();
      toast('工坊位置已更新', 'ok');
    }
  };

  const rs = $('#gdResetUser');
  if (rs) rs.onclick = async () => {
    if (!d.defaultUserDir) { toast('未找到默认位置', 'err'); return; }
    const r = await post('/api/config', { userDir: d.defaultUserDir });
    if (r.ok) { await refreshState(); render(); toast('已恢复默认位置', 'ok'); }
  };

  const dp = $('#gdDataPath');
  if (dp) dp.onchange = async () => {
    await post('/api/config', { useDataPathArg: dp.checked });
    await refreshState();
    toast(dp.checked ? '启动时将传 -dataPath' : '已关闭 -dataPath 参数', 'ok');
  };
}

/* ---------------- 内存管理 ---------------- */
async function renderMemory() {
  const m = await api('/api/mem');
  const c = S.state.config || {};
  if (!m.ok) { $('#view').innerHTML = '<div class="empty">读取内存信息失败</div>'; return; }

  const physPct = Math.min(100, Math.round((m.usedPhysGB / m.totalPhysGB) * 100));
  const commitPct = Math.min(100, Math.round((m.usedCommitGB / m.totalCommitGB) * 100));
  const pfs = m.pagefiles || [];
  const pfAuto = pfs.length && pfs.every(p => p.auto);

  const pfRows = pfs.length
    ? pfs.map(p => `<div class="pf-row">
        <span class="pf-path">${esc(p.path)}</span>
        <span class="pf-size">${p.auto ? '系统管理' : p.initialMB + ' ~ ' + p.maxMB + ' MB'}</span>
      </div>`).join('')
    : `<div class="pf-row"><span class="pf-path" style="color:var(--dim-2)">没有读取到分页文件</span></div>`;

  $('#view').innerHTML = `
    <div class="page-head"><div>
      <div class="page-title">内存管理</div>
      <div class="page-sub">CK3 开大包时吃内存和提交量，这里能看还能清。</div>
    </div><div class="bar">
      <span class="mem-load">${TT('已使用')}<b> ${m.loadPercent}%</b></span>
      <button class="btn sm ghost" id="memRefresh">刷新</button>
    </div></div>

    <div class="card">
      <div class="card-title">◈ 内存优化</div>

      <div class="mem-grid">
        <div class="mem-cell">
          <div class="mc-k">可用内存</div>
          <div class="mc-v">${m.availPhysGB}<small>GB</small></div>
          <div class="membar"><i style="width:${physPct}%"></i></div>
          <div class="mc-f">共 ${m.totalPhysGB} GB</div>
        </div>
        <div class="mem-cell">
          <div class="mc-k">系统提交</div>
          <div class="mc-v">${m.usedCommitGB}<small>/ ${m.totalCommitGB} GB</small></div>
          <div class="membar"><i style="width:${commitPct}%;background:linear-gradient(90deg,#a8722f,#e9cb80)"></i></div>
          <div class="mc-f">大包主要吃这个</div>
        </div>
        <div class="mem-cell">
          <div class="mc-k">系统缓存</div>
          <div class="mc-v">${m.cacheMB >= 1024 ? (m.cacheMB / 1024).toFixed(1) : m.cacheMB}<small>${m.cacheMB >= 1024 ? 'GB' : 'MB'}</small></div>
          <div class="mc-f">优化时会释放掉大部分</div>
        </div>
        <div class="mem-cell">
          <div class="mc-k">虚拟内存</div>
          <div class="mc-v">${m.pagefileCount}<small>个分页文件</small></div>
          <div class="mc-f">${pfAuto ? '系统自动管理' : '手动指定大小'}</div>
        </div>
      </div>

      <button class="btn mem-opt${m.isAdmin ? '' : ' need-admin'}" id="memOpt">
        <span class="mo-ic">◈</span>
        <span class="mo-tx">
          <b>一键优化${m.isAdmin ? '' : '（需要管理员权限）'}</b>
          <small>${m.isAdmin
            ? '清理系统待机内存 + 整理各程序工作集，不会关闭任何程序'
            : '点这里以管理员身份重启，才能清理系统待机内存'}</small>
        </span>
        <span class="mo-ar">→</span>
      </button>

      <label class="chk-row">
        <input type="checkbox" id="memMin" ${c.minimizeOnLaunch ? 'checked' : ''}>
        <span>启动游戏后最小化启动器<small>免得挡住游戏窗口</small></span>
      </label>
      <label class="chk-row">
        <input type="checkbox" id="memClose" ${c.closeOnLaunch ? 'checked' : ''}>
        <span>启动游戏后关闭启动器<small>省一点内存；游戏本身不受影响</small></span>
      </label>
    </div>

    <div class="card">
      <div class="card-title">◈ 虚拟内存（分页文件）</div>
      <div class="card-desc" style="margin-bottom:12px">
        提交上限 = 物理内存 + 分页文件。CK3 大包吃满提交量会被系统直接结束进程，而且不留日志。
      </div>
      <div class="pf-box">
        <div class="pf-head">
          <span class="pf-tag">${pfAuto ? '系统自动管理' : '当前使用自定义分页文件'}</span>
          <span class="pf-note">配置由 Windows 维护，这里只读</span>
        </div>
        ${pfRows}
      </div>
      <div class="bar" style="margin-top:12px">
        <button class="btn sm primary" id="memPerf">打开系统性能选项</button>
        <span class="hint">在「高级 → 虚拟内存 → 更改」里调大小</span>
      </div>
      <div class="hint" style="margin-top:10px">
        建议：初始大小设成物理内存的 1.5 倍，最大值 2 倍。改完重启电脑生效。
      </div>
    </div>`;

  $('#memRefresh').onclick = () => render();

  $('#memOpt').onclick = async (e) => {
    const btn = e.currentTarget;
    if (btn.disabled) return;
    if (!m.isAdmin) {
      const ok = await ui.confirm(
        '清理系统待机内存需要管理员权限。\n\n点「以管理员身份重启」后，Windows 会弹确认框，选「是」即可。',
        { title: '需要管理员权限', okText: '以管理员身份重启' });
      if (!ok) return;
      const r = await post('/api/mem/admin-restart', {});
      if (!r.ok) toast(r.error || '提权失败', 'err');
      return;
    }
    btn.disabled = true;
    const sub = btn.querySelector('.mo-tx small');
    const old = sub.textContent;
    sub.textContent = '正在清理待机内存与工作集…';
    try {
      const r = await post('/api/mem/optimize', {});
      if (!r.ok) { toast(r.error || '优化失败', 'err'); return; }
      const parts = [];
      if (r.freedGB > 0) parts.push('释放 ' + r.freedGB + ' GB 物理内存');
      if (r.cacheFreedMB > 0) parts.push('清掉 ' + r.cacheFreedMB + ' MB 缓存');
      if (r.trimmed) parts.push('整理 ' + r.trimmed + ' 个进程');
      toast(parts.length ? '优化完成：' + parts.join('，') : '内存已经很干净了', 'ok');
      await new Promise(s => setTimeout(s, 500));
      render();
    } catch (err) {
      toast('优化失败：' + err.message, 'err');
    } finally {
      btn.disabled = false;
      const s2 = btn.querySelector('.mo-tx small');
      if (s2) s2.textContent = old;
    }
  };

  $('#memMin').onchange = async (e) => {
    await post('/api/config', { minimizeOnLaunch: e.target.checked });
    await refreshState();
    toast(e.target.checked ? '启动游戏后将最小化' : '已关闭最小化', 'ok');
  };
  $('#memClose').onchange = async (e) => {
    await post('/api/config', { closeOnLaunch: e.target.checked });
    await refreshState();
    toast(e.target.checked ? '启动游戏后将关闭启动器' : '已关闭自动退出', 'ok');
  };
  $('#memPerf').onclick = async () => {
    const r = await post('/api/win/perf', {});
    if (!r.ok) toast('打不开系统设置，请手动搜索「查看高级系统设置」', 'err');
  };
}

/* ---------------- 崩溃诊断 ---------------- */
let diagResult = null;

function diagItem(n, title, desc) {
  return `<div class="diag-item"><span class="n">${n}</span>
    <div><strong>${esc(title)}</strong><small>${esc(desc)}</small></div></div>`;
}

function renderSponsor() {
  $('#view').innerHTML = `
    <div class="page-head"><div>
      <div class="page-title">赞助人联</div>
      <div class="page-sub">人联启动器完全免费、开源。如果你觉得好用，欢迎扫码请开发者喝杯奶茶 ❤</div>
    </div></div>

    <div class="sponsor-wrap">
      <div class="sponsor-card">
        <img class="sponsor-img" src="/static/img/sponsor.jpg" alt="赞赏码">
        <div class="sponsor-tip">微信扫一扫，感谢对人联的支持！</div>
      </div>
    </div>`;
}

/* ---------------- 成就 ---------------- */
const ACH = { data: null, filter: 'all', group: '' };

async function renderAchievements() {
  ACH.filter = 'all';
  ACH.group = '';
  $('#view').innerHTML = `
    <div class="page-head">
      <div>
        <div class="page-title">成就</div>
        <div class="page-sub">从本机 Steam 成就缓存读取，<b>读一次就会存到本地</b>，下次打开秒开、不再碰 Steam。
          游戏里解锁了新成就后，点右上角「刷新」重新同步（同时会重新获取全球完成率，需联网）。</div>
      </div>
      <div class="bar">
        <button class="btn ghost sm" id="achRefresh" title="重新读取 Steam 成就缓存 + 刷新全球完成率">⟳ 刷新</button>
      </div>
    </div>
    <div id="achBody"><div class="empty"><div class="big">★</div>读取中…</div></div>`;

  $('#achRefresh').onclick = async (ev) => {
    const b = ev.currentTarget;
    b.disabled = true;
    b.textContent = '刷新中…';
    await loadAchievements(true);
    b.disabled = false;
    b.textContent = '⟳ 刷新';
  };
  await loadAchievements(false);
}

async function loadAchievements(force) {
  const box = $('#achBody');
  if (!box) return;
  box.innerHTML = `<div class="empty"><div class="big">★</div>${
    force ? '正在重新读取 Steam 成就并获取全球完成率，可能要几秒…' : '读取中…'}</div>`;
  let d = null;
  try {
    d = await api('/api/achievements' + (force ? '?force=1' : ''));
  } catch (e) {
    box.innerHTML = `<div class="empty"><div class="big">!</div>读取失败：${esc(e.message)}</div>`;
    return;
  }
  if (!d || !d.ok) {
    box.innerHTML = `<div class="empty"><div class="big">★</div>${esc((d && d.error) || '没有读到成就数据')}</div>`;
    return;
  }
  ACH.data = d;
  paintAchievements();
}

function achRow(it) {
  const hideDesc = it.hidden && !it.unlocked;
  const pct = (it.percent === null || it.percent === undefined) ? '' : `全球 ${it.percent}%`;
  // 图标优先级：游戏本体自带的 DDS（后端转 PNG）→ Steam CDN → 成就名首字
  const local = it.unlocked ? it.iconLocal : (it.iconGrayLocal || it.iconLocal);
  const cdn = it.unlocked ? it.icon : (it.iconGray || it.icon);
  const initial = (it.name || '?').trim().charAt(0);
  const src = local || cdn || '';
  return `<div class="ach-row${it.unlocked ? ' on' : ''}">
    <div class="ach-ic">
      <span class="ach-initial">${esc(initial)}</span>
      ${src ? `<img src="${esc(src)}" data-fb="${esc(cdn && src !== cdn ? cdn : '')}" loading="lazy" alt=""
           onerror="var f=this.dataset.fb;if(f){this.dataset.fb='';this.src=f;}else{this.remove();}">` : ''}
    </div>
    <div class="ach-main">
      <div class="ach-name">${esc(it.name)}
        ${it.rare ? '<span class="ach-tag rare">稀有</span>' : ''}
        ${it.group ? `<span class="ach-tag dlc">${esc(it.group)}</span>` : ''}</div>
      <div class="ach-desc">${esc(hideDesc ? '隐藏成就 · 解锁后显示说明' : (it.desc || '—'))}</div>
    </div>
    <div class="ach-side">
      ${pct ? `<div class="ach-pct">${esc(pct)}</div>` : ''}
      ${it.unlocked
        ? `<div class="ach-date">${esc(it.timeText)}</div>`
        : '<div class="ach-lock">未解锁</div>'}
    </div>
  </div>`;
}

function fmtAchTime(ts) {
  if (!ts) return '—';
  const d = new Date(ts * 1000);
  const p = n => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`;
}

function paintAchievements() {
  const box = $('#achBody');
  const d = ACH.data;
  if (!box || !d) return;

  const total = d.total || 0;
  const unlocked = d.unlocked || 0;
  const pct = total ? Math.round(unlocked / total * 100) : 0;

  const items = (d.items || []).filter(it => {
    if (ACH.group === '__base') {
      if (it.group) return false;
    } else if (ACH.group) {
      if (it.group !== ACH.group) return false;
    }
    if (ACH.filter === 'unlocked') return it.unlocked;
    if (ACH.filter === 'locked') return !it.unlocked;
    if (ACH.filter === 'rare') return !!it.rare;
    return true;
  });

  box.innerHTML = `
    <div class="ach-statbar">
      <div class="ach-prog">
        <div class="ach-prog-bar"><i style="width:${pct}%"></i></div>
        <div class="ach-prog-tx">${TT('已解锁')}<b> ${unlocked}</b> / ${total} · ${TT('完成度')}<b> ${pct}%</b></div>
      </div>
      <div class="ach-kpis">
        <div class="ach-kpi"><b>${unlocked}</b><span>已解锁</span></div>
        <div class="ach-kpi"><b>${total - unlocked}</b><span>未解锁</span></div>
        <div class="ach-kpi"><b>${d.rareUnlocked || 0}</b><span>稀有已解锁</span></div>
      </div>
    </div>

    <div class="bar ach-filters">
      ${[['all', '全部'], ['unlocked', '已解锁'], ['locked', '未解锁'], ['rare', '稀有（<5%）']]
        .map(([k, lab]) => `<span class="chip${ACH.filter === k ? ' on' : ''}" data-af="${k}">${lab}</span>`)
        .join('')}
      ${(d.groups && d.groups.length) ? `<span class="sep"></span>
        <select class="ach-gsel" id="achGroup">
          <option value="">全部 DLC</option>
          <option value="__base"${ACH.group === '__base' ? ' selected' : ''}>基础游戏</option>
          ${d.groups.map(g => `<option value="${esc(g)}"${ACH.group === g ? ' selected' : ''}>${esc(g)}</option>`).join('')}
        </select>` : ''}
      <div class="grow"></div>
      <span class="ach-sync">${d.fromCache ? '本地缓存' : '刚从 Steam 读取'} · ${fmtAchTime(d.cachedAt)}</span>
      ${d.hasGlobal ? '' : '<span class="ach-warn" title="Steam 成就接口连不上（国内通常需要加速器）。开加速器后点右上角「刷新」。">全球完成率暂不可用</span>'}
    </div>

    <div class="ach-list">
      ${items.length ? items.map(achRow).join('') : '<div class="empty"><div class="big">★</div>这个筛选下没有成就</div>'}
    </div>`;

  $$('#achBody [data-af]').forEach(el => {
    el.onclick = () => { ACH.filter = el.dataset.af; paintAchievements(); };
  });
  const gs = $('#achGroup');
  if (gs) gs.onchange = () => { ACH.group = gs.value; paintAchievements(); };
}

function renderDiag() {
  $('#view').innerHTML = `
    <div class="page-head"><div>
      <div class="page-title">崩溃诊断</div>
      <div class="page-sub">游戏闪退、卡死、白屏、模组冲突时，收集一份诊断包发给帮你排查的人。</div>
    </div><div class="bar">
      <button class="btn ghost sm" id="dgOpenUser">${esc(T('settings.openDataDir'))}</button>
    </div></div>

    <div class="card">
      <div class="card-title">◈ 会收集什么</div>
      <div class="card-desc" style="margin-bottom:12px">
        全部在本机完成，不联网、不上传。<b style="color:var(--gold-b)">不含存档本体</b>，
        也不含几十 MB 的崩溃转储——大日志只保留开头和结尾。
      </div>
      <div class="diag-grid">
        ${diagItem('00', '系统与硬件', '内存 / 提交上限 / 显卡 / 磁盘')}
        ${diagItem('01', '游戏版本', '从 logs/system.log 提取')}
        ${diagItem('02', '崩溃记录', '最近 2 次的 exception.txt / meta.yml')}
        ${diagItem('03', 'exceptions', '引擎异常记录')}
        ${diagItem('04', '游戏日志', 'error / game / setup / debug 等')}
        ${diagItem('05', '模组列表', 'dlc_load.json + 启用数量')}
        ${diagItem('06', '启动器日志', '官方启动器最近 15 个日志')}
        ${diagItem('08', '存档列表', '只记录名字和大小')}
      </div>
    </div>

    <div class="card">
      <div class="card-title">◈ 开始收集</div>
      <label class="chk-row">
        <input type="checkbox" id="dgDx">
        <span>同时生成 dxdiag 系统报告
          <small>多花 10~40 秒；排查显卡 / 驱动问题才需要</small></span>
      </label>
      <div class="bar" style="margin-top:14px">
        <button class="btn primary" id="dgRun">▶ 一键收集</button>
        <span class="hint" id="dgProg"></span>
      </div>
    </div>

    <div id="dgResult"></div>`;

  $('#dgRun').onclick = runDiag;
  $('#dgOpenUser').onclick = () => post('/api/open', { path: S.state.userDir });
  if (diagResult) paintDiagResult(diagResult);
}

async function runDiag() {
  const btn = $('#dgRun');
  const prog = $('#dgProg');
  if (!btn) return;
  const dx = $('#dgDx') && $('#dgDx').checked;
  btn.disabled = true;

  const steps = ['采集系统与硬件…', '读取游戏版本…', '收集崩溃记录…', '收集游戏日志…',
    '整理模组与存档…', dx ? '生成 dxdiag（较慢，请稍候）…' : '打包压缩…'];
  let i = 0;
  prog.textContent = steps[0];
  const timer = setInterval(() => {
    i = Math.min(i + 1, steps.length - 1);
    prog.textContent = steps[i];
  }, 1400);

  try {
    const t0 = Date.now();
    const r = await post('/api/diag/collect', { dxdiag: dx });
    if (!r.ok) { toast(r.error || '收集失败', 'err'); return; }
    diagResult = r;
    paintDiagResult(r);
    toast(`收集完成 · ${(r.size / 1048576).toFixed(2)} MB · 用时 ${((Date.now() - t0) / 1000).toFixed(1)} 秒`, 'ok');
    // 收集完直接问存哪儿，省得再点一次
    const s = await post('/api/diag/save-as', { zip: r.zip });
    if (s.ok && !s.cancelled) {
      toast('诊断包已保存到：' + s.path, 'ok');
    } else if (s.ok && s.cancelled) {
      toast('已取消保存。诊断包仍在临时目录，可点「打开临时目录」取。');
    } else {
      toast(s.error || '保存失败', 'err');
    }
  } catch (e) {
    toast('收集失败：' + e.message, 'err');
  } finally {
    clearInterval(timer);
    prog.textContent = '';
    btn.disabled = false;
  }
}

function paintDiagResult(r) {
  const box = $('#dgResult');
  if (!box) return;
  const okCount = (r.items || []).filter(x => x.ok).length;
  box.innerHTML = `
    <div class="card" style="border-color:rgba(63,185,80,.42)">
      <div class="card-title" style="color:#79d18b">
        ✓ 收集完成 · ${(r.size / 1048576).toFixed(2)} MB
      </div>
      <div class="card-desc" style="margin:6px 0 12px">
        ${okCount}/${(r.items || []).length} 项成功　·　${esc(r.name || '')}
      </div>
      <div class="diag-list">
        ${(r.items || []).map(it => `<div class="diag-row ${it.ok ? 'ok' : 'bad'}">
          <span class="mk">${it.ok ? '✓' : '!'}</span>
          <b>${esc(it.name)}</b>
          <span>${esc(it.note || '')}</span>
        </div>`).join('')}
      </div>
      ${(r.summary || []).length ? `
        <div class="diag-sum">
          <div class="diag-sum-t">自动摘要</div>
          ${r.summary.map(s => `<div class="diag-sum-l">${esc(s)}</div>`).join('')}
        </div>` : ''}
      <div class="bar" style="margin-top:14px">
        <button class="btn ghost" id="dgDir">打开临时目录</button>
      </div>
      <div class="hint" style="margin-top:10px">
        收集完会自动弹窗让你选保存位置。如果刚才取消了，诊断包还在临时目录里，点上面按钮去取。
      </div>
    </div>`;

  $('#dgDir').onclick = () => post('/api/diag/open-dir', { dir: r.dir });
}

// 版本配置变更后：清空游戏数据缓存并重读，刷新顶栏与当前视图
function afterProfileChange() {
  S.mods = []; S.modByKey = new Map(); S.play = []; S.collections = []; S.colFilter = '';
  (async () => {
    await reloadGameData();
    paintTop();
    await refreshSteamChip();   // 配置里的工坊/清单路径可能变了，同步刷新 Steam 关联显示
    render();
  })();
}

/* ---------------- 版本切换（独立页） ---------------- */
async function renderVersions() {
  const c = S.state.config || {};

  $('#view').innerHTML = `
    <div class="page-head"><div>
      <div class="page-title">${esc(T('nav.versions'))}</div>
      <div class="page-sub">${esc(T('versions.sub'))}</div>
    </div></div>

    <!-- 版本配置 -->
    <div class="card" id="verCard">
      <div class="card-title">◈ ${esc(T('settings.versionProfiles'))}</div>
      <div class="card-desc" style="margin-bottom:10px">${esc(T('settings.versionProfilesDesc'))}</div>
      <div id="verProfileList"></div>
      <div class="bar" style="margin-top:10px">
        <button class="btn sm" id="verAdd">＋ ${esc(T('settings.addProfile'))}</button>
      </div>
      <div class="hint" id="verDataInfo" style="margin-top:10px;line-height:1.7"></div>
      <div class="bar" style="margin-top:8px">
        <button class="btn sm" id="verDpOn">${esc(T('settings.verDpBtn'))}</button>
      </div>
    </div>

    <!-- 通过 Steam 切换游戏版本 -->
    <div class="card" id="steamCard">
      <div class="card-title">◈ ${esc(T('settings.steamVersion'))}</div>
      <div class="card-desc" style="margin-bottom:10px">${esc(T('settings.steamVersionDesc'))}</div>
      <div class="form-row"><div class="lab">${esc(T('settings.steamCurrent'))}</div>
        <div class="val"><span id="steamCur" style="font-size:calc(13px*var(--fs-scale));color:var(--gold-b);font-weight:600">…</span>
        <button class="btn sm ghost" id="steamRefresh">${esc(T('settings.rescan'))}</button></div></div>
      <div class="form-row"><div class="lab">${esc(T('settings.steamBranch'))}</div>
        <div class="val"><select id="steamSel" style="flex:1;max-width:380px"><option>…</option></select>
        <button class="btn sm" id="steamSwitch" disabled>${esc(T('settings.steamSwitch'))}</button></div></div>
      <div class="hint" id="steamHint" style="margin-top:8px;line-height:1.6"></div>
    </div>`;

  // 版本配置：列表渲染 + 设为当前 / 保存 / 删除 / 新增
  const vlist = $('#verProfileList');
  if (vlist) {
    const vps = (c.profiles || []);
    vlist.innerHTML = vps.map(p => `
      <div class="ver-pf-row ${p.active ? 'active' : ''}" style="flex-direction:column;align-items:stretch;gap:8px">
        <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
          <input class="inp ver-pf-name" data-pf="${esc(p.id)}" data-f="name" value="${esc(p.name)}" placeholder="${esc(T('settings.profileName'))}" style="flex:1;min-width:120px">
          <input class="inp ver-pf-ver" data-pf="${esc(p.id)}" data-f="ver" value="${esc(p.gameVersion || '')}" placeholder="${esc(T('settings.profVerPh'))}" style="width:80px">
          ${p.active
            ? `<span class="ver-pf-tag">${esc(T('settings.profActive'))}</span>`
            : `<button class="btn sm ghost" data-pfact="switch" data-id="${esc(p.id)}">${esc(T('settings.setActive'))}</button>`}
          <button class="btn sm ghost" data-pfact="save" data-id="${esc(p.id)}">${esc(T('common.saveSettings'))}</button>
          ${vps.length > 1 ? `<button class="btn sm danger" data-pfact="del" data-id="${esc(p.id)}">${esc(T('common.delete'))}</button>` : ''}
        </div>
        <div style="display:flex;gap:8px;align-items:center">
          <span style="font-size:calc(12px*var(--fs-scale));color:var(--dim);min-width:60px">${esc(T('settings.profDir'))}</span>
          <input class="inp ver-pf-dir" data-pf="${esc(p.id)}" data-f="dir" id="pfdir_${esc(p.id)}" value="${esc(p.userDir || '')}" placeholder="${esc(T('settings.profDirPh'))}" style="flex:1">
          <button class="btn sm ghost" data-pick="pfdir_${esc(p.id)}">${esc(T('common.browse'))}</button>
          <button class="btn sm ghost" data-openpf="pfdir_${esc(p.id)}">${esc(T('common.open'))}</button>
        </div>
      </div>`).join('');
    $$('#verProfileList [data-pfact]').forEach(b => b.onclick = async () => {
      const id = b.dataset.id, act = b.dataset.pfact;
      if (act === 'del') {
        if (!(await ui.confirm(T('settings.confirmDelProfile'), { danger: true }))) return;
        const r = await post('/api/profile/delete', { id });
        if (r.ok) { S.state = r.state; afterProfileChange(); toast(T('common.deleted'), 'ok'); }
        else toast(r.error || T('common.failed'), 'err');
      } else if (act === 'switch') {
        const r = await post('/api/profile/switch', { id });
        if (r.ok) { S.state = r.state; afterProfileChange(); toast(T('top.versionSwitched'), 'ok'); }
        else toast(r.error || T('common.failed'), 'err');
      } else if (act === 'save') {
        const row = b.closest('.ver-pf-row');
        const name = row.querySelector('[data-f=name]').value.trim();
        const ver = row.querySelector('[data-f=ver]').value.trim();
        const dirEl = row.querySelector('[data-f=dir]');
        const dir = dirEl ? dirEl.value.trim() : '';
        const r = await post('/api/profile/update', { id, name, gameVersion: ver, userDir: dir });
        if (r.ok) { S.state = r.state; afterProfileChange(); toast(T('common.saved'), 'ok'); }
        else toast(r.error || T('common.failed'), 'err');
      }
    });
  }
  $('#verAdd').onclick = async () => {
    const r = await post('/api/profile/add', { name: T('settings.newProfile'), gameVersion: '', gameRoot: '', userDir: '', workshopDir: '' });
    if (r.ok) { S.state = r.state; afterProfileChange(); toast(T('settings.profileAdded'), 'ok'); }
    else toast(r.error || T('common.failed'), 'err');
  };

  // 版本行的「切换」按钮（在 verProfileList 里后生成，需单独绑定，否则点击无反应）
  $$('#verProfileList [data-pick]').forEach(b => b.onclick = async () => {
    const id = b.dataset.pick, inp = $('#' + id);
    const r = await post('/api/pick-dir', { initial: inp ? inp.value : '' });
    if (r && r.path && inp) inp.value = r.path;
  });

  // 版本行的「打开」按钮：像「打开 MOD 目录」一样，直接在资源管理器打开该数据目录
  $$('#verProfileList [data-openpf]').forEach(b => b.onclick = () => {
    const inp = $('#' + b.dataset.openpf);
    const p = inp ? inp.value.trim() : '';
    if (!p) { toast('该版本还没有数据目录，请先填写或点「切换」选择', 'err'); return; }
    post('/api/open', { path: p }).then(r => {
      if (r && r.ok === false) toast(T('common.failed') + '：' + p, 'err');
    }).catch(() => toast(T('common.failed'), 'err'));
  });

  // 多版本「数据隔离」说明 + 一键开/关 -dataPath
  (() => {
    const info = $('#verDataInfo'), btn = $('#verDpOn');
    if (!info) return;
    const on = !!c.useDataPathArg;
    info.textContent = on ? T('settings.verDataOn') : T('settings.verDataOff');
    if (btn) {
      btn.textContent = on ? T('settings.verDpOffBtn') : T('settings.verDpBtn');
      btn.className = on ? 'btn sm ghost' : 'btn sm';
      btn.onclick = async () => {
        btn.disabled = true;
        await post('/api/config', { useDataPathArg: !on });
        await refreshState();
        toast(on ? T('settings.verDataDisabled') : T('settings.verDataEnabled'), 'ok');
        render();
      };
    }
  })();

  // 通过 Steam 切换游戏版本
  const steamSel = $('#steamSel');
  if (steamSel) {
    const loadSteam = async () => {
      const cur = $('#steamCur'), hint = $('#steamHint'), sw = $('#steamSwitch');
      cur.textContent = '…'; sw.disabled = true; hint.textContent = '';
      let r;
      try {
        // 后端注册的是 MapGet，这里必须走 GET；用 post 会 405 并让界面永远卡在"…"
        r = await api('/api/steam/branches');
      } catch (e) {
        cur.textContent = '—';
        steamSel.innerHTML = '<option>…</option>';
        hint.innerHTML = '<span style="color:var(--danger,#e24b4a)">读取 Steam 信息失败：' + esc(e.message) + '</span>';
        return;
      }
      if (!r.ok) { cur.textContent = '—'; hint.textContent = r.error || '读取失败'; return; }
      if (!r.manifest) {
        cur.textContent = T('settings.steamNotManaged');
        steamSel.innerHTML = ''; hint.textContent = '';
        return;
      }
      const cb = (r.branches || []).find(b => b.key === r.current);
      cur.textContent = r.current + (cb && cb.buildid ? '  ·  build ' + cb.buildid : '');
      steamSel.innerHTML = (r.branches || []).map(b =>
        `<option value="${esc(b.key)}" ${b.key === r.current ? 'selected' : ''}>${esc(b.key)}${b.desc ? ' — ' + esc(b.desc) : ''}</option>`).join('');
      if (r.steamRunning) {
        hint.innerHTML = '<span style="color:var(--danger,#e24b4a)">' + esc(T('settings.steamAutoRestart')) + '</span>';
      } else {
        hint.textContent = T('settings.steamCloseHint');
      }
      sw.disabled = false;
    };
    loadSteam();
    $('#steamRefresh').onclick = loadSteam;
    let steamBusy = false;
    $('#steamSwitch').onclick = async () => {
      if (steamBusy) return;
      const key = steamSel.value;
      if (!key) return;
      if (!(await ui.confirm(T('settings.steamSwitchConfirm', { v: key }),
            { title: T('settings.steamSwitchTitle'), okText: T('settings.steamRestartOk') }))) return;
      steamBusy = true;
      const sw = $('#steamSwitch'), hint = $('#steamHint'), old = sw.textContent;
      sw.disabled = true; sw.textContent = '…';
      hint.innerHTML = '<span style="color:var(--gold-b)">' + esc(T('settings.steamSwitching')) + '</span>';
      try {
        const r = await post('/api/steam/switch', { key, restart: true });
        if (r.ok) {
          toast(T('settings.steamSwitched', { v: key }), 'ok');
          await loadSteam();
          await refreshState();
        } else toast(r.error || T('common.failed'), 'err');
      } catch (e) {
        toast(String((e && e.message) || e), 'err');
      } finally {
        steamBusy = false; sw.disabled = false; sw.textContent = old;
      }
    };
  }
}

async function renderSettings() {
  const c = S.state.config || {};
  let bk = { items: [], total: 0, max: 12, dir: '' };
  try { bk = await api('/api/backups'); } catch (e) {}

  const st = S.state;
  const bakList = bk.items || [];
  const bakRows = bakList.length
    ? bakList.slice(0, 8).map(b => `
        <tr>
          <td class="mono" style="font-size:calc(11.5px*var(--fs-scale))">${esc(b.name)}</td>
          <td class="mono">${fmtSize(b.size)}</td>
          <td class="mono">${fmtTime(b.mtime)}</td>
          <td style="text-align:right">
            <button class="btn sm ghost" data-restore="${esc(b.name)}">${esc(T('settings.restore'))}</button>
          </td>
        </tr>`).join('')
    : `<tr><td colspan="4" style="color:var(--dim2);text-align:center;padding:22px">${esc(T('settings.noBackups'))}</td></tr>`;

  $('#view').innerHTML = `
    <div class="page-head"><div>
      <div class="page-title">${esc(T('settings.title'))}</div>
      <div class="page-sub">${esc(T('settings.sub'))}</div>
    </div><div class="bar">
      <button class="btn sm ghost" id="cReload">${esc(T('settings.redetect'))}</button>
      <button class="btn primary sm" id="cSave">${esc(T('common.saveSettings'))}</button>
    </div></div>

    <!-- 界面语言 -->
    <div class="card">
      <div class="card-title">◈ ${esc(T('settings.language'))}</div>
      <div class="card-desc" style="margin-bottom:10px">${esc(T('settings.languageDesc'))}</div>
      <div class="form-row"><div class="lab">${esc(T('settings.language'))}</div>
        <div class="val">
          <select id="cLang" style="min-width:200px">
            ${I18N_LANGS.map(l => `<option value="${l.code}"${l.code === i18nLang() ? ' selected' : ''}>${esc(l.label)}</option>`).join('')}
          </select>
        </div></div>
    </div>

    <!-- 外观主题 -->
    <div class="card">
      <div class="card-title">◈ 外观主题</div>
      <div class="card-desc" style="margin-bottom:12px">整套配色（背景、卡片、边框、强调色）会一起变化，点一下立即生效，下次打开仍然保留。</div>
      <div class="theme-preview"></div>
      <div class="theme-sub">明暗</div>
      <div class="mode-cards">
        <button class="mode-card" data-mode="light"><span class="mc-ic">☀</span><span><span class="mc-t">白天模式</span><br><span class="mc-d">浅色底，适合白天</span></span></button>
        <button class="mode-card" data-mode="dark"><span class="mc-ic">☾</span><span><span class="mc-t">夜晚模式</span><br><span class="mc-d">深色底，适合夜间</span></span></button>
      </div>
      <div class="theme-sub">主题色</div>
      <div class="palette-grid">
        <button class="palette" data-accent="gold"><span class="pal-swatch"></span><span class="pal-name">典雅金</span></button>
        <button class="palette" data-accent="red"><span class="pal-swatch"></span><span class="pal-name">辰砂红</span></button>
        <button class="palette" data-accent="orange"><span class="pal-swatch"></span><span class="pal-name">心想事橙</span></button>
        <button class="palette" data-accent="yellow"><span class="pal-swatch"></span><span class="pal-name">琥珀黄</span></button>
        <button class="palette" data-accent="green"><span class="pal-swatch"></span><span class="pal-name">芸豆青</span></button>
        <button class="palette" data-accent="white"><span class="pal-swatch"></span><span class="pal-name">极简白</span></button>
        <button class="palette" data-accent="purple"><span class="pal-swatch"></span><span class="pal-name">丁香紫</span></button>
      </div>
    </div>

    <!-- 界面字体大小 -->
    <div class="card">
      <div class="card-title">◈ 界面字体大小</div>
      <div class="card-desc" style="margin-bottom:12px">觉得字太小可以放大，只缩放文字大小、不改变界面布局（和 QQ 的字体大小一样），拖动滑块立即生效，下次打开仍然保留。</div>
      <div class="mode-cards">
        <button class="mode-card font-card" data-scale="0.9"><span class="mc-ic" style="font-size:calc(13px*var(--fs-scale))">A</span><span><span class="mc-t">小（90%）</span><br><span class="mc-d">屏幕小、想多看内容</span></span></button>
        <button class="mode-card font-card" data-scale="1"><span class="mc-ic" style="font-size:calc(16px*var(--fs-scale))">A</span><span><span class="mc-t">中（100%）</span><br><span class="mc-d">默认大小，推荐</span></span></button>
        <button class="mode-card font-card" data-scale="1.15"><span class="mc-ic" style="font-size:calc(19px*var(--fs-scale))">A</span><span><span class="mc-t">大（115%）</span><br><span class="mc-d">字大看得清楚</span></span></button>
      </div>
      <div class="theme-sub">自定义大小（85% ~ 150%）</div>
      <div class="font-slider-row">
        <span class="hint">85%</span>
        <input type="range" id="fontRange" min="0.85" max="1.5" step="0.05">
        <span class="hint">150%</span>
        <b id="fontVal" class="font-val">100%</b>
      </div>
    </div>

    <!-- 兼容补丁文件 -->
    <div class="card">
      <div class="card-title">◈ 兼容补丁文件</div>
      <div class="card-desc" style="margin-bottom:10px">把你自己的兼容补丁文件放在这个目录里，方便随时查看和替换。</div>
      <div class="form-row"><div class="lab">补丁目录</div>
        <div class="val">
          <span style="flex:1;font-size:calc(12px*var(--fs-scale));color:var(--dim);word-break:break-all">RenlianData\\patches</span>
          <button class="btn sm ghost" id="patchOpen">打开目录</button>
        </div></div>
      <div class="hint" style="margin-top:8px;line-height:1.7">
        启动器不提供、不下载、不注入任何补丁文件，只负责管理你自己放进来的文件；也不会自动清理游戏目录，避免误删。
      </div>
    </div>

    <!-- 路径 -->
    <div class="card">
      <div class="card-title">◈ ${esc(T('settings.paths'))}</div>
      <div class="card-desc" style="margin-bottom:10px">${esc(T('settings.pathsDesc'))}</div>
      ${(() => {
        const issues = st.pathIssues || [];
        if (!issues.length) return `<div class="path-state ok">✓ 三个目录都已就位，可以正常启动游戏</div>`;
        const names = issues.map(x => x.label).join('、');
        return `<div class="path-state warn">⚠ 还缺：${esc(names)}。点每项右侧「切换」选到对应目录，或点下方「重新探测」自动找。</div>`;
      })()}
      <div class="form-row"><div class="lab">${esc(T('settings.gameRoot'))}</div>
        <div class="val"><input type="text" id="cGame" class="${(st.pathIssues||[]).some(x=>x.field==='gameRoot')?'path-bad':''}" value="${esc(c.gameRoot || '')}" placeholder="${esc(T('settings.gameRootPh'))}">
        <button class="btn sm ghost" data-pick="cGame">${esc(T('common.browse'))}</button>
        <button class="btn sm ghost" data-openpath="cGame">${esc(T('common.open'))}</button></div></div>
      <div class="form-row"><div class="lab">${esc(T('settings.userDir'))}</div>
        <div class="val"><input type="text" id="cUser" class="${(st.pathIssues||[]).some(x=>x.field==='userDir')?'path-bad':''}" value="${esc(c.userDir || '')}" placeholder="${esc(T('settings.userDirPh'))}">
        <button class="btn sm ghost" data-pick="cUser">${esc(T('common.browse'))}</button>
        <button class="btn sm ghost" data-openpath="cUser">${esc(T('common.open'))}</button></div></div>
      <div class="form-row"><div class="lab">${esc(T('settings.workshopDir'))}</div>
        <div class="val"><input type="text" id="cWork" class="${(st.pathIssues||[]).some(x=>x.field==='workshopDir')?'path-bad':''}" value="${esc(c.workshopDir || '')}" placeholder="steamapps\\workshop\\content\\1158310">
        <button class="btn sm ghost" data-pick="cWork">${esc(T('common.browse'))}</button>
        <button class="btn sm ghost" data-openpath="cWork">${esc(T('common.open'))}</button></div></div>
      <div class="form-row"><div class="lab">${esc(T('settings.cacheDir'))}</div>
        <div class="val"><input type="text" id="cCache" value="${esc(c.cacheDir || '')}" placeholder="${esc(T('settings.cacheDirPh'))}">
        <button class="btn sm ghost" data-pick="cCache">${esc(T('common.browse'))}</button>
        <button class="btn sm ghost" data-openpath="cCache">${esc(T('common.open'))}</button></div>
        <div class="hint" style="margin:6px 0 0 0;line-height:1.6">${esc(T('settings.cacheDirHint'))}
          <br>${esc(T('settings.current'))} <b style="color:var(--gold-b);word-break:break-all">${esc(st.cacheDir || '')}</b></div></div>
      <div class="form-row"><div class="lab">${esc(T('settings.gameVersion'))}</div>
        <div class="val"><input type="text" id="cVer" value="${esc(c.gameVersion || st.version || '')}" style="max-width:110px" placeholder="${esc(T('settings.gameVersionPh'))}">
        <button class="btn sm ghost" id="cVerSync">${esc(T('settings.syncVersion'))}</button>
        <button class="btn sm ghost" id="cVerRefresh">${esc(T('settings.refresh'))}</button>
        <span class="hint">${esc(T('settings.detected'))} <b style="color:var(--gold-b)">${esc(st.versionFull || st.version || '—')}</b></span></div>
        <div class="hint" style="margin:6px 0 0 0;line-height:1.6">
          ${esc(T('settings.gameVersionNote'))}
        </div></div>
    </div>

    <!-- 数据位置 -->
    <div class="card">
      <div class="card-title">◈ ${esc(T('settings.dataLocation'))}</div>
      <div class="card-desc" style="margin-bottom:10px">${esc(T('settings.dataLocationDesc'))}</div>
      <div class="form-row"><div class="lab">${esc(T('settings.modDir'))}</div>
        <div class="val"><span style="flex:1;font-size:calc(12px*var(--fs-scale));color:var(--dim);word-break:break-all">${esc((c.userDir || '') + '\\mod')}</span>
        <button class="btn sm ghost" data-dir="mod">${esc(T('common.open'))}</button></div></div>
      <div class="form-row"><div class="lab">${esc(T('settings.saveDir'))}</div>
        <div class="val"><span style="flex:1;font-size:calc(12px*var(--fs-scale));color:var(--dim);word-break:break-all">${esc((c.userDir || '') + '\\save games')}</span>
        <button class="btn sm ghost" data-dir="saves">${esc(T('common.open'))}</button></div></div>
      <div class="form-row"><div class="lab">${esc(T('settings.modIndex'))}</div>
        <div class="val"><span style="flex:1;font-size:calc(12px*var(--fs-scale));color:var(--dim)">${T('settings.modIndexText', { n: S.mods.length, e: st.dlcLoad?.enabledCount || 0 })}</span>
        <button class="btn sm ghost" id="cRescan">${esc(T('settings.rescan'))}</button></div></div>
    </div>

    <!-- 备份 -->
    <div class="card">
      <div class="card-title">◈ ${esc(T('settings.backups'))}</div>
      <div class="card-desc" style="margin-bottom:10px">
        ${T('settings.backupsDesc', { max: bk.max || 12, n: bakList.length, size: fmtSize(bk.total) })}
      </div>
      <div style="max-height:240px;overflow:auto">
        <table class="tbl"><thead><tr>
          <th>${esc(T('settings.colFile'))}</th><th style="width:80px">${esc(T('settings.colSize'))}</th><th style="width:140px">${esc(T('settings.colTime'))}</th><th style="width:70px"></th>
        </tr></thead><tbody>${bakRows}</tbody></table>
      </div>
      <div class="bar" style="margin-top:12px">
        <button class="btn sm" id="bkNow">${esc(T('settings.backupNow'))}</button>
        <button class="btn sm ghost" id="bkOpen">${esc(T('settings.openBackupDir'))}</button>
        <button class="btn sm danger" id="bkClear">${esc(T('settings.clearBackups'))}</button>
      </div>
    </div>

    <!-- 维护 -->
    <div class="card">
      <div class="card-title">◈ ${esc(T('settings.maintenance'))}</div>
      <div class="bar" style="margin-top:10px">
        <button class="btn sm ghost" id="mkShortcut">${esc(T('settings.createShortcut'))}</button>
        <button class="btn sm ghost" data-raw="${esc(st.dataDir || '')}" title="人联启动器自己的配置、缓存、日志、备份所在目录">${esc(T('settings.openLauncherDir'))}</button>
        <button class="btn sm ghost" data-raw="${esc(c.userDir || '')}" title="游戏存档、配置与官方启动器数据库所在目录">${esc(T('settings.openDataDir'))}</button>
      </div>
      <div class="hint" style="margin-top:12px">
        ${T('settings.logHint')}
      </div>
    </div>

    <div class="card">
      <div class="card-title">◈ ${esc(T('settings.update'))}</div>
      <div class="card-desc" style="margin-bottom:10px">${esc(T('settings.updateDesc'))}</div>
      <div class="form-row"><div class="lab">${esc(T('settings.currentVersion'))}</div>
        <div class="val"><span style="font-size:calc(13px*var(--fs-scale));color:var(--gold-b);font-weight:600">v${LAUNCHER_VERSION}</span>
        <button class="btn sm ghost" id="cCheckUpd">${esc(T('settings.checkUpdate'))}</button></div></div>
      <div id="updResult" class="upd-result" style="display:none"></div>
    </div>

    <div class="card">
      <div class="card-title">◈ ${esc(T('settings.about'))}</div>
      <div class="hint" style="margin-top:8px">
        ${T('settings.aboutBody', { dir: bk.dir || st.backupDir || '' })}
      </div>
      <div class="about-contact">
        <div class="ac-row"><span class="ac-k">${esc(T('settings.feedbackGroup'))}</span><span class="ac-v">793826682</span></div>
        <div class="ac-row"><span class="ac-k">${esc(T('settings.xiaoheihe'))}</span><span class="ac-v">人联永不服输</span></div>
      </div>
      <div class="bar" style="margin-top:14px">
        <button class="btn danger sm" id="aExit">${esc(T('settings.exit'))}</button>
      </div>
    </div>`;

  $$('[data-pick]').forEach(b => b.onclick = async () => {
    const id = b.dataset.pick;
    const r = await post('/api/pick-dir', { initial: $('#' + id).value });
    if (r.path) $('#' + id).value = r.path;
  });
  $$('[data-openpath]').forEach(b => b.onclick = () => {
    // 缓存目录留空表示跟随程序目录，这时直接打开实际使用的目录
    let v = $('#' + b.dataset.openpath).value;
    if (!v && b.dataset.openpath === 'cCache') v = st.cacheDir || '';
    if (!v) { toast(TT('请先填写或选择目录'), 'err'); return; }
    post('/api/open', { path: v });
  });
  $$('[data-raw]').forEach(b => b.onclick = () => post('/api/open', { path: b.dataset.raw }));
  $$('[data-dir]').forEach(b => b.onclick = async () => {
    const d = b.dataset.dir;
    const p = d === 'mod' ? (c.userDir || '') + '\\mod' : (c.userDir || '') + '\\save games';
    post('/api/open', { path: p });
  });

  // 兼容补丁目录：后端会先创建目录再打开
  $('#patchOpen').onclick = async () => {
    const r = await post('/api/patches/open', {});
    if (!r.ok) toast(r.error || '打开补丁目录失败', 'err');
    else toast('已打开兼容补丁目录', 'ok');
  };

  $('#cVerSync').onclick = () => {
    $('#cVer').value = st.version || '';
    toast('已同步为检测版本', 'ok');
  };
  $('#cVerRefresh').onclick = async () => {
    await refreshState();
    render();
    toast(T('settings.versionRefreshed'), 'ok');
  };
  $('#cLang').onchange = async (e) => {
    const lang = e.target.value;
    if (!setLang(lang)) return;
    applyI18nDom();
    paintTop();
    const r = await post('/api/ui-lang', { lang });
    render();     // 整个页面按新语言重绘
    toast(r.ok ? T('settings.langChanged') : T('settings.langFailed'), r.ok ? 'ok' : 'err');
  };

  $('#cSave').onclick = async () => {
    // 游戏版本：留空则自动跟随检测到的真实版本，避免兼容性误判
    const gv = $('#cVer').value.trim();
    const realVer = st.version || '';
    let r;
    try {
      r = await post('/api/config', {
        gameRoot: $('#cGame').value, userDir: $('#cUser').value,
        workshopDir: $('#cWork').value, gameVersion: gv || realVer,
        cacheDir: $('#cCache') ? $('#cCache').value : undefined,
      });
    } catch (e) {
      toast('保存失败：无法写入配置文件', 'err');
      return;
    }
    if (!r || r.ok === false) {
      toast('保存失败：' + ((r && r.error) || '未知错误'), 'err');
      return;
    }
    await refreshState();
    const d = await api('/api/mods?force=1');
    S.mods = d.items || [];
    S.modByKey = new Map(S.mods.map(m => [m.modFile.toLowerCase(), m]));
    if (r.pathIssues && r.pathIssues.length) {
      toast('设置已保存，但路径有问题', 'err');
      pathIssueDialog(r.pathIssues);
    } else {
      toast(`设置已保存 · 识别到 ${S.mods.length} 个模组`, 'ok');
    }
    renderSettings();
  };

  // 版本自检：内置固定更新源，无需用户填地址
  const checkUpdate = async () => {
    const box = $('#updResult');
    const url = (c && c.updateUrl && /^https?:/i.test(c.updateUrl))
      ? c.updateUrl : 'https://gitee.com/xiaoyi2233/renlian-launcher/raw/master/version.json';
    box.style.display = 'block'; box.className = 'upd-result upd-loading';
    box.textContent = T('settings.checking');
    const r = await post('/api/check-update', { url, current: LAUNCHER_VERSION });
    if (!r.ok) { box.className = 'upd-result upd-err'; box.textContent = T('settings.updateFail') + '：' + (r.error || ''); return; }
    if (r.updateAvailable) {
      box.className = 'upd-result upd-new';
      box.innerHTML = T('settings.updateNew', { v: r.latest })
        + (r.date ? `（${esc(r.date)}）` : '')
        + (r.notes ? `<div class="upd-notes">${esc(r.notes)}</div>` : '')
        + (r.url ? `<a class="upd-dl" href="${esc(r.url)}" target="_blank" rel="noopener">${esc(T('settings.download'))}</a>` : '');
    } else {
      box.className = 'upd-result upd-ok';
      box.textContent = T('settings.updateLatest', { v: r.latest });
    }
  };
  $('#cCheckUpd').onclick = checkUpdate;
  $('#cReload').onclick = async () => {
    const r = await post('/api/config', { resetPaths: true });
    if (!r || r.ok === false) {
      toast('重新探测失败：' + ((r && r.error) || '未知错误'), 'err');
      return;
    }
    await refreshState(); renderSettings();
    if ((S.state.pathIssues || []).length) {
      pathIssueDialog(S.state.pathIssues);
    } else {
      toast('已重新探测路径', 'ok');
    }
  };
  $('#cRescan').onclick = async () => {
    saveState('重新扫描中…', true);
    const d = await api('/api/mods?force=1');
    S.mods = d.items || [];
    S.modByKey = new Map(S.mods.map(m => [m.modFile.toLowerCase(), m]));
    saveState(T('home.loaded', { n: S.mods.length }));
    renderSettings();
    toast('扫描完成：' + S.mods.length + ' 个模组', 'ok');
  };

  $('#bkNow').onclick = async () => {
    const r = await post('/api/backup', {});
    toast(r.files?.length ? '已备份：' + r.files.join(T('common.listSep')) : '内容未变，无需重复备份', 'ok');
    renderSettings();
  };
  $('#bkOpen').onclick = () => post('/api/open', { path: bk.dir || st.backupDir });
  $('#bkClear').onclick = async () => {
    const ok = await ui.confirm(`确定清空全部 ${bakList.length} 份备份？\n\n清空后无法靠备份回滚，但不会影响当前游戏数据。`, {
      title: '清空备份', okText: '清空', danger: true,
    });
    if (!ok) return;
    const r = await post('/api/backups/clear', {});
    toast(`已清空 ${r.removed} 份备份`, 'ok');
    renderSettings();
  };
  $$('[data-restore]').forEach(b => b.onclick = async () => {
    const name = b.dataset.restore;
    const ok = await ui.confirm(`确定用这个备份覆盖当前文件？\n\n${name}\n\n当前文件会先自动备份一份，出问题还能再切回来。`, {
      title: '恢复备份', okText: '恢复', danger: true,
    });
    if (!ok) return;
    const r = await post('/api/backups/restore', { name });
    if (r.ok) { toast('已恢复 ' + r.restored + '，建议重启启动器', 'ok'); await refreshState(); }
    else toast(r.error || '恢复失败', 'err');
  });

  $('#mkShortcut').onclick = async () => {
    const r = await post('/api/shortcut', {});
    toast(r.ok ? '已在桌面创建「人联启动器」快捷方式' : (r.error || '创建失败'), r.ok ? 'ok' : 'err');
  };
  $('#aExit').onclick = async () => {
    const ok = await ui.confirm('确定退出启动器？\n\n游戏进程不会受影响。', { title: '退出', okText: '退出' });
    if (!ok) return;
    await post('/api/exit', {});
    window.close();
  };
}

/* ---------------- 顶栏动作 ---------------- */
function launch() {
  const psid = S.state.activePlayset;
  if (S.play.length) {
    // 有未保存改动时先保存，保证进游戏的是最新顺序
    clearTimeout(S.dirtyTimer);
    post('/api/playset/save', {
      id: psid,
      items: S.play.map(x => ({ modId: x.modId || null, modFile: x.modFile, enabled: x.enabled })),
    }).finally(() => doLaunch(psid));
  } else doLaunch(psid);
}
async function doLaunch(psid) {
  saveState('正在启动游戏…', true);
  const r = await post('/api/launch', { psid });
  if (r.ok) {
    toast('游戏已启动，本启动器可以关掉了', 'ok');
    if (r.steamStarted) {
      toast(r.steamReady
        ? 'Steam 原本没在运行，已自动启动并等待就绪——工坊 mod 需要它才能加载'
        : '⚠ Steam 启动等待超时：若游戏里 mod 列表为空，请先开好 Steam 再启动', r.steamReady ? 'ok' : 'err');
    }
    if (r.overwritten) {
      toast('检测到官方启动器改写过启停文件，已按人联播放集重新写入', 'ok');
    }
    saveState('游戏运行中');
    const cfg = S.state.config || {};
    // 等游戏进程真正起来再动手，免得把启动流程打断
    if (cfg.closeOnLaunch) {
      setTimeout(() => { post('/api/exit', {}); window.close(); }, 1500);
    } else if (cfg.minimizeOnLaunch) {
      setTimeout(() => post('/api/launcher/minimize', {}), 1500);
    }
  } else {
    toast(r.error || '启动失败', 'err');
    saveState('启动失败');
  }
}
$('#btnLaunch').onclick = launch;
$('#btnOfficial').onclick = async () => {
  const r = await post('/api/launch-official', {});
  toast(r.ok ? '已打开官方启动器' : (r.error || '未找到官方启动器'), r.ok ? 'ok' : 'err');
};
$('#btnHelp').onclick = async () => {
  const r = await post('/api/open-tutorial', {});
  if (!r.ok) toast(r.error || '打不开使用教程', 'err');
};
$('#quickPlayset').onchange = async e => {
  const r = await post('/api/playset/activate', { id: e.target.value });
  if (!r.ok) { toast(r.error || '切换播放集失败', 'err'); return; }
  await refreshState();
  if (S.route === 'mods') await loadPlayset(); else render();
  toast('已切换到：' + e.target.selectedOptions[0].textContent, 'ok');
};

/* ---------------- 键盘快捷键 ---------------- */
window.addEventListener('blur', () => { if (drag) cleanupDrag(); });
document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && drag) { cleanupDrag(); return; }
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f' && S.route === 'mods') {
    e.preventDefault();
    $('#qLeft')?.focus();
  }
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
    e.preventDefault();
    if (S.route === 'mods') { clearTimeout(S.dirtyTimer); savePlayset(); }
  }
});

/* ---------------- 外部文件拖入：自动解压安装 mod ---------------- */
function initFileDrop() {
  let depth = 0;
  const mask = document.createElement('div');
  mask.className = 'filedrop-mask';
  mask.innerHTML = `<div class="filedrop-box">
      <div class="filedrop-ico">📦</div>
      <div class="filedrop-t">松开即可安装 Mod</div>
      <div class="filedrop-s">支持 .zip / .7z / .rar，自动解压到本地 mod 目录</div>
    </div>`;
  document.body.appendChild(mask);

  // 标记「从页面内开始的拖拽」，dragend / drop 后复位
  let internalDrag = false;
  const hasFiles = e => {
    // 页面内部拖动（比如拖 mod 缩略图）不能触发安装，否则 Chromium 会把图片当成文件拖拽
    if (internalDrag) return false;
    const t = e.dataTransfer && e.dataTransfer.types;
    return !!t && [...t].indexOf('Files') >= 0;
  };
  const hide = () => { depth = 0; mask.classList.remove('show'); };

  window.addEventListener('dragstart', () => { internalDrag = true; }, true);
  window.addEventListener('dragend', () => { internalDrag = false; }, true);
  window.addEventListener('drop', () => { setTimeout(() => { internalDrag = false; }, 0); }, true);

  window.addEventListener('dragenter', e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    depth++;
    mask.classList.add('show');
  });
  window.addEventListener('dragover', e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    try { e.dataTransfer.dropEffect = 'copy'; } catch (err) {}
  });
  window.addEventListener('dragleave', e => {
    if (depth === 0) return;
    depth = Math.max(0, depth - 1);
    if (depth === 0) mask.classList.remove('show');
  });
  window.addEventListener('drop', async e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    e.stopPropagation();
    hide();
    const files = [...(e.dataTransfer.files || [])];
    if (files.length) await importArchives(files);
  });
  window.addEventListener('blur', hide);
}

// 带上传进度的上传（fetch 拿不到上传进度，所以用 XHR）
function uploadArchive(file, onPct) {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    xhr.open('POST', '/api/mod/import-archive', true);
    xhr.setRequestHeader('X-File-Name', encodeURIComponent(file.name));
    xhr.responseType = 'text';
    xhr.timeout = 600000;   // 上传 + 解压，10 分钟上限
    xhr.upload.onprogress = e => {
      if (e.lengthComputable) {
        const pct = Math.floor(e.loaded / e.total * 100);
        onPct(Math.max(1, Math.min(100, pct)));
      }
    };
    xhr.onload = () => {
      try {
        resolve(JSON.parse(xhr.responseText));
      } catch (err) {
        reject(new Error('服务端返回内容异常'));
      }
    };
    xhr.onerror = () => reject(new Error('上传中断（网络或服务异常）'));
    xhr.ontimeout = () => reject(new Error('超时（超过 10 分钟）'));
    xhr.send(file);
  });
}

async function importArchives(files) {
  const okExt = /\.(zip|7z|rar)$/i;
  const list = files.filter(f => okExt.test(f.name));
  if (!list.length) {
    toast('只支持 .zip / .7z / .rar 压缩包', 'err');
    return;
  }

  const box = document.createElement('div');
  box.className = 'import-mask';
  box.innerHTML = `<div class="import-box">
      <div class="import-t">正在安装 Mod（${list.length} 个）</div>
      <div class="import-list"></div>
      <div class="import-tip">大包解压需要时间，请别关窗口（可用 Esc 关掉这个提示）</div>
    </div>`;
  document.body.appendChild(box);
  const listEl = box.querySelector('.import-list');
  const escClose = e => { if (e.key === 'Escape') box.remove(); };
  document.addEventListener('keydown', escClose);

  let ok = 0, fail = 0;
  const MAX_MB = 400;   // 与后端上限一致
  try {
    for (const f of list) {
      const row = document.createElement('div');
      row.className = 'import-row';
      const mb = (f.size / 1048576).toFixed(1);
      row.innerHTML = `<span class="ir-name" title="${esc(f.name)}">${esc(f.name)}`
        + `<small class="ir-size">${mb}MB</small></span><span class="ir-st">准备中…</span>`;
      listEl.appendChild(row);
      const st = row.querySelector('.ir-st');

      // 超大包先在本地拦掉：整个文件要过一遍内存，几百 MB 以上容易把渲染进程拖死
      if (f.size > MAX_MB * 1048576) {
        fail++;
        st.textContent = `✗ 超过 ${MAX_MB}MB，请先手动解压后把文件夹放进 mod 目录`;
        st.className = 'ir-st err';
        continue;
      }

      try {
        const d = await uploadArchive(f, pct => {
          st.textContent = pct >= 100 ? '解压中…' : `上传 ${pct}%`;
          st.className = 'ir-st';
        });
        if (d.ok) {
          ok++;
          st.textContent = '✓ ' + (d.name || '已安装');
          st.className = 'ir-st ok';
        } else {
          fail++;
          st.textContent = '✗ ' + (d.error || '失败');
          st.className = 'ir-st err';
        }
      } catch (err) {
        fail++;
        st.textContent = '✗ ' + (err.message || '失败');
        st.className = 'ir-st err';
      }
    }
  } finally {
    // 无论成功失败/中途异常，遮罩都必须收掉，否则界面就"黑屏"了
    document.removeEventListener('keydown', escClose);
    await new Promise(res => setTimeout(res, fail ? 2800 : 1200));
    box.remove();
  }

  if (!ok) {
    toast('安装失败——检查一下压缩包是否加密/损坏，或里面缺 descriptor.mod', 'err');
    return;
  }
  toast(`已安装 ${ok} 个 Mod${fail ? `，${fail} 个失败` : ''}`, fail ? '' : 'ok');
  await refreshStateQuiet();
  if (S.route === 'mods') await refreshMods();
  else render();
}

initFileDrop();
boot();
