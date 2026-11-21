/* 主题：白天/夜晚 + 整套配色盘，保存在 localStorage
   在 <head> 同步执行，避免加载时闪一下默认配色；设置页里点击即时切换。 */
(function () {
  var KEY = 'rl_theme';
  var MODES = { light: 1, dark: 1 };
  var ACCENTS = { gold: 1, red: 1, orange: 1, yellow: 1, green: 1, white: 1, purple: 1 };
  // 白天模式不展示金色/黄色：观感突兀。这里只改「生效色」，用户原配置保留，
  // 切回夜晚模式还是原来的金/黄，不会丢选择。
  var LIGHT_HIDE = { gold: 1, yellow: 1 };
  var state = { mode: 'dark', accent: 'gold', scale: 1 };
  function effectiveAccent() {
    return state.mode === 'light' && LIGHT_HIDE[state.accent] ? 'white' : state.accent;
  }

  try {
    var raw = localStorage.getItem(KEY);
    if (raw) {
      var o = JSON.parse(raw);
      if (o && MODES[o.mode]) state.mode = o.mode;
      if (o && ACCENTS[o.accent]) state.accent = o.accent;
      // UI 字体大小（整体缩放比例 0.85~1.5），1 = 100% 默认
      if (o && typeof o.scale === 'number' && o.scale >= 0.85 && o.scale <= 1.5) state.scale = o.scale;
    }
  } catch (e) { /* 忽略损坏的配置 */ }

  window.__rlThemeState = state;

  function apply() {
    var r = document.documentElement;
    var acc = effectiveAccent();
    if (state.mode === 'light') r.setAttribute('data-theme', 'light');
    else r.removeAttribute('data-theme');
    r.setAttribute('data-accent', acc);
    // UI 字体大小（QQ 同款）：只缩放文字、不动布局——CSS 里所有 font-size
    // 都是 calc(Npx * var(--fs-scale))，改这一个变量即可全局生效，无 zoom 撕裂
    if (Math.abs(state.scale - 1) < 0.001) r.style.removeProperty('--fs-scale');
    else r.style.setProperty('--fs-scale', String(state.scale));

    var modes = document.querySelectorAll('.mode-card');
    for (var i = 0; i < modes.length; i++) {
      if (modes[i].getAttribute('data-mode') === state.mode) modes[i].classList.add('active');
      else modes[i].classList.remove('active');
    }
    var pals = document.querySelectorAll('.palette');
    for (var j = 0; j < pals.length; j++) {
      if (pals[j].getAttribute('data-accent') === acc) pals[j].classList.add('active');
      else pals[j].classList.remove('active');
    }
    // 字体大小档位高亮 + 滑块同步（设置页每次重绘后都会走到这里）
    var fonts = document.querySelectorAll('.font-card');
    for (var k = 0; k < fonts.length; k++) {
      var sv = parseFloat(fonts[k].getAttribute('data-scale'));
      if (Math.abs(sv - state.scale) < 0.001) fonts[k].classList.add('active');
      else fonts[k].classList.remove('active');
    }
    var rng = document.getElementById('fontRange');
    if (rng) {
      // 拖动中浏览器自己控制 value，只有外部值不一致时才回写，避免打断拖拽
      if (Math.abs(parseFloat(rng.value) - state.scale) >= 0.001) rng.value = String(state.scale);
      var fv = document.getElementById('fontVal');
      if (fv) fv.textContent = Math.round(state.scale * 100) + '%';
    }
  }

  function save() {
    try { localStorage.setItem(KEY, JSON.stringify(state)); } catch (e) { /* 隐私模式 */ }
  }

  apply();

  // 外部环境若改写主题属性，立即按用户选择纠正回来
  try {
    new MutationObserver(function () {
      var r = document.documentElement;
      var isLight = r.getAttribute('data-theme') === 'light';
      if (isLight !== (state.mode === 'light') || r.getAttribute('data-accent') !== effectiveAccent()) apply();
    }).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme', 'data-accent'] });
  } catch (e) { /* 老内核不支持 */ }
  setTimeout(apply, 300);

  // 设置页每次重新渲染后，重新标出当前选中项
  document.addEventListener('DOMContentLoaded', function () {
    var view = document.getElementById('view');
    if (view && window.MutationObserver) {
      new MutationObserver(function () { apply(); })
        .observe(view, { childList: true });
    }
    apply();
  });

  // 事件委托：设置页重新渲染后按钮依然可点
  document.addEventListener('click', function (e) {
    var t = e.target;
    // 注意：字体档位按钮带 .font-card 类，判断必须在 .mode-card 之前——
    // 否则 closest('.mode-card') 先命中并 return，档位永远点不动
    var fc = t.closest ? t.closest('.font-card') : null;
    if (fc) {
      var sv = parseFloat(fc.getAttribute('data-scale'));
      if (sv >= 0.85 && sv <= 1.5) { state.scale = sv; save(); apply(); }
      return;
    }
    var modeBtn = t.closest ? t.closest('.mode-card') : null;
    if (modeBtn) {
      var m = modeBtn.getAttribute('data-mode');
      if (MODES[m]) { state.mode = m; save(); apply(); }
      return;
    }
    var pal = t.closest ? t.closest('.palette') : null;
    if (pal) {
      var a = pal.getAttribute('data-accent');
      if (ACCENTS[a]) {
        // 白天模式金/黄按钮已隐藏；万一被外部触发，也只落到中性白盘
        state.accent = (state.mode === 'light' && LIGHT_HIDE[a]) ? 'white' : a;
        save(); apply();
      }
    }
  });

  // 字体大小滑块：拖动即时生效并保存
  document.addEventListener('input', function (e) {
    if (e.target && e.target.id === 'fontRange') {
      var v = parseFloat(e.target.value);
      if (v >= 0.85 && v <= 1.5) { state.scale = v; save(); apply(); }
    }
  });
})();
