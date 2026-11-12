/**
 * 网站主题 —— 用原版 Day-night-toggle-button 组件（/vendor/theme-button.js）
 *
 * 原组件 <theme-button value="light|dark" size="3"></theme-button>
 * shadow DOM 封装，自带：非线性过冲动画、三层视差背景、云、星星、月亮。
 * 全部是原作者实现（MIT），我们只做集成。
 *
 * 本脚本只负责：
 *   ① 加载组件、放到右上角
 *   ② 组件 change 事件 → html[data-theme]，并写 localStorage
 *   ③ 页面整体主题过渡 + 全屏气氛渐变 + 去掉板块接缝
 */
(function () {
  var KEY = 'fish-theme';
  var root = document.documentElement;
  var HOST_ID = 'themeHost';
  var DUR = 720;
  var EASE = 'cubic-bezier(.4,0,.2,1)';

  /* ==================== 页面侧样式 ==================== */
  var PAGE_CSS = [
    'html:not([data-theme="light"]) body::before{',
    '  content:"";position:fixed;inset:0;pointer-events:none;z-index:0;',
    '  background:',
    '    radial-gradient(900px 700px at 8% 4%, rgba(46,96,190,.22), transparent 62%),',
    '    radial-gradient(800px 640px at 96% 8%, rgba(70,140,255,.14), transparent 60%),',
    '    radial-gradient(1000px 800px at 92% 96%, rgba(16,120,110,.16), transparent 62%),',
    '    radial-gradient(760px 620px at 4% 92%, rgba(40,70,150,.18), transparent 64%),',
    '    linear-gradient(180deg,#0b0d12 0%,#0d1016 45%,#0b0e14 100%);',
    '}',
    'html[data-theme="light"] body::before{',
    '  content:"";position:fixed;inset:0;pointer-events:none;z-index:0;',
    '  background:',
    '    radial-gradient(900px 700px at 8% 4%, rgba(180,210,255,.55), transparent 62%),',
    '    radial-gradient(800px 640px at 96% 8%, rgba(200,225,255,.42), transparent 60%),',
    '    radial-gradient(1000px 800px at 92% 96%, rgba(190,240,232,.45), transparent 62%),',
    '    radial-gradient(760px 620px at 4% 92%, rgba(205,220,255,.40), transparent 64%),',
    '    linear-gradient(180deg,#fbfcff 0%,#f7f9fd 45%,#f6f8fc 100%);',
    '}',
    /* 内容压在渐变之上；但 nav 必须排除 —— 否则它的 position:sticky 会被 relative 覆盖 */
    'html body > *:not(#' + HOST_ID + '):not(nav){position:relative;z-index:1}',

    /* 导航置顶：页面 body 有 overflow-x:hidden，sticky 会失效，所以直接用 fixed，
       再用 body padding-top 把内容顶下来（2026-10-23 踩到） */
    'html nav{position:fixed !important;top:0 !important;left:0 !important;right:0 !important;',
    '  z-index:80 !important}',
    'html body{padding-top:64px}',
    '@media (max-width:760px){html body{padding-top:56px}}',

    /* 主题按钮定位（实测校准）：
       原组件 .container 居中于宿主原点，宿主 0×0，
       所以 right/top 指的是「按钮中心」相对视口右边/顶边 的距离。
       scale(.55) 后按钮 99×38.5 → 半宽 49.5、半高 19.25
         右边缘留 18px → 中心距右 = 18 + 49.5 ≈ 68  (实测再补偿 16 → 51.5)
         垂直居中于 64px 导航 → 中心距顶 = 32  (实测补偿 -3.25 → 28.75) */
    '#' + HOST_ID + '{',
    '  position:fixed;right:51.5px;top:28.75px;width:0;height:0;',
    '  z-index:90;transform:scale(.55);transform-origin:center center;',
    '  filter:drop-shadow(0 3px 10px rgba(10,18,34,.20));',
    '}',
    '@media (max-width:760px){',
    '  #' + HOST_ID + '{right:44px;top:24px;transform:scale(.46)}',
    '}',

    '.hr{display:none !important}',
    'section, .hero{background:transparent !important}',
    'section + section, .hero + section{border-top:0 !important}',

    /* 干掉分界线的根因：hero 里两个大光斑 + .hero{overflow:hidden}
       光斑被裁在 hero 边界上，就成了横切一刀的色差。
       整页气氛渐变已由 body::before 负责，这里直接隐藏光斑。 */
    '.blob{display:none !important}',
    '.hero{overflow:visible !important}',
    /* 任何 section 的分隔线/阴影也一并去掉 */
    'section{border:0 !important;box-shadow:none !important}',
    '.hero + section, section + section{margin-top:0 !important}',

    'html[data-theme="light"] .ex .box,',
    'html[data-theme="light"] .tag{border-color:rgba(150,170,200,.22) !important}',


  ].join('\n');

  function injectStyle() {
    var old = document.getElementById('fish-theme-style');
    if (old) { old.textContent = PAGE_CSS; return; }
    var st = document.createElement('style');
    st.id = 'fish-theme-style';
    st.textContent = PAGE_CSS;
    (document.head || root).appendChild(st);
  }

  /* ==================== 主题读写 ==================== */
  function saved() { try { return localStorage.getItem(KEY); } catch (e) { return null; } }
  function save(v) { try { localStorage.setItem(KEY, v); } catch (e) { } }
  function system() {
    try {
      return (window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches)
        ? 'light' : 'dark';
    } catch (e) { return 'dark'; }
  }
  function wantAnim() {
    try {
      var q = new URLSearchParams(location.search).get('anim');
      if (q === '0') return false;
      if (q === '1') return true;
    } catch (e) { }
    return true;
  }

  var cur = saved() || system();
  root.setAttribute('data-theme', cur);

  /* ==================== 页面过渡 ==================== */
  var PROPS = ['backgroundColor', 'color', 'borderTopColor', 'borderRightColor',
               'borderBottomColor', 'borderLeftColor', 'outlineColor', 'fill', 'stroke'];

  function collect() {
    var all = document.querySelectorAll('body,div,section,article,aside,nav,header,footer,' +
      'li,td,th,p,span,a,h1,h2,h3,h4,button,input,textarea,code,b,strong,small');
    var picked = [];
    for (var i = 0; i < all.length && picked.length < 420; i++) {
      var el = all[i];
      if (el.id === HOST_ID || (el.closest && el.closest('#' + HOST_ID))) continue;
      picked.push(el);
    }
    if (picked.indexOf(document.body) < 0) picked.unshift(document.body);
    if (picked.indexOf(root) < 0) picked.unshift(root);
    return picked;
  }

  function readColors(els) {
    var out = [];
    for (var i = 0; i < els.length; i++) {
      var cs = getComputedStyle(els[i]);
      var rec = { el: els[i], v: {} };
      for (var j = 0; j < PROPS.length; j++) rec.v[PROPS[j]] = cs[PROPS[j]] || '';
      out.push(rec);
    }
    return out;
  }

  function animateFrom(before) {
    var anims = [];
    for (var i = 0; i < before.length; i++) {
      var rec = before[i], el = rec.el, cs;
      try { cs = getComputedStyle(el); } catch (e) { continue; }
      var kf = {}, has = false;
      for (var p in rec.v) {
        var a = rec.v[p], b = cs[p];
        if (!a || !b || a === b) continue;
        if (a === 'rgba(0, 0, 0, 0)' && b === 'rgba(0, 0, 0, 0)') continue;
        kf[p] = [a, b]; has = true;
      }
      if (!has) continue;
      try { anims.push(el.animate(kf, { duration: DUR, easing: EASE, fill: 'none' })); }
      catch (e) { }
    }
    return anims;
  }

  var busy = false;

  function applyTheme(v) {
    if (v === cur || busy) return;
    var prev = cur;
    function done() {
      busy = false;
      try {
        window.dispatchEvent(new CustomEvent('themechange', { detail: { theme: cur, from: prev } }));
      } catch (e) { }
    }
    var anim = wantAnim();
    var before = anim ? readColors(collect()) : null;

    cur = v;
    root.setAttribute('data-theme', v);
    save(v);

    if (!anim) { done(); return; }
    busy = true;
    requestAnimationFrame(function () {
      var anims = [];
      try { anims = animateFrom(before); } catch (e) { }
      if (!anims.length) { busy = false; done(); return; }
      var left = anims.length;
      var finish = function () { if (--left <= 0) done(); };
      anims.forEach(function (a) {
        try { a.finished.then(finish).catch(finish); } catch (e) { finish(); }
      });
      setTimeout(function () { if (busy) done(); }, DUR + 300);
    });
  }

  /* ==================== 挂原版组件 ==================== */
  function mount() {
    injectStyle();
    if (document.getElementById(HOST_ID)) return;

    var host = document.createElement('div');
    host.id = HOST_ID;
    document.body.appendChild(host);

    function load() {
      var btn = document.createElement('theme-button');
      btn.setAttribute('value', cur);
      btn.setAttribute('size', '3');
      btn.addEventListener('change', function (e) {
        var next = e.detail === 'dark' ? 'dark' : 'light';
        if (next !== cur) applyTheme(next);
      });
      host.appendChild(btn);
    }

    if (window.customElements && customElements.get('theme-button')) {
      load();
    } else {
      var s = document.createElement('script');
      s.src = '/vendor/theme-button.js';
      s.onload = load;
      s.onerror = function () {
        var b = document.createElement('button');
        b.textContent = cur === 'light' ? '☀' : '☾';
        b.style.cssText = 'width:47px;height:47px;border-radius:50%;border:1px solid var(--line);' +
          'background:var(--card);color:var(--txt);font-size:19px;cursor:pointer';
        b.onclick = function () { applyTheme(cur === 'light' ? 'dark' : 'light'); };
        host.appendChild(b);
      };
      document.head.appendChild(s);
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', mount);
  } else {
    mount();
  }

  try {
    var mq = window.matchMedia('(prefers-color-scheme: light)');
    var onChange = function (e) { if (!saved()) applyTheme(e.matches ? 'light' : 'dark'); };
    if (mq.addEventListener) mq.addEventListener('change', onChange);
    else if (mq.addListener) mq.addListener(onChange);
  } catch (e) { }

  window.FishTheme = { get: function () { return cur; }, set: applyTheme };
})();
