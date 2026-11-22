/* =============================================================================
   SAM Modern — 前端逻辑（纯原生 JS，无框架、无构建）
   协议来源：modern/BRIDGE.md
   ============================================================================= */
(function () {
  'use strict';

  /* ---------------------------------------------------------------------------
     1. 常量与图标（全部内联 SVG，不使用 emoji / 外链）
     --------------------------------------------------------------------------- */

  var ICON = {
    info: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><circle cx="8" cy="8" r="6"/><path d="M8 7.3v3.4M8 5.2v.1"/></svg>',
    success: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3.4 8.4 6.5 11.4 12.6 4.8"/></svg>',
    error: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"><path d="M5.2 5.2 10.8 10.8M10.8 5.2 5.2 10.8"/></svg>',
    warn: '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M8 2.6 14.2 13H1.8L8 2.6Z"/><path d="M8 6.6v2.9M8 11.3v.1"/></svg>'
  };

  var SVG_CHECK = '<svg viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M2.4 6.3 4.9 8.8 9.6 3.4"/></svg>';
  var SVG_CLOSE = '<svg viewBox="0 0 12 12" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"><path d="M3.2 3.2 8.8 8.8M8.8 3.2 3.2 8.8"/></svg>';
  var SVG_CLOCK = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"><circle cx="8" cy="8" r="5.6"/><path d="M8 5v3.2l2.1 1.3"/></svg>';
  var SVG_RARE = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M8 2.4 13.6 8 8 13.6 2.4 8Z"/></svg>';
  var SVG_LOCK = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="3.4" y="7" width="9.2" height="6.4" rx="1.7"/><path d="M5.7 7V5.3a2.3 2.3 0 0 1 4.6 0V7"/></svg>';

  var TYPE_LABEL = { normal: '正式版', demo: '试玩', mod: '模组', junk: '其他' };

  // 稀有成就：全服解锁率低于阈值。数据来自 Steam 的 GetAchievementAchievedPercent。
  var RARE_PCT = 5;
  // 稀有度档位：pct=0 表示不按稀有度过滤
  var RARE_OPTIONS = [
    { label: '全部成就', pct: 0 },
    { label: '稀有 < 1%', pct: 1 },
    { label: '稀有 < 5%', pct: 5 },
    { label: '稀有 < 10%', pct: 10 }
  ];

  /* ---------------------------------------------------------------------------
     2. 单一数据源
     --------------------------------------------------------------------------- */

  var state = {
    mode: 'picker',
    mock: false,
    booted: false,
    meta: { appId: 0, gameName: '', steamId: '', userName: '', language: 'zh-CN' },
    status: { text: '正在初始化…', busy: false },
    picker: { games: [], total: 0, query: '', type: 'all', loading: true, scanning: 0 },
    game: {
      appId: 0, name: '', capsule: null, capsules: null,
      achievements: [], stats: [],
      summary: { total: 0, unlocked: 0 },
      unlock: { active: false, done: 0, total: 0, next: 0 },
      loading: false
    },
    ui: { tab: 'achievements', query: '', filter: 'all' },
    pending: { ach: {}, stats: {} },
    saving: false,
    schedule: { jobs: [], loaded: false }
  };

  // 打开过的游戏记下「已解锁 / 总数」，卡片第二行才有东西可显示。
  // 游戏库和游戏页是两个进程，但共用同一个 WebView2 用户目录和 https://sam.ui 源，
  // 所以 localStorage 是跨进程共享的 —— 这是唯一不用改主机代码就能把进度带回游戏库的办法。
  var PROGRESS_KEY = 'sam.progress.v1';
  var sessionProgress = {};

  function loadProgress() {
    var next = {};
    try {
      var raw = window.localStorage ? window.localStorage.getItem(PROGRESS_KEY) : null;
      if (raw) {
        var parsed = JSON.parse(raw);
        if (parsed && typeof parsed === 'object') next = parsed;
      }
    } catch (err) {
      next = {};
    }
    var before = JSON.stringify(sessionProgress);
    var after = JSON.stringify(next);
    sessionProgress = next;
    return before !== after;
  }

  function saveProgress() {
    try {
      if (window.localStorage) window.localStorage.setItem(PROGRESS_KEY, JSON.stringify(sessionProgress));
    } catch (err) { }
  }

  function recordProgress(appId, total, unlocked) {
    var key = String(appId);
    if (!key || key === '0' || !(total > 0)) return;
    var prev = sessionProgress[key];
    if (prev && prev.total === total && prev.unlocked === unlocked) return;
    sessionProgress[key] = { total: total, unlocked: unlocked };
    saveProgress();
  }

  var els = {};
  var modalState = null;
  var saveGuard = 0;
  var handshakeSeen = false;
  var paintedSent = false;

  /* ---------------------------------------------------------------------------
     3. 通用工具
     --------------------------------------------------------------------------- */

  function esc(value) {
    return String(value === null || value === undefined ? '' : value)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function setHidden(el, hide) {
    if (!el) return;
    el.hidden = !!hide;
    el.style.display = hide ? 'none' : '';
  }

  function num(value, fallback) {
    var n = Number(value);
    return isFinite(n) ? n : (fallback || 0);
  }

  function pad2(n) {
    return n < 10 ? '0' + n : String(n);
  }

  // 输入框逐字触发整表重绘，代价很高；统一走 120ms 防抖，回车/清空仍然立即生效。
  function debounce(fn, wait) {
    var timer = 0;
    var wrapped = function () {
      var args = arguments, self = this;
      if (timer) clearTimeout(timer);
      timer = setTimeout(function () {
        timer = 0;
        fn.apply(self, args);
      }, wait || 120);
    };
    wrapped.cancel = function () {
      if (timer) { clearTimeout(timer); timer = 0; }
    };
    return wrapped;
  }

  function fmtTime(ts) {
    var sec = Number(ts);
    if (!sec || !isFinite(sec) || sec <= 0) return '';
    var d = new Date(sec * 1000);
    return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) +
      ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes());
  }

  // 相对时间只给左栏「最近解锁」用：绝对时间戳在窄栏里又长又难读。
  function fmtAgo(ts) {
    var sec = Number(ts);
    if (!sec || !isFinite(sec) || sec <= 0) return '';
    var diff = Math.floor(Date.now() / 1000 - sec);
    if (diff < 0) diff = 0;
    if (diff < 60) return '刚刚';
    if (diff < 3600) return Math.floor(diff / 60) + ' 分钟前';
    if (diff < 86400) return Math.floor(diff / 3600) + ' 小时前';
    if (diff < 86400 * 30) return Math.floor(diff / 86400) + ' 天前';
    return fmtTime(sec).slice(0, 10);
  }

  function fmtNumber(value, isFloat) {
    var n = Number(value);
    if (!isFinite(n)) return '0';
    if (!isFloat) return String(Math.round(n));
    var rounded = Math.round(n * 100) / 100;
    return String(rounded);
  }

  function monogram(name) {
    var s = String(name === null || name === undefined ? '' : name).trim();
    if (!s) return '?';
    if (/^[A-Za-z0-9]/.test(s)) {
      var words = s.split(/\s+/).filter(function (w) { return w.length > 0; });
      if (words.length > 1) {
        return (words[0].charAt(0) + words[1].charAt(0)).toUpperCase();
      }
      return s.slice(0, 2).toUpperCase();
    }
    return s.slice(0, 2);
  }

  function hash32(str) {
    var h = 2166136261;
    var s = String(str);
    for (var i = 0; i < s.length; i++) {
      h ^= s.charCodeAt(i);
      h = Math.imul(h, 16777619) >>> 0;
    }
    return h >>> 0;
  }

  function makeRng(seed) {
    var s = (seed >>> 0) || 1;
    return function () {
      s ^= s << 13; s >>>= 0;
      s ^= s >>> 17;
      s ^= s << 5; s >>>= 0;
      return s / 4294967296;
    };
  }

  // 占位图标/封面上的缩写：单色，不用渐变（渐变文字既装饰又降低对比度）
  function monoStyle(size) {
    return 'font-size:' + size + 'px;font-weight:600;letter-spacing:.02em;line-height:1;color:var(--text-3);';
  }

  function fallbackIcon(text) {
    return '<span class="aicon-fallback"><span style="' + monoStyle(21) + '">' + esc(text) + '</span></span>';
  }

  function fallbackCover(text, hue) {
    var style = 'filter:hue-rotate(' + (hue || 0) + 'deg);';
    return '<div class="gcover-fallback" style="' + style + '">' +
      '<span style="' + monoStyle(44) + '">' + esc(text) + '</span></div>';
  }

  function own(obj, key) {
    return Object.prototype.hasOwnProperty.call(obj, key);
  }

  /* ---------------------------------------------------------------------------
     4. 与 C# 主机的桥接
     --------------------------------------------------------------------------- */

  function getBridge() {
    if (typeof window.chrome === 'object' && window.chrome && window.chrome.webview) {
      return window.chrome.webview;
    }
    return null;
  }

  function hasBridge() {
    var b = getBridge();
    return !!(b && typeof b.postMessage === 'function');
  }

  function send(cmd, payload) {
    if (!hasBridge()) return false;
    var message = { cmd: cmd };
    if (payload) {
      for (var k in payload) {
        if (own(payload, k)) message[k] = payload[k];
      }
    }
    try {
      getBridge().postMessage(message);
      return true;
    } catch (err) {
      console.warn('[SAM] 发送命令失败：' + cmd, err);
      return false;
    }
  }

  /* ---------------------------------------------------------------------------
     5. 事件处理（C# → JS）
     --------------------------------------------------------------------------- */

  function handleEvent(ev) {
    if (!ev || typeof ev !== 'object') return;
    var name = ev.ev;
    if (typeof name !== 'string') return;

    switch (name) {
      case 'ready':
        onReady(ev);
        break;
      case 'status':
        state.status.text = ev.text ? String(ev.text) : '';
        state.status.busy = !!ev.busy;
        renderStatus();
        break;
      case 'toast':
        toast(ev.kind || 'info', ev.title || '', ev.text || '');
        break;
      case 'error':
        openModal({
          title: ev.title || '发生错误',
          text: ev.text || '主机返回了未描述的错误。',
          danger: false,
          confirmText: '我知道了',
          showCancel: false
        });
        break;
      case 'games':
        onGames(ev);
        break;
      case 'gamesProgress':
        onGamesProgress(ev);
        break;
      case 'game':
        onGame(ev);
        break;
      case 'stored':
        onStored(ev);
        break;
      case 'unlockRun':
        onUnlockRun(ev);
        break;
      case 'scheduleList':
        onScheduleList(ev);
        break;
      case 'scheduleStarted':
        onScheduleEvent(ev, 'started');
        break;
      case 'scheduleDone':
        onScheduleEvent(ev, 'done');
        break;
      case 'scheduleMissed':
        onScheduleEvent(ev, 'missed');
        break;
      case 'mock':
        if (!state.mock) enterMock();
        break;
      case 'window':
        applyWindowState(!!ev.maximized);
        break;
      default:
        console.warn('[SAM] 未识别的事件：' + name, ev);
    }
  }

  function onReady(ev) {
    readyAt = (window.performance && performance.now) ? performance.now() : Date.now();
    handshakeSeen = true;
    state.booted = true;
    state.meta.appId = num(ev.appId, 0);
    state.meta.gameName = ev.gameName ? String(ev.gameName) : '';
    state.meta.steamId = ev.steamId ? String(ev.steamId) : '';
    state.meta.userName = ev.userName ? String(ev.userName) : '';
    state.meta.language = ev.language ? String(ev.language) : 'zh-CN';
    state.mode = ev.mode === 'game' ? 'game' : 'picker';

    if (state.mode === 'game') {
      state.game.appId = state.meta.appId;
      state.game.name = state.meta.gameName;
      // 主机把上次的快照塞在 ready 里一起发过来：直接一次画完整界面。
      // 若分两步（先 ready 后 game），第一步会渲染出「0 个成就 / 转圈」的空骨架，
      // 第二步才填内容 —— 切换时看到的那一两帧闪烁就是这么来的。
      if (ev.cachedGame && typeof ev.cachedGame === 'object') {
        ev.cachedGame.cached = true;
        onGame(ev.cachedGame);
        announcePainted();
        return;
      }
      state.game.loading = true;
      state.status.text = '正在读取成就与统计数据…';
      state.status.busy = true;
    } else {
      if (Array.isArray(ev.cachedGames) && ev.cachedGames.length > 0) {
        onGames({ games: ev.cachedGames, cached: true, total: ev.cachedGames.length });
        announcePainted();
        return;
      }
      state.picker.loading = true;
      state.status.text = '正在扫描游戏库…';
      state.status.busy = true;
    }
    render();
    announcePainted();
  }

  // 首屏已经画完（含布局、图片解码与首次绘制）再通知主机亮窗口。
  // 主机收到 ui/painted 才把窗口从屏幕外挪进来：这样旧窗口会一直停在
  // 「正确的上一页」，直到新窗口内容已就绪才交接，中间不会露出空壳。
  var readyAt = 0;
  var paintProbe = { layoutMs: 0, imageMs: 0 };

  function announcePainted() {
    if (paintedSent) return;
    paintedSent = true;
    var t0 = readyAt || 0;
    afterPaint(function () {
      paintProbe.layoutMs = Math.round(((window.performance && performance.now) ? performance.now() : Date.now()) - t0);
      var t1 = (window.performance && performance.now) ? performance.now() : Date.now();
      waitVisibleImages(function () {
        paintProbe.imageMs = Math.round(((window.performance && performance.now) ? performance.now() : Date.now()) - t1);
        afterPaint(function () {
          send('ui/painted', { layoutMs: paintProbe.layoutMs, imageMs: paintProbe.imageMs });
        });
      });
    });
  }

  // 两次 rAF：第一次回调在样式与布局之后，第二次回调之后浏览器才真正绘制。
  function afterPaint(fn) {
    var raf = window.requestAnimationFrame;
    if (typeof raf !== 'function') {
      fn();
      return;
    }
    raf(function () {
      raf(fn);
    });
  }

  // 成就图标 / 封面是 WebView2 异步解码的：DOM 已经就位、文字已经排版，
  // 但位图还没到，这时窗口亮起来就是一屏灰色占位块，再等 100~200ms 才
  // 「啪」地变成图标。所以亮窗口之前先等视口附近的图片解码完。
  // 只等视口附近的：下面几百个成就还在 lazy 队列里，等它们纯属白等；
  // 并且最多等 IMAGE_WAIT_MS，绝不把窗口无限期留在屏幕外。
  var IMAGE_WAIT_MS = 400;

  function waitVisibleImages(done) {
    var all = document.querySelectorAll('img');
    var pending = [];
    var limit = (window.innerHeight || 800) + 240;
    for (var i = 0; i < all.length; i++) {
      var img = all[i];
      if (img.complete) continue;
      // display:none 的容器（当前没在显示的另一个视图）里图片没有布局盒，
      // 不能算「可见」，否则会白等它们到超时。
      if (!img.getClientRects().length) continue;
      var rect = img.getBoundingClientRect();
      if (rect.bottom < -240 || rect.top > limit) continue;
      pending.push(img);
    }
    if (pending.length === 0) {
      done();
      return;
    }

    var remaining = pending.length;
    var finished = false;
    var timer = setTimeout(flush, IMAGE_WAIT_MS);

    function flush() {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      for (var k = 0; k < pending.length; k++) {
        pending[k].removeEventListener('load', one);
        pending[k].removeEventListener('error', one);
      }
      done();
    }

    function one() {
      remaining--;
      if (remaining <= 0) flush();
    }

    for (var j = 0; j < pending.length; j++) {
      if (pending[j].complete) {
        remaining--;
        continue;
      }
      pending[j].addEventListener('load', one);
      pending[j].addEventListener('error', one);
    }
    if (remaining <= 0) flush();
  }

  function onGames(ev) {
    var list = Array.isArray(ev.games) ? ev.games : [];
    state.picker.games = list.map(function (g) {
      return {
        id: g && g.id !== undefined ? g.id : 0,
        name: g && g.name ? String(g.name) : '未命名游戏',
        type: g && TYPE_LABEL[g.type] ? g.type : 'junk',
        capsule: g && g.capsule ? String(g.capsule) : null,
        capsules: g && Array.isArray(g.capsules) ? g.capsules.map(String) : null
      };
    });
    state.picker.total = typeof ev.total === 'number' ? ev.total : state.picker.games.length;
    var cached = !!ev.cached;
    state.picker.loading = false;
    state.status.busy = cached;
    state.status.text = cached
      ? '正在刷新游戏库…'
      : '扫描完成，共找到 ' + state.picker.total + ' 个游戏';
    if (state.mode !== 'picker') {
      state.mode = 'picker';
    }
    render();
  }

  function onGamesProgress(ev) {
    state.picker.scanning = num(ev.scanning, 0);
    state.picker.loading = true;
    state.status.text = '正在扫描游戏库… 已发现 ' + state.picker.scanning + ' 个';
    state.status.busy = true;
    renderStatus();
  }

  function normalizeAchievement(a) {
    var id = a && a.id !== undefined && a.id !== null ? String(a.id) : '';
    return {
      id: id,
      name: a && a.name ? String(a.name) : id,
      desc: a && a.desc ? String(a.desc) : '',
      icon: a && a.icon ? String(a.icon) : null,
      iconLocked: a && a.iconLocked ? String(a.iconLocked) : null,
      achieved: !!(a && a.achieved),
      unlockTime: a && typeof a.unlockTime === 'number' && a.unlockTime > 0 ? a.unlockTime : null,
      hidden: !!(a && a.hidden),
      protect: !!(a && a.protect),
      rarity: a && typeof a.rarity === 'number' && a.rarity >= 0 ? a.rarity : -1
    };
  }

  function normalizeStat(s) {
    var min = num(s && s.min, 0);
    var max = num(s && s.max, 0);
    if (max <= min) max = Math.max(min + 1, 1);
    var id = s && s.id !== undefined && s.id !== null ? String(s.id) : '';
    return {
      id: id,
      name: s && s.name ? String(s.name) : id,
      value: num(s && s.value, min),
      isFloat: !!(s && s.isFloat),
      min: min,
      max: max,
      incrementOnly: !!(s && s.incrementOnly),
      protect: !!(s && s.protect)
    };
  }

  function onGame(ev) {
    var previousAppId = state.game.appId;
    var cached = !!ev.cached;
    state.booted = true;
    state.mode = 'game';
    state.game.appId = num(ev.appId, state.meta.appId);
    if (previousAppId !== state.game.appId) {
      state.game.unlock = { active: false, done: 0, total: 0, next: 0 };
    }
    state.game.name = ev.name ? String(ev.name) : (state.meta.gameName || '未知游戏');
    if (ev.capsule) state.game.capsule = String(ev.capsule);
    else if (!state.game.capsule) state.game.capsule = capsuleForAppId(state.game.appId);
    state.game.capsules = Array.isArray(ev.capsules) ? ev.capsules.map(String) : null;
    state.game.achievements = (Array.isArray(ev.achievements) ? ev.achievements : []).map(normalizeAchievement);
    state.game.stats = (Array.isArray(ev.stats) ? ev.stats : []).map(normalizeStat);
    state.game.summary = {
      total: ev.summary && typeof ev.summary.total === 'number' ? ev.summary.total : state.game.achievements.length,
      unlocked: ev.summary && typeof ev.summary.unlocked === 'number'
        ? ev.summary.unlocked
        : countUnlocked(state.game.achievements)
    };
    recordProgress(state.game.appId, state.game.summary.total, state.game.summary.unlocked);
    // 缓存快照只是先垫一层内容，Steam 数据马上会再推一次并整表覆盖。
    // 这里必须把 loading 关掉，否则 renderGame 只画转圈、不画列表，缓存就白推了。
    state.game.loading = false;
    state.pending = { ach: {}, stats: {} };
    state.saving = false;
    window.clearTimeout(saveGuard);
    state.status.busy = cached;
    state.status.text = cached
      ? '正在载入《' + state.game.name + '》…'
      : '已载入《' + state.game.name + '》：' +
        state.game.achievements.length + ' 个成就、' + state.game.stats.length + ' 项统计';
    render();
  }

  function onUnlockRun(ev) {
    if (ev && ev.active) {
      state.game.unlock = {
        active: true,
        done: num(ev.done, 0),
        total: num(ev.total, 0),
        next: num(ev.next, 0)
      };
      if (ev.id) {
        var a = findAch(ev.id);
        if (a) {
          a.achieved = true;
          if (typeof ev.unlockTime === 'number' && ev.unlockTime > 0) a.unlockTime = ev.unlockTime;
          delete state.pending.ach[a.id];
        }
      }
      state.status.text = '随机解锁进行中：' + state.game.unlock.done + ' / ' + state.game.unlock.total +
        (state.game.unlock.next > 0 ? '，下一个约 ' + state.game.unlock.next + ' 秒后解锁。' : '。');
      state.status.busy = true;
      renderGame();
      renderStatus();
      return;
    }

    state.game.unlock = {
      active: false,
      done: num(ev && ev.done, 0),
      total: num(ev && ev.total, 0),
      next: 0
    };
    state.status.busy = false;
    if (ev && ev.cancelled) {
      state.status.text = '已停止随机解锁：本次共解锁 ' + state.game.unlock.done + ' 个成就。';
    } else if (ev && ev.finished) {
      state.status.text = '随机解锁完成：本次共解锁 ' + state.game.unlock.done + ' 个成就。';
    }
    renderGame();
    renderStatus();
  }

  function renderUnlockBar() {
    if (!els.unlockBar) return;
    var u = state.game.unlock || { active: false, done: 0, total: 0, next: 0 };
    var show = state.mode === 'game' && u.active;
    setHidden(els.unlockBar, !show);
    if (els.btnRandom) els.btnRandom.disabled = !!show;
    if (!show) return;

    var pct = u.total > 0 ? (u.done / u.total) * 100 : 0;
    if (els.unlockBarFill) els.unlockBarFill.style.width = Math.max(0, Math.min(100, pct)) + '%';
    if (els.unlockBarText) {
      var text = '随机解锁中：' + u.done + ' / ' + u.total + ' 个成就';
      if (u.next > 0) text += '，下一个约 ' + fmtGap(u.next) + '后';
      els.unlockBarText.textContent = text;
    }
  }

  function onStored(ev) {
    window.clearTimeout(saveGuard);
    state.saving = false;
    state.pending = { ach: {}, stats: {} };
    render();
  }

  function countUnlocked(list) {
    var n = 0;
    for (var i = 0; i < list.length; i++) {
      if (list[i].achieved) n++;
    }
    return n;
  }

  /* ---------------------------------------------------------------------------
     6. 读取期望值（本地待保存改动优先）
     --------------------------------------------------------------------------- */

  function desiredAch(a) {
    return own(state.pending.ach, a.id) ? !!state.pending.ach[a.id] : !!a.achieved;
  }

  function desiredStat(s) {
    return own(state.pending.stats, s.id) ? Number(state.pending.stats[s.id]) : s.value;
  }

  function findAch(id) {
    var key = String(id);
    var list = state.game.achievements;
    for (var i = 0; i < list.length; i++) {
      if (list[i].id === key) return list[i];
    }
    return null;
  }

  function findStat(id) {
    var key = String(id);
    var list = state.game.stats;
    for (var i = 0; i < list.length; i++) {
      if (list[i].id === key) return list[i];
    }
    return null;
  }

  function progressInfo() {
    var list = state.game.achievements;
    var unlocked = 0;
    for (var i = 0; i < list.length; i++) {
      if (desiredAch(list[i])) unlocked++;
    }
    return { unlocked: unlocked, total: list.length };
  }

  function dirtyCount() {
    var n = 0;
    var i;
    var list = state.game.achievements;
    for (i = 0; i < list.length; i++) {
      if (own(state.pending.ach, list[i].id) && !!state.pending.ach[list[i].id] !== !!list[i].achieved) n++;
    }
    var stats = state.game.stats;
    for (i = 0; i < stats.length; i++) {
      if (own(state.pending.stats, stats[i].id) && Number(state.pending.stats[stats[i].id]) !== stats[i].value) n++;
    }
    return n;
  }

  function collectChanges() {
    var achievements = {};
    var stats = {};
    var i;
    var list = state.game.achievements;
    for (i = 0; i < list.length; i++) {
      var a = list[i];
      if (own(state.pending.ach, a.id)) {
        var want = !!state.pending.ach[a.id];
        if (want !== !!a.achieved) achievements[a.id] = want;
      }
    }
    var statList = state.game.stats;
    for (i = 0; i < statList.length; i++) {
      var s = statList[i];
      if (own(state.pending.stats, s.id)) {
        var v = Number(state.pending.stats[s.id]);
        if (isFinite(v) && v !== s.value) stats[s.id] = v;
      }
    }
    return { achievements: achievements, stats: stats };
  }

  /* ---------------------------------------------------------------------------
     7. 渲染
     --------------------------------------------------------------------------- */

  function render() {
    document.body.setAttribute('data-mode', state.mode);
    renderTitlebar();
    renderToolbar();
    if (state.mode === 'picker') {
      setHidden(els.viewPicker, false);
      setHidden(els.viewGame, true);
      renderPicker();
    } else {
      setHidden(els.viewPicker, true);
      setHidden(els.viewGame, false);
      renderGame();
    }
    renderStatus();
    window.requestAnimationFrame(markLoadedImages);
  }

  function renderTitlebar() {
    if (state.mode === 'picker') {
      els.brandSub.textContent = state.picker.total > 0
        ? '共 ' + state.picker.total + ' 个游戏 · 选择要管理的游戏'
        : '成就与统计编辑工具';
    } else {
      els.brandSub.textContent = state.game.name ? state.game.name : '成就与统计编辑工具';
      els.brandSub.title = state.game.name || '';
    }
    setHidden(els.mockChip, !state.mock);
    if (state.mock) {
      els.mockChip.title = '当前为演示数据，未连接 Steam';
    }
    var who = state.meta.userName;
    if (who) {
      els.userChip.textContent = who;
      els.userChip.title = state.meta.steamId ? 'SteamID：' + state.meta.steamId : who;
      setHidden(els.userChip, false);
    } else {
      setHidden(els.userChip, true);
    }
    els.brandMark.title = state.mode === 'game'
      ? '返回游戏库（Esc 也可返回）'
      : (state.mock ? '返回游戏列表（演示模式）' : 'Steam 成就管理器');
    renderScheduleChip();
  }

  function setActive(container, key, value) {
    if (!container) return;
    var items = container.querySelectorAll('.seg__item');
    for (var i = 0; i < items.length; i++) {
      var attr = items[i].getAttribute(key);
      var on = attr === value;
      if (on) items[i].classList.add('is-active');
      else items[i].classList.remove('is-active');
      items[i].setAttribute('aria-selected', on ? 'true' : 'false');
    }
  }

  function renderToolbar() {
    setHidden(els.tbPicker, state.mode !== 'picker');
    setHidden(els.tbGame, state.mode !== 'game');
    setActive(els.pickerTypeSeg, 'data-type', state.picker.type);
    setActive(els.achFilterSeg, 'data-filter', state.ui.filter);
    setActive(els.gameTabs, 'data-tab', state.ui.tab);

    var onAchTab = state.ui.tab === 'achievements';
    setHidden(els.achSearchWrap, !onAchTab);
    setHidden(els.achFilterSeg, !onAchTab);
    setHidden(els.achActions, !onAchTab);

    renderSaveButton();
  }

  function renderSaveButton() {
    var dirty = dirtyCount();
    // 徽标常驻：0 也要显示，否则用户无法一眼确认「现在确实没有未保存改动」。
    // 0 用弱化色，和「有改动」的强调色区分开。
    setHidden(els.saveBadge, false);
    els.saveBadge.textContent = String(dirty);
    if (dirty === 0) els.saveBadge.classList.add('is-idle');
    else els.saveBadge.classList.remove('is-idle');
    setHidden(els.saveSpinner, !state.saving);
    els.saveLabel.textContent = state.saving ? '保存中…' : '保存改动';
    els.btnSave.disabled = !state.saving && dirty === 0;
    if (state.saving) els.btnSave.classList.add('is-busy');
    else els.btnSave.classList.remove('is-busy');
    els.btnSave.title = dirty > 0
      ? '保存 ' + dirty + ' 处改动（Ctrl+S）'
      : '暂无可保存的改动';
  }

  function renderStatus() {
    els.statusText.textContent = state.status.text || '';
    els.statusText.title = state.status.text || '';
    setHidden(els.statusSpinner, !state.status.busy);

    var parts = [];
    if (state.mode === 'picker') {
      var shown = filteredGames().length;
      parts.push('共 <b>' + state.picker.total + '</b> 个游戏');
      if (shown !== state.picker.total) parts.push('筛选出 <b>' + shown + '</b> 个');
    } else {
      var p = progressInfo();
      parts.push('成就 <b>' + p.unlocked + '</b> / <b>' + p.total + '</b>');
      parts.push('统计 <b>' + state.game.stats.length + '</b> 项');
      var d = dirtyCount();
      if (d > 0) parts.push('待保存 <b>' + d + '</b>');
    }
    var sj = state.schedule.jobs || [];
    if (sj.length > 0) parts.push('定时 <b>' + sj.length + '</b> 个');
    if (state.mock) parts.push('演示模式');
    els.statusMeta.innerHTML = parts.map(function (t) {
      return '<span>' + t + '</span>';
    }).join('');
  }

  /* ---------- Picker ---------- */

  function filteredGames() {
    var q = state.picker.query.trim().toLowerCase();
    var type = state.picker.type;
    return state.picker.games.filter(function (g) {
      if (type !== 'all' && g.type !== type) return false;
      if (!q) return true;
      if (String(g.name).toLowerCase().indexOf(q) >= 0) return true;
      return String(g.id).indexOf(q) >= 0;
    });
  }

  /* 把候选封面串成一个属性，图片加载失败时按顺序换下一个。 */
  function capList(g) {
    var list = [];
    if (g && Array.isArray(g.capsules)) {
      for (var i = 0; i < g.capsules.length; i++) {
        var u = g.capsules[i];
        if (u && list.indexOf(u) < 0) list.push(String(u));
      }
    }
    if (g && g.capsule && list.indexOf(String(g.capsule)) < 0) list.unshift(String(g.capsule));
    return list;
  }

  function coverImgHtml(g) {
    var list = capList(g);
    if (list.length === 0) return '';
    return '<img src="' + esc(list[0]) + '" data-caps="' + esc(list.join('|')) +
      '" data-caps-idx="0" alt="" decoding="async"' + (arguments.length > 1 && arguments[1] ? ' loading="lazy"' : '') + ' />';
  }

  function typeTagHtml(type) {
    var key = TYPE_LABEL[type] ? type : 'junk';
    return '<span class="gtag gtag--' + key + '">' + TYPE_LABEL[key] + '</span>';
  }

  // 卡片第二行：能拿到进度就给进度，拿不到就给 AppID。
  // 永远返回一句非空文案，否则卡片高度会随数据有无而跳动。
  function cardMetaText(g) {
    var p = sessionProgress[String(g.id)];
    if (p && p.total > 0) return p.unlocked + ' / ' + p.total + ' 成就';
    return 'AppID ' + g.id;
  }

  function gameCardHtml(g, index) {
    var mono = monogram(g.name);
    var hue = hash32('' + g.id) % 300;
    var out = [];
    out.push('<button type="button" class="game-card" data-id="' + esc(g.id) + '"');
    out.push(' title="' + esc(g.name) + '">');
    out.push('<span class="game-card__cover" data-mono="' + esc(mono) + '">');
    var cover = coverImgHtml(g, true);
    out.push(cover || fallbackCover(mono, hue));
    out.push('</span>');
    // 正式版占绝大多数，逐张标注等于没标；只有例外（试玩/模组/其他）才显示标签。
    out.push(g.type && g.type !== 'normal'
      ? '<span class="game-card__tags">' + typeTagHtml(g.type) + '</span>'
      : '');
    out.push('<span class="game-card__name">' + esc(g.name) + '</span>');
    out.push('<span class="game-card__meta">' + esc(cardMetaText(g)) + '</span>');
    out.push('<span class="game-card__hover"></span>');
    out.push('</button>');
    return out.join('');
  }

  function skeletonHtml(count) {
    var out = [];
    for (var i = 0; i < count; i++) {
      out.push('<div class="skel"><div class="skel__cover"></div></div>');
    }
    return out.join('');
  }

  function renderPicker() {
    if (state.picker.loading && state.picker.games.length === 0) {
      setHidden(els.gameGrid, true);
      setHidden(els.pickerEmpty, true);
      els.pickerSkeleton.classList.add('is-on');
      els.pickerSkeleton.innerHTML = skeletonHtml(8);
      return;
    }
    els.pickerSkeleton.classList.remove('is-on');
    els.pickerSkeleton.innerHTML = '';

    var list = filteredGames();
    setHidden(els.gameGrid, list.length === 0);
    setHidden(els.pickerEmpty, list.length > 0);

    if (list.length === 0) {
      if (state.picker.games.length === 0) {
        els.pickerEmptyTitle.textContent = '还没有可管理的游戏';
        els.pickerEmptyText.textContent = '未在 Steam 库中找到任何游戏，试试点击右上角「重新扫描」。';
      } else {
        els.pickerEmptyTitle.textContent = '没有匹配的游戏';
        els.pickerEmptyText.textContent = '换个关键词，或切换上方的类型筛选。';
      }
      els.gameGrid.innerHTML = '';
      return;
    }
    els.gameGrid.innerHTML = list.map(gameCardHtml).join('');
    markLoadedImages();
  }

  /* ---------- Game ---------- */

  // 全服解锁率是否低于阈值（rarity < 0 表示 Steam 没有给出百分比，不算稀有）
  function isRareAt(a, pct) {
    return !!a && typeof a.rarity === 'number' && a.rarity >= 0 && a.rarity < pct;
  }

  function isRare(a) {
    return isRareAt(a, RARE_PCT);
  }

  function fmtPct(v) {
    if (typeof v !== 'number' || !isFinite(v) || v < 0) return '—';
    if (v >= 10) return Math.round(v) + '%';
    if (v >= 1) return (Math.round(v * 10) / 10) + '%';
    return (Math.round(v * 100) / 100) + '%';
  }

  function achMatches(a, on) {
    var filter = state.ui.filter;
    if (filter === 'unlocked' && !on) return false;
    if (filter === 'locked' && on) return false;
    if (filter === 'rare' && !isRare(a)) return false;
    var q = state.ui.query.trim().toLowerCase();
    if (!q) return true;
    if (String(a.name).toLowerCase().indexOf(q) >= 0) return true;
    if (String(a.desc).toLowerCase().indexOf(q) >= 0) return true;
    return String(a.id).toLowerCase().indexOf(q) >= 0;
  }

  function filteredAch() {
    return state.game.achievements.filter(function (a) {
      return achMatches(a, desiredAch(a));
    });
  }

  function achIconHtml(a, on) {
    var src = (!on && a.iconLocked) ? a.iconLocked : a.icon;
    if (src) {
      return '<img src="' + esc(src) + '" alt="" loading="lazy" decoding="async" />';
    }
    return fallbackIcon(monogram(a.name));
  }

  function achTagsHtml(a, on) {
    var out = [];
    if (on && a.unlockTime) {
      out.push('<span class="tag tag--time">' + SVG_CLOCK + esc(fmtTime(a.unlockTime)) + '</span>');
    }
    if (own(state.pending.ach, a.id)) {
      out.push('<span class="tag tag--ok">待保存</span>');
    }
    if (isRare(a)) {
      out.push('<span class="tag tag--rare" title="全服解锁率 ' + fmtPct(a.rarity) + '">' +
        SVG_RARE + '稀有 ' + fmtPct(a.rarity) + '</span>');
    }
    if (a.protect) {
      out.push('<span class="tag tag--protect">' + SVG_LOCK + '受保护</span>');
    }
    if (a.hidden) {
      out.push('<span class="tag tag--hidden">隐藏</span>');
    }
    // 不再给每个未解锁成就补一个「未解锁」标签：365 行都写同一句是纯噪音，
    // 未解锁本身由「名字降一级 + 无勾」表达。
    return out.join('');
  }

  // 一行一个成就：图标 | 名称+描述 | 标签 | 勾选。用 grid-template-areas 直接排版，
  // 不再包一层 .ach__body（包裹层会打断 grid 区域布局）。无入场动画：长列表错峰
  // 会让人等，且和首帧分块渲染叠加成两次视觉跳动。
  function achCardHtml(a, index) {
    var on = desiredAch(a);
    var classes = 'ach ' + (on ? 'is-on' : 'is-locked');
    var desc = (a.hidden && !on) ? '隐藏成就：解锁前无法查看描述。' : (a.desc || '暂无描述');
    var out = [];
    out.push('<button type="button" class="' + classes + '" data-id="' + esc(a.id) + '"');
    out.push(' aria-pressed="' + (on ? 'true' : 'false') + '">');
    out.push('<span class="ach__icon">' + achIconHtml(a, on) + '</span>');
    out.push('<span class="ach__name" title="' + esc(a.name) + '">' + esc(a.name) + '</span>');
    out.push('<span class="ach__desc" title="' + esc(desc) + '">' + esc(desc) + '</span>');
    out.push('<span class="ach__foot">' + achTagsHtml(a, on) + '</span>');
    out.push('<span class="ach__check">' + SVG_CHECK + '</span>');
    out.push('</button>');
    return out.join('');
  }

  function statTagsHtml(s) {
    var out = [];
    if (s.protect) {
      out.push('<span class="tag tag--protect">' + SVG_LOCK + '受保护</span>');
    }
    if (s.incrementOnly) {
      out.push('<span class="tag tag--ok">只增</span>');
    }
    if (out.length === 0) {
      out.push('<span class="tag">可编辑</span>');
    }
    return out.join('');
  }

  function rangeText(s) {
    var lo = Number(s.min);
    var hi = Number(s.max);
    var loOpen = !isFinite(lo) || lo <= -2147483648;
    var hiOpen = !isFinite(hi) || hi >= 2147483647;
    if (loOpen && hiOpen) return '<span class="stat-range__any">不限</span>';
    if (hiOpen) return '≥ ' + fmtNumber(lo, s.isFloat);
    if (loOpen) return '≤ ' + fmtNumber(hi, s.isFloat);
    return fmtNumber(lo, s.isFloat) + ' – ' + fmtNumber(hi, s.isFloat);
  }

  function statRowHtml(s) {
    var value = desiredStat(s);
    var rows = state.pending.stats;
    var classes = [];
    if (own(rows, s.id)) classes.push('is-edited');
    if (s.protect) classes.push('is-protect');
    var out = [];
    out.push('<tr data-id="' + esc(s.id) + '"');
    if (classes.length) out.push(' class="' + classes.join(' ') + '"');
    out.push('>');
    out.push('<td class="stat-name" title="' + esc(s.id) + '">' + esc(s.name) +
      '<small>' + esc(s.id) + '</small></td>');
    out.push('<td class="c-val"><input class="stat-input" type="number" inputmode="decimal" data-id="' +
      esc(s.id) + '" step="' + (s.isFloat ? '0.01' : '1') +
      '" min="' + s.min + '" max="' + s.max +
      '" value="' + esc(fmtNumber(value, s.isFloat)) + '"' +
      (s.protect ? ' disabled' : '') +
      ' aria-label="' + esc(s.name) + '" /></td>');
    out.push('<td class="stat-range">' + rangeText(s) + '</td>');
    out.push('<td class="stat-flag">' + statTagsHtml(s) + '</td>');
    out.push('</tr>');
    return out.join('');
  }

  function renderSideCover() {
    var g = state.game;
    var mono = monogram(g.name || 'SAM');
    els.sideCover.setAttribute('data-mono', mono);
    var cover = coverImgHtml(g);
    els.sideCover.innerHTML = cover || fallbackCover(mono, hash32('' + g.appId) % 300);
    els.sideTitle.textContent = g.name || '未选择游戏';
    els.sideTitle.title = g.name || '';

    var meta = [];
    if (g.appId) meta.push('<span class="chip">AppID ' + esc(g.appId) + '</span>');
    meta.push('<span class="chip">' + g.achievements.length + ' 个成就</span>');
    meta.push('<span class="chip">' + g.stats.length + ' 项统计</span>');
    els.sideMeta.innerHTML = meta.join('');
  }

  function renderSideFacts() {
    var p = progressInfo();
    var ratio = p.total > 0 ? p.unlocked / p.total : 0;
    var pct = ratio * 100;
    els.progUnlocked.textContent = String(p.unlocked);
    els.progTotal.textContent = String(p.total);
    els.progPct.textContent = (pct % 1 === 0 ? String(Math.round(pct)) : pct.toFixed(1)) + '%';
    els.progRest.textContent = '剩余 ' + (p.total - p.unlocked) + ' 项';
    els.progFill.style.width = (ratio * 100) + '%';

    // 这三项以前是「成就进度 / 统计项目 / 待保存改动」，分别和上方大数字、
    // meta 行、右上角保存按钮重复。改成左栏独有的两条：什么时候解锁的、有多少稀有成就。
    var list = state.game.achievements;
    var latest = 0;
    var rareTotal = 0;
    var rareUnlocked = 0;
    for (var i = 0; i < list.length; i++) {
      var a = list[i];
      if (isRare(a)) rareTotal++;
      if (!desiredAch(a)) continue;
      if (isRare(a)) rareUnlocked++;
      if (a.unlockTime && a.unlockTime > latest) latest = a.unlockTime;
    }
    els.sideFacts.innerHTML = [
      '<div><dt>最近解锁</dt><dd>' + (latest > 0 ? esc(fmtAgo(latest)) : '—') + '</dd></div>',
      '<div><dt>稀有成就</dt><dd>' + (rareTotal > 0 ? rareUnlocked + ' / ' + rareTotal : '—') + '</dd></div>'
    ].join('');

    recordProgress(state.game.appId, p.total, p.unlocked);
  }

  function renderGame() {
    renderSideCover();
    renderUnlockBar();

    if (state.game.loading) {
      setHidden(els.gameLoading, false);
      setHidden(els.achGrid, true);
      setHidden(els.achEmpty, true);
      setHidden(els.statWrap, true);
      renderSideFacts();
      return;
    }
    setHidden(els.gameLoading, true);

    if (state.ui.tab === 'stats') {
      setHidden(els.achGrid, true);
      setHidden(els.achEmpty, true);
      setHidden(els.statWrap, false);
      renderStats();
      renderSideFacts();
      return;
    }

    setHidden(els.statWrap, true);
    var list = filteredAch();
    setHidden(els.achGrid, list.length === 0);
    setHidden(els.achEmpty, list.length > 0);

    if (list.length === 0) {
      els.achGrid.innerHTML = '';
      if (state.game.achievements.length === 0) {
        els.achEmptyTitle.textContent = '这个游戏没有成就';
        els.achEmptyText.textContent = 'Steam 未返回任何成就数据，可能该游戏不支持成就系统。';
      } else {
        els.achEmptyTitle.textContent = '没有匹配的成就';
        els.achEmptyText.textContent = state.ui.filter === 'rare'
          ? '这个游戏没有全服解锁率低于 ' + RARE_PCT + '% 的成就。'
          : '换个关键词，或切换「全部 / 已解锁 / 未解锁 / 稀有」。';
      }
    } else {
      renderAchGrid(list);
      renderSideFacts();
      return;
    }
    markLoadedImages();
    renderSideFacts();
  }

  /* 首帧只排可见的那十几张卡：一个游戏动辄 300+ 成就，一次性 innerHTML
     会让浏览器对整表做样式计算与布局，实测首帧多花 150~200ms，正好卡在
     「窗口该亮了」的关键路径上。剩下的卡片在首帧之后分块追加，用户看不到
     （都在滚动条下方），但窗口能提前亮起来。 */
  var ACH_FIRST_CHUNK = 24;
  var achRenderToken = 0;

  function renderAchGrid(list) {
    var token = ++achRenderToken;
    if (list.length <= ACH_FIRST_CHUNK) {
      els.achGrid.innerHTML = list.map(achCardHtml).join('');
      markLoadedImages();
      return;
    }
    els.achGrid.innerHTML = list.slice(0, ACH_FIRST_CHUNK).map(achCardHtml).join('');
    markLoadedImages();
    var index = ACH_FIRST_CHUNK;
    function step() {
      if (token !== achRenderToken) return;
      var end = Math.min(index + 64, list.length);
      var html = '';
      for (var i = index; i < end; i++) html += achCardHtml(list[i], i);
      els.achGrid.insertAdjacentHTML('beforeend', html);
      index = end;
      if (index < list.length) setTimeout(step, 0);
      else markLoadedImages();
    }
    setTimeout(step, 0);
  }

  function renderStats() {
    var list = state.game.stats;
    els.statBody.innerHTML = list.map(statRowHtml).join('');
  }

  function markLoadedImages() {
    var imgs = document.querySelectorAll('img');
    for (var i = 0; i < imgs.length; i++) {
      var img = imgs[i];
      if (!img.classList.contains('is-loaded') && img.complete && img.naturalWidth > 0) {
        img.classList.add('is-loaded');
      }
    }
  }

  /* ---------------------------------------------------------------------------
     8. Toast
     --------------------------------------------------------------------------- */

  function toast(kind, title, text) {
    var k = (kind === 'success' || kind === 'error' || kind === 'warn') ? kind : 'info';
    var el = document.createElement('div');
    el.className = 'toast toast--' + k;
    el.innerHTML = [
      '<span class="toast__icon">' + (ICON[k] || ICON.info) + '</span>',
      '<div class="toast__text">',
      title ? '<div class="toast__title">' + esc(title) + '</div>' : '',
      text ? '<div class="toast__body">' + esc(text) + '</div>' : '',
      '</div>',
      '<button type="button" class="toast__close" aria-label="关闭提示">' + SVG_CLOSE + '</button>'
    ].join('');

    els.toasts.appendChild(el);
    while (els.toasts.children.length > 4) {
      els.toasts.removeChild(els.toasts.firstChild);
    }

    var timer = window.setTimeout(dismiss, 4600);

    function dismiss() {
      window.clearTimeout(timer);
      if (el.classList.contains('is-out')) return;
      el.classList.add('is-out');
      window.setTimeout(function () {
        if (el.parentElement) el.parentElement.removeChild(el);
      }, 240);
    }

    var closeBtn = el.querySelector('.toast__close');
    if (closeBtn) closeBtn.addEventListener('click', dismiss);
    el.addEventListener('mouseenter', function () { window.clearTimeout(timer); });
    el.addEventListener('mouseleave', function () {
      timer = window.setTimeout(dismiss, 1500);
    });
  }

  /* ---------------------------------------------------------------------------
     9. 模态框
     --------------------------------------------------------------------------- */

  function openModal(opts) {
    closeModal();
    var o = opts || {};
    var danger = o.danger !== false;
    var showCancel = o.showCancel !== false;
    var el = document.createElement('div');
    el.className = 'modal';
    el.setAttribute('role', 'dialog');
    el.setAttribute('aria-modal', 'true');

    var parts = [];
    var body = [];
    body.push('<div class="modal__head">');
    body.push('<span class="modal__ico' + (danger ? ' modal__ico--danger' : ' modal__ico--info') + '">' +
      (danger ? ICON.warn : ICON.info) + '</span>');
    body.push('<h2 class="modal__title">' + esc(o.title || '提示') + '</h2>');
    body.push('</div>');
    if (o.text) body.push('<p class="modal__text">' + esc(o.text) + '</p>');
    if (o.checkboxLabel) {
      body.push('<label class="modal__check">' +
        '<input type="checkbox" id="samModalCheck" />' +
        '<span>' + esc(o.checkboxLabel) + '</span></label>');
    }
    parts.push('<div class="modal__body">' + body.join('') + '</div>');
    parts.push('<div class="modal__foot">');
    if (showCancel) {
      parts.push('<button type="button" class="btn btn--ghost" data-role="cancel">' +
        esc(o.cancelText || '取消') + '</button>');
    }
    parts.push('<button type="button" class="btn ' + (danger ? 'btn--danger' : 'btn--primary') +
      '" data-role="ok">' + esc(o.confirmText || '确认') + '</button>');
    parts.push('</div>');
    el.innerHTML = parts.join('');

    var prevFocus = document.activeElement;
    var okBtn = el.querySelector('[data-role="ok"]');
    var cancelBtn = el.querySelector('[data-role="cancel"]');

    function finish(confirmed) {
      var checkEl = el.querySelector('#samModalCheck');
      var checked = !!(checkEl && checkEl.checked);
      closeModal();
      if (confirmed && typeof o.onConfirm === 'function') o.onConfirm(checked);
    }

    if (okBtn) okBtn.addEventListener('click', function () { finish(true); });
    if (cancelBtn) cancelBtn.addEventListener('click', function () { finish(false); });

    els.modalRoot.innerHTML = '';
    els.modalRoot.appendChild(el);
    setHidden(els.modalRoot, false);
    modalState = { el: el, prevFocus: prevFocus };

    // 焦点给弹窗本身而不是「确认」按钮：程序化聚焦会画出焦点环，
    // 在破坏性操作的红色按钮上像一层发光描边。键盘用户 Tab 到按钮时仍有焦点环。
    el.setAttribute('tabindex', '-1');
    window.setTimeout(function () {
      try { el.focus({ preventScroll: true }); } catch (err) { /* 忽略 */ }
    }, 30);
  }

  function closeModal() {
    if (!modalState) {
      if (!els.modalRoot.hidden) {
        els.modalRoot.innerHTML = '';
        setHidden(els.modalRoot, true);
      }
      return;
    }
    var prev = modalState.prevFocus;
    modalState = null;
    els.modalRoot.innerHTML = '';
    setHidden(els.modalRoot, true);
    if (prev && typeof prev.focus === 'function') {
      try { prev.focus(); } catch (err) { /* 忽略 */ }
    }
  }

  /* ---------------------------------------------------------------------------
     10. 交互动作
     --------------------------------------------------------------------------- */

  function toggleAch(id) {
    var a = findAch(id);
    if (!a) return;
    var next = !desiredAch(a);
    if (next === !!a.achieved) delete state.pending.ach[a.id];
    else state.pending.ach[a.id] = next;

    if (!achMatches(a, next)) {
      renderGame();
      renderSaveButton();
      renderStatus();
      return;
    }
    updateAchCard(a, next);
    renderSideFacts();
    renderSaveButton();
    renderStatus();
  }

  function updateAchCard(a, on) {
    var cards = els.achGrid.children;
    var card = null;
    for (var i = 0; i < cards.length; i++) {
      if (cards[i].getAttribute('data-id') === a.id) { card = cards[i]; break; }
    }
    if (!card) return;
    if (on) {
      card.classList.add('is-on');
      card.classList.remove('is-locked');
    } else {
      card.classList.remove('is-on');
      card.classList.add('is-locked');
    }
    card.setAttribute('aria-pressed', on ? 'true' : 'false');
    var icon = card.querySelector('.ach__icon');
    if (icon) icon.innerHTML = achIconHtml(a, on);
    var foot = card.querySelector('.ach__foot');
    if (foot) foot.innerHTML = achTagsHtml(a, on);
    var desc = card.querySelector('.ach__desc');
    if (desc) {
      var text = (a.hidden && !on) ? '隐藏成就：解锁前无法查看描述。' : (a.desc || '暂无描述');
      desc.textContent = text;
      desc.title = text;
    }
    window.requestAnimationFrame(markLoadedImages);
  }

  function clampStat(value, s) {
    var v = Number(value);
    if (!isFinite(v)) v = s.value;
    if (v < s.min) v = s.min;
    if (v > s.max) v = s.max;
    if (!s.isFloat) v = Math.round(v);
    else v = Math.round(v * 100) / 100;
    return v;
  }

  function commitStatInput(input, clamp) {
    if (!input || !input.classList || !input.classList.contains('stat-input')) return;
    var id = input.getAttribute('data-id');
    var s = findStat(id);
    if (!s || s.protect) return;
    var raw = input.value;
    if (raw === '' || raw === '-' || raw === '.') return;
    var v = Number(raw);
    if (!isFinite(v)) return;
    if (clamp) {
      v = clampStat(v, s);
      input.value = fmtNumber(v, s.isFloat);
    }
    if (v === s.value) delete state.pending.stats[id];
    else state.pending.stats[id] = v;
    var row = input.parentElement ? input.parentElement.parentElement : null;
    if (row && row.classList) row.classList.toggle('is-edited', own(state.pending.stats, id));
    renderSaveButton();
    renderSideFacts();
    renderStatus();
  }

  function onSave() {
    if (state.saving) return;
    var changes = collectChanges();
    var count = Object.keys(changes.achievements).length + Object.keys(changes.stats).length;
    if (count === 0) {
      toast('info', '没有需要保存的改动', '先勾选成就或修改统计数值吧。');
      return;
    }
    if (state.mock) {
      mockStore(changes);
      return;
    }
    state.saving = true;
    renderSaveButton();
    send('game/store', { achievements: changes.achievements, stats: changes.stats });
    window.clearTimeout(saveGuard);
    saveGuard = window.setTimeout(function () {
      if (!state.saving) return;
      state.saving = false;
      renderSaveButton();
      toast('warn', '主机响应超时', '保存请求已发出，但没有收到确认，请稍后点击「刷新数据」核对。');
    }, 12000);
  }

  function onBatch(kind) {
    if (state.game.loading) return;
    var list = state.game.achievements;
    var touched = 0;
    var blocked = 0;
    for (var i = 0; i < list.length; i++) {
      var a = list[i];
      var base = !!a.achieved;
      var target;
      if (kind === 'unlockAll') target = true;
      else if (kind === 'lockAll') target = false;
      else target = !desiredAch(a);
      if (target === base) {
        delete state.pending.ach[a.id];
        continue;
      }
      if (a.protect) { blocked++; continue; }
      state.pending.ach[a.id] = target;
      touched++;
    }

    var done = kind === 'unlockAll' ? '已全选' : (kind === 'lockAll' ? '已全部锁定' : '已反选');
    var none = kind === 'unlockAll' ? '没有需要解锁的成就' : (kind === 'lockAll' ? '没有需要锁定的成就' : '没有可以反选的成就');
    if (touched === 0) {
      toast('info', none, blocked > 0 ? '剩余成就受 Steam 保护，无法修改。' : '所有成就已经是目标状态。');
    } else {
      toast('info', done + ' ' + touched + ' 个成就',
        '改动暂存于本地，点击「保存改动」后写入 Steam。' + (blocked > 0 ? '另有 ' + blocked + ' 个受保护成就已跳过。' : ''));
    }

    state.status.text = touched > 0
      ? '已在本地暂存 ' + dirtyCount() + ' 处改动，按 Ctrl+S 保存。'
      : '没有需要保存的改动。';
    state.status.busy = false;
    renderGame();
    renderSaveButton();
    renderStatus();
  }

  function onReset() {
    openModal({
      title: '重置所有统计？',
      text: '该操作会把本游戏的全部统计数值清零，且无法撤销。\n请确认你了解风险后再继续。',
      confirmText: '确认重置',
      cancelText: '取消',
      danger: true,
      checkboxLabel: '同时清除全部成就的解锁状态',
      onConfirm: function (checked) {
        if (state.mock) {
          mockReset(checked);
          return;
        }
        state.status.text = '已发送重置请求，等待主机处理…';
        state.status.busy = true;
        renderStatus();
        send('game/reset', { resetAchievements: !!checked });
        toast('info', '已发送重置请求', '主机处理完成后会自动刷新数据。');
      }
    });
  }

  /* ---------- 随机时间解锁 ---------- */

  // 解锁数量：全部剩余，以及剩余数的 1/4、2/4、3/4（只显示数字，不显示分数）
  function countOptions(available) {
    var out = [{ value: 0, label: '全部剩余' }];
    var fracs = [0.25, 0.5, 0.75];
    var seen = { 0: true };
    for (var i = 0; i < fracs.length; i++) {
      var n = Math.round(available * fracs[i]);
      if (n < 1) n = 1;
      if (n >= available) continue;
      if (seen[n]) continue;
      seen[n] = true;
      out.push({ value: n, label: n + ' 个' });
    }
    return out;
  }

  var RANDOM_GAPS = [
    { label: '快速 · 5–15 秒', min: 5, max: 15 },
    { label: '标准 · 15–45 秒', min: 15, max: 45 },
    { label: '缓慢 · 60–180 秒', min: 60, max: 180 },
    { label: '自定义', custom: true }
  ];

  // 自定义间隔按「天 / 小时 / 分钟 / 秒」四个输入框填写
  var GAP_PARTS = [
    { key: 'd', label: '天', unit: 86400, max: 7 },
    { key: 'h', label: '小时', unit: 3600, max: 23 },
    { key: 'm', label: '分', unit: 60, max: 59 },
    { key: 's', label: '秒', unit: 1, max: 59 }
  ];

  var GAP_MAX_SECONDS = 7 * 86400;

  // 把秒数拆成 天 / 小时 / 分 / 秒 四个输入框的初始值
  function gapPartIndex(seconds) {
    var s = Math.max(0, Math.round(seconds || 0));
    var out = [];
    for (var i = 0; i < GAP_PARTS.length; i++) {
      var p = GAP_PARTS[i];
      var v = Math.floor(s / p.unit);
      if (v > p.max) v = p.max;
      s -= v * p.unit;
      out.push(v);
    }
    return out;
  }

  function fmtGap(seconds) {
    var s = Math.max(0, Math.round(seconds || 0));
    if (s < 60) return s + ' 秒';
    if (s < 3600) {
      var m = Math.floor(s / 60);
      var rs = s % 60;
      return rs > 0 ? m + ' 分 ' + rs + ' 秒' : m + ' 分钟';
    }
    if (s < 86400) {
      var h = Math.floor(s / 3600);
      var rm = Math.floor((s % 3600) / 60);
      return rm > 0 ? h + ' 小时 ' + rm + ' 分' : h + ' 小时';
    }
    var d = Math.floor(s / 86400);
    var rh = Math.floor((s % 86400) / 3600);
    return rh > 0 ? d + ' 天 ' + rh + ' 小时' : d + ' 天';
  }

  var mockUnlockTimer = 0;

  function randomCandidates() {
    var out = [];
    var list = state.game.achievements;
    for (var i = 0; i < list.length; i++) {
      var a = list[i];
      if (a.protect) continue;
      if (desiredAch(a)) continue;
      out.push(a);
    }
    return out;
  }

  function onRandomUnlock() {
    if (state.game.loading) return;
    if (state.game.unlock && state.game.unlock.active) {
      toast('info', '随机解锁正在进行', '请等待这一轮结束，或点进度条上的「停止」。');
      return;
    }
    var pool = randomCandidates();
    if (pool.length === 0) {
      toast('info', '没有可解锁的成就', '该游戏所有可修改的成就都已经解锁了。');
      return;
    }
    openRandomModal(pool);
  }

  function openRandomModal(pool) {
    closeModal();
    var list = Array.isArray(pool) ? pool : [];
    var sel = { count: 0, gap: 1, rare: 0 };
    var el = document.createElement('div');
    el.className = 'modal modal--wide';
    el.setAttribute('role', 'dialog');
    el.setAttribute('aria-modal', 'true');

    var parts = [];
    var body = [];
    body.push('<div class="modal__head">');
    body.push('<span class="modal__ico modal__ico--info">' + ICON.info + '</span>');
    body.push('<h2 class="modal__title">随机时间解锁</h2>');
    body.push('</div>');
    var rarityKnown = list.some(function (a) { return a && typeof a.rarity === 'number' && a.rarity >= 0; });

    // 说明只讲结果：解锁几个、大概要等多久、中途能不能停。
    // 具体数字交给 syncSummary()，按当前选择实时刷新。
    body.push('<p class="modal__text" data-role="summary"></p>');

    function groupHtml(key, label, items) {
      var h = ['<div class="opt-group" data-key="' + key + '">'];
      h.push('<span class="opt-group__label">' + esc(label) + '</span>');
      h.push('<div class="opt-row">');
      for (var i = 0; i < items.length; i++) {
        var it = items[i];
        var extra = it && it.pct > 0 ? ' data-rare="' + it.pct + '"' : '';
        var dim = it && it.dim ? ' is-dim' : '';
        h.push('<button type="button" class="opt' + (i === sel[key] ? ' is-active' : '') + dim +
          '" data-value="' + i + '"' + extra + '>' + esc(it.label) + '</button>');
      }
      h.push('</div></div>');
      return h.join('');
    }

    // 稀有度档位：先选范围，再定数量，所以候选池跟着档位变化
    function poolFor(rareIdx) {
      var pct = (RARE_OPTIONS[rareIdx] || RARE_OPTIONS[0]).pct;
      if (!pct) return list;
      return list.filter(function (a) { return isRareAt(a, pct); });
    }

    // 稀有档位里一个候选都没有的，显示为不可选（0 个）
    function rareItems() {
      var out = [];
      for (var i = 0; i < RARE_OPTIONS.length; i++) {
        var o = RARE_OPTIONS[i];
        var n = poolFor(i).length;
        out.push({ label: o.label + (n > 0 ? '（' + n + '）' : '（0）'), pct: o.pct, dim: n === 0 });
      }
      return out;
    }

    // 数量档位跟着稀有范围走：换了范围，可解锁数就变了
    function activeCounts() {
      return countOptions(poolFor(sel.rare).length);
    }

    var counts = activeCounts();

    body.push(groupHtml('rare', '解锁范围', rareItems()));
    body.push(groupHtml('count', '解锁数量', counts));
    body.push(groupHtml('gap', '解锁间隔', RANDOM_GAPS));

    // 自定义间隔：最短 / 最长各填 天、小时、分、秒 四个数字
    function gapRowHtml(cap, rolePrefix, seconds) {
      var vs = gapPartIndex(seconds);
      var h = ['<div class="gap-custom__row">'];
      h.push('<span class="gap-custom__cap">' + cap + '</span>');
      for (var i = 0; i < GAP_PARTS.length; i++) {
        var p = GAP_PARTS[i];
        h.push('<label class="gap-custom__field">');
        h.push('<input class="gap-custom__num" type="number" min="0" max="' + p.max + '" step="1" value="' + vs[i] +
          '" data-role="' + rolePrefix + p.key + '" aria-label="' + cap + p.label + '" />');
        h.push('<span class="gap-custom__suffix">' + p.label + '</span>');
        h.push('</label>');
      }
      h.push('</div>');
      return h.join('');
    }

    body.push('<div class="gap-custom" data-role="customGap" hidden>');
    body.push(gapRowHtml('最短', 'gapMin', 30));
    body.push(gapRowHtml('最长', 'gapMax', 90));
    body.push('<p class="gap-custom__hint" data-role="gapHint"></p>');
    body.push('</div>');

    parts.push('<div class="modal__body">' + body.join('') + '</div>');
    parts.push('<div class="modal__foot">');
    parts.push('<button type="button" class="btn btn--ghost" data-role="cancel">取消</button>');
    parts.push('<button type="button" class="btn btn--primary" data-role="ok">开始解锁</button>');
    parts.push('</div>');
    el.innerHTML = parts.join('');

    var prevFocus = document.activeElement;

    var customBox = el.querySelector('[data-role="customGap"]');
    var gapHint = el.querySelector('[data-role="gapHint"]');
    var summaryText = el.querySelector('[data-role="summary"]');
    var okBtn = el.querySelector('[data-role="ok"]');
    var minInputs = [];
    var maxInputs = [];
    for (var pi = 0; pi < GAP_PARTS.length; pi++) {
      minInputs.push(el.querySelector('[data-role="gapMin' + GAP_PARTS[pi].key + '"]'));
      maxInputs.push(el.querySelector('[data-role="gapMax' + GAP_PARTS[pi].key + '"]'));
    }

    function readParts(inputs) {
      var total = 0;
      for (var i = 0; i < GAP_PARTS.length; i++) {
        var v = Number(inputs[i] && inputs[i].value);
        if (!isFinite(v) || v < 0) v = 0;
        v = Math.floor(Math.min(v, GAP_PARTS[i].max));
        total += v * GAP_PARTS[i].unit;
      }
      return total;
    }

    function readCustom() {
      var lo = readParts(minInputs);
      var hi = readParts(maxInputs);
      if (hi < lo) { var t = lo; lo = hi; hi = t; }
      lo = Math.max(1, Math.min(GAP_MAX_SECONDS, lo));
      hi = Math.max(lo, Math.min(GAP_MAX_SECONDS, hi));
      return { minGap: lo, maxGap: hi };
    }

    // 把当前选择换算成一次真实运行：解锁几个、间隔多少、整轮大概多久。
    // 主机的节奏是「第一个成就先等 delay 秒，之后每个间隔在 [minGap, maxGap] 里随机」，
    // 所以整轮耗时 = delay + (n - 1) × 间隔，不是 n × 间隔（旧文案按 n × 平均间隔算，多算了一档）。
    function planFor() {
      var avail = poolFor(sel.rare).length;
      var opts = activeCounts();
      var picked = opts[sel.count] || opts[0];
      var n = picked.value > 0 ? Math.min(picked.value, avail) : avail;
      var gap = RANDOM_GAPS[sel.gap] || RANDOM_GAPS[1];
      var custom = gap.custom ? readCustom() : null;
      var lo = custom ? custom.minGap : gap.min;
      var hi = custom ? custom.maxGap : gap.max;
      var delay = Math.min(5, lo);
      var totalLow = n > 0 ? delay + (n - 1) * lo : 0;
      var totalHigh = n > 0 ? delay + (n - 1) * hi : 0;
      return { avail: avail, n: n, lo: lo, hi: hi, totalLow: totalLow, totalHigh: totalHigh };
    }

    function durationText(plan) {
      if (plan.n <= 0) return '';
      if (plan.totalLow === plan.totalHigh) return fmtGap(plan.totalLow);
      return fmtGap(plan.totalLow) + ' – ' + fmtGap(plan.totalHigh);
    }

    // 弹窗里所有随选择变化的东西都走这里：顶部说明、自定义间隔提示、开始按钮文案。
    // 数量、范围、间隔任意一项改动都会重算，所以说明里的数字永远和实际执行一致。
    function syncSummary() {
      var plan = planFor();
      var gap = RANDOM_GAPS[sel.gap] || RANDOM_GAPS[1];
      setHidden(customBox, !gap.custom);

      if (gapHint && gap.custom) {
        gapHint.textContent = '每个成就之间随机等 ' + fmtGap(plan.lo) + ' – ' + fmtGap(plan.hi) + '。';
      }

      if (summaryText) {
        if (plan.avail <= 0) {
          summaryText.textContent = '当前范围里没有可解锁的成就。换一个更大的范围试试。';
        } else {
          var lines = [];
          lines.push('将解锁 ' + plan.n + ' 个成就，每两个之间随机等 ' + fmtGap(plan.lo) +
            (plan.hi > plan.lo ? '到 ' + fmtGap(plan.hi) : '') +
            '，预计耗时 ' + durationText(plan) + '。');
          if (rarityKnown) {
            var pct = (RARE_OPTIONS[sel.rare] || RARE_OPTIONS[0]).pct;
            if (pct > 0) {
              lines.push('候选里的 ' + plan.avail + ' 个成就都属于全服解锁率低于 ' + pct + '% 的稀有成就。');
            } else {
              var rareInPool = list.filter(isRare).length;
              if (rareInPool > 0) {
                lines.push('候选里有 ' + rareInPool + ' 个是全服解锁率低于 ' + RARE_PCT + '% 的稀有成就。');
              }
            }
          }
          lines.push('过程中可以随时点进度条上的「停止」，已经解锁的成就不会回退。');
          summaryText.textContent = lines.join('\n');
        }
      }

      if (okBtn) okBtn.textContent = plan.n > 0 ? '开始解锁 ' + plan.n + ' 个' : '开始解锁';
    }

    if (customBox) {
      customBox.addEventListener('input', syncSummary);
      customBox.addEventListener('change', syncSummary);
    }

    el.addEventListener('click', function (e) {
      var opt = e.target.closest ? e.target.closest('.opt') : null;
      if (opt) {
        var g = opt.closest('.opt-group');
        if (!g) return;
        var key = g.getAttribute('data-key');
        sel[key] = Number(opt.getAttribute('data-value')) || 0;
        var btns = g.querySelectorAll('.opt');
        for (var i = 0; i < btns.length; i++) {
          if (btns[i] === opt) btns[i].classList.add('is-active');
          else btns[i].classList.remove('is-active');
        }
        if (key === 'rare') {
          // 换了解锁范围，数量档位要重算（原选择可能超出新的候选数）
          var box = el.querySelector('.opt-group[data-key="count"]');
          sel.count = 0;
          if (box) box.outerHTML = groupHtml('count', '解锁数量', activeCounts());
        }
        if (key === 'gap' || key === 'count' || key === 'rare') syncSummary();
        return;
      }
      var hit = e.target.closest ? e.target.closest('[data-role]') : null;
      var role = hit ? hit.getAttribute('data-role') : '';
      if (role === 'cancel') {
        closeModal();
        return;
      }
      if (role === 'ok') {
        var avail = poolFor(sel.rare).length;
        if (avail <= 0) {
          toast('info', '这个范围没有可解锁的成就',
            RARE_OPTIONS[sel.rare] && RARE_OPTIONS[sel.rare].pct > 0
              ? '换个更大的范围吧，当前范围里没有满足条件的成就。'
              : '该游戏所有可修改的成就都已经解锁了。');
          return;
        }
        var gap = RANDOM_GAPS[sel.gap] || RANDOM_GAPS[1];
        var opts = activeCounts();
        var entry = opts[sel.count] || opts[0];
        var lo = gap.custom ? readCustom().minGap : gap.min;
        var hi = gap.custom ? readCustom().maxGap : gap.max;
        var rarePct = (RARE_OPTIONS[sel.rare] || RARE_OPTIONS[0]).pct;
        startRandomUnlock({
          count: entry.value,
          minGap: lo,
          maxGap: hi,
          delay: Math.min(5, lo),
          rareOnly: rarePct > 0,
          rarePct: rarePct > 0 ? rarePct : RARE_PCT
        });
      }
    });

    els.modalRoot.innerHTML = '';
    els.modalRoot.appendChild(el);
    setHidden(els.modalRoot, false);
    modalState = { el: el, prevFocus: prevFocus };
    syncSummary();
    window.setTimeout(function () {
      var ok = el.querySelector('[data-role="ok"]');
      if (ok && ok.focus) ok.focus();
    }, 30);
  }

  function startRandomUnlock(cfg) {
    closeModal();
    if (state.mock) {
      mockRandomUnlock(cfg);
      return;
    }
    state.game.unlock = { active: true, done: 0, total: 0, next: cfg.delay };
    state.status.text = '随机解锁已开始，正在等待第一个成就…';
    state.status.busy = true;
    renderGame();
    renderStatus();
    send('game/unlockRandom', {
      count: cfg.count,
      minGap: cfg.minGap,
      maxGap: cfg.maxGap,
      delay: cfg.delay,
      rareOnly: !!cfg.rareOnly,
      rarePct: cfg.rarePct || RARE_PCT
    });
  }

  function onStopRandomUnlock() {
    if (!(state.game.unlock && state.game.unlock.active)) return;
    if (state.mock) {
      stopMockRandom();
      return;
    }
    send('game/unlockRandom/cancel');
  }

  function mockRandomUnlock(cfg) {
    var pool = randomCandidates();
    if (pool.length === 0) return;
    if (cfg.count > 0 && cfg.count < pool.length) pool = pool.slice(0, cfg.count);
    state.game.unlock = { active: true, done: 0, total: pool.length, next: 1 };
    var i = 0;
    state.status.text = '随机解锁已开始：共 ' + pool.length + ' 个成就（演示模式）。';
    state.status.busy = true;
    renderGame();
    renderStatus();

    function step() {
      if (!state.game.unlock || !state.game.unlock.active) return;
      if (i >= pool.length) {
        var done = i;
        state.game.unlock = { active: false, done: done, total: done, next: 0 };
        state.status.busy = false;
        state.status.text = '随机解锁完成：本次共解锁 ' + done + ' 个成就。';
        toast('success', '随机解锁完成', '演示模式下不写入 Steam；真实模式下每个成就都会带上各自的解锁时间。');
        renderGame();
        renderStatus();
        return;
      }
      var target = findAch(pool[i].id);
      if (target) {
        target.achieved = true;
        target.unlockTime = Math.floor(Date.now() / 1000);
      }
      i++;
      state.game.unlock.done = i;
      state.game.unlock.next = 1;
      state.status.text = '随机解锁进行中：' + i + ' / ' + pool.length + '（演示模式）';
      state.status.busy = true;
      renderGame();
      renderStatus();
      mockUnlockTimer = window.setTimeout(step, 700);
    }

    window.clearTimeout(mockUnlockTimer);
    mockUnlockTimer = window.setTimeout(step, 700);
  }

  function stopMockRandom() {
    window.clearTimeout(mockUnlockTimer);
    var u = state.game.unlock || { done: 0, total: 0 };
    state.game.unlock = { active: false, done: u.done, total: u.total, next: 0 };
    state.status.busy = false;
    state.status.text = '已停止随机解锁：本次共解锁 ' + u.done + ' 个成就。';
    toast('info', '已停止随机解锁', '本次共解锁 ' + u.done + ' / ' + u.total + ' 个成就（演示模式）。');
    renderGame();
    renderStatus();
  }

  function onReload() {
    if (state.mock) {
      state.picker.loading = true;
      state.picker.games = [];
      state.status.text = '正在重新扫描游戏库…';
      state.status.busy = true;
      render();
      window.setTimeout(function () {
        state.picker.games = buildMockGames();
        state.picker.total = state.picker.games.length;
        state.picker.loading = false;
        state.status.busy = false;
        state.status.text = '扫描完成，共找到 ' + state.picker.total + ' 个游戏';
        render();
        toast('success', '扫描完成', '演示模式下重新载入了 ' + state.picker.total + ' 个游戏。');
      }, 620);
      return;
    }
    state.status.text = '正在重新扫描游戏库…';
    state.status.busy = true;
    state.picker.loading = true;
    renderStatus();
    send('picker/reload');
  }

  function onRefreshGame() {
    if (state.mock) {
      if (state.game.appId) {
        var id = state.game.appId;
        toast('info', '演示模式', '正在重新生成《' + state.game.name + '》的演示数据。');
        mockOpenGame(id);
      }
      return;
    }
    state.status.text = '正在刷新成就与统计数据…';
    state.status.busy = true;
    state.game.loading = true;
    render();
    send('game/refresh');
  }

  /* ---------------------------------------------------------------------------
     10.5 定时解锁（指定某一分钟开始，到点自动执行）
     --------------------------------------------------------------------------- */

  // 常用时刻：观众点名的几个整点 / 叠数时间，点一下直接填进输入框
  var SCHEDULE_QUICK = ['11:11', '22:22', '00:00', '04:44'];

  // 批量定时里每个游戏解锁多少个：游戏库阶段拿不到各游戏的候选数，只能给固定档位
  var SCHEDULE_BATCH_COUNTS = [
    { value: 0, label: '全部剩余' },
    { value: 5, label: '各 5 个' },
    { value: 10, label: '各 10 个' },
    { value: 20, label: '各 20 个' }
  ];

  function two(n) {
    return n < 10 ? '0' + n : String(n);
  }

  function hhmm(hour, minute) {
    return two(hour) + ':' + two(minute);
  }

  // 把「时 / 分」换算成下一次出现的绝对时刻：今天已经过了（或只剩不到 1 分钟）就顺延到明天。
  function nextAt(hour, minute) {
    var now = new Date();
    var d = new Date(now.getFullYear(), now.getMonth(), now.getDate(), hour, minute, 0, 0);
    if (d.getTime() <= now.getTime() + 60000) d = new Date(d.getTime() + 86400000);
    return d;
  }

  function dayLabel(date) {
    var now = new Date();
    var d0 = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
    var d1 = new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
    var diff = Math.round((d1 - d0) / 86400000);
    if (diff === 0) return '今天';
    if (diff === 1) return '明天';
    if (diff === 2) return '后天';
    return two(date.getMonth() + 1) + '-' + two(date.getDate());
  }

  function clockLabel(date) {
    return two(date.getMonth() + 1) + '-' + two(date.getDate()) + ' ' + hhmm(date.getHours(), date.getMinutes());
  }

  function countdownText(date) {
    var diff = Math.floor((date.getTime() - Date.now()) / 1000);
    if (diff <= 0) return '即将开始';
    return '还有 ' + fmtGap(diff);
  }

  // 选项组：定时解锁的几个弹窗共用（随机解锁那个弹窗有自己的闭包版本，不动它）
  function optGroupHtml(key, label, items, activeIdx) {
    var h = ['<div class="opt-group" data-key="' + key + '">'];
    h.push('<span class="opt-group__label">' + esc(label) + '</span>');
    h.push('<div class="opt-row">');
    for (var i = 0; i < items.length; i++) {
      var it = items[i];
      h.push('<button type="button" class="opt' + (i === activeIdx ? ' is-active' : '') +
        (it && it.dim ? ' is-dim' : '') + '" data-value="' + i + '">' + esc(it.label) + '</button>');
    }
    h.push('</div></div>');
    return h.join('');
  }

  function schedPool(list, rareIdx) {
    var pct = (RARE_OPTIONS[rareIdx] || RARE_OPTIONS[0]).pct;
    if (!pct) return list;
    return list.filter(function (a) { return isRareAt(a, pct); });
  }

  function schedRareItems(list) {
    var out = [];
    for (var i = 0; i < RARE_OPTIONS.length; i++) {
      var n = schedPool(list, i).length;
      out.push({ label: RARE_OPTIONS[i].label + '（' + n + '）', dim: n === 0 });
    }
    return out;
  }

  // 定时解锁只用三个内置间隔档：自定义四段输入在随机解锁弹窗里已经有入口了
  function schedGaps() {
    return RANDOM_GAPS.slice(0, 3);
  }

  // 时刻输入行：时 / 分两个数字框 + 四个快捷时刻
  function timeRowHtml(hour, minute) {
    var h = ['<div class="sch-time">'];
    h.push('<span class="sch-time__cap">解锁时刻</span>');
    h.push('<label class="sch-time__field">');
    h.push('<input class="gap-custom__num" type="number" min="0" max="23" step="1" value="' + hour +
      '" data-role="hour" aria-label="解锁时刻的小时" />');
    h.push('<span class="gap-custom__suffix">时</span>');
    h.push('</label>');
    h.push('<label class="sch-time__field">');
    h.push('<input class="gap-custom__num" type="number" min="0" max="59" step="1" value="' + minute +
      '" data-role="minute" aria-label="解锁时刻的分钟" />');
    h.push('<span class="gap-custom__suffix">分</span>');
    h.push('</label>');
    h.push('</div>');
    h.push('<div class="sch-quick">');
    for (var i = 0; i < SCHEDULE_QUICK.length; i++) {
      h.push('<button type="button" class="opt" data-quick="' + SCHEDULE_QUICK[i] + '">' +
        SCHEDULE_QUICK[i] + '</button>');
    }
    h.push('</div>');
    return h.join('');
  }

  // 时刻输入的通用读数 + 收敛：输入框里允许随便打，算的时候夹到合法范围
  function readClock(hourInput, minuteInput) {
    var h = Math.floor(Number(hourInput && hourInput.value));
    var m = Math.floor(Number(minuteInput && minuteInput.value));
    if (!isFinite(h)) h = 0;
    if (!isFinite(m)) m = 0;
    h = Math.max(0, Math.min(23, h));
    m = Math.max(0, Math.min(59, m));
    return { hour: h, minute: m };
  }

  /* ---------- 标题栏的定时任务入口 ---------- */

  function renderScheduleChip() {
    if (!els.scheduleChip) return;
    var jobs = state.schedule.jobs || [];
    if (jobs.length === 0) {
      setHidden(els.scheduleChip, true);
      return;
    }
    var next = jobs[0];
    for (var i = 1; i < jobs.length; i++) {
      if (jobs[i].at && jobs[i].at < next.at) next = jobs[i];
    }
    var d = new Date((next.at || 0) * 1000);
    setHidden(els.scheduleChip, false);
    if (els.scheduleChipText) {
      els.scheduleChipText.textContent = jobs.length === 1
        ? dayLabel(d) + ' ' + hhmm(d.getHours(), d.getMinutes())
        : jobs.length + ' 个定时 · ' + hhmm(d.getHours(), d.getMinutes());
    }
    els.scheduleChip.title = jobs.length + ' 个定时解锁任务，最近一个 ' + clockLabel(d) + ' 开始（点击查看）';
  }

  function onScheduleList(ev) {
    state.schedule.jobs = Array.isArray(ev.jobs) ? ev.jobs : [];
    state.schedule.loaded = true;
    renderScheduleChip();
    renderStatus();
    if (modalState && modalState.kind === 'scheduleList') openScheduleListModal();
  }

  // started / done / missed 共用：从列表里更新或摘掉对应任务，然后刷新入口
  function onScheduleEvent(ev, kind) {
    var job = ev && ev.job ? ev.job : null;
    if (job && job.id) {
      var kept = [];
      var found = false;
      for (var i = 0; i < state.schedule.jobs.length; i++) {
        var it = state.schedule.jobs[i];
        if (it.id !== job.id) {
          kept.push(it);
          continue;
        }
        found = true;
        if (kind === 'started') kept.push(job);
      }
      if (kind === 'started' && !found) kept.push(job);
      state.schedule.jobs = kept;
    }
    renderScheduleChip();
    renderStatus();
    if (modalState && modalState.kind === 'scheduleList') openScheduleListModal();
  }

  /* ---------- 任务列表弹窗 ---------- */

  function jobMetaText(job) {
    var d = new Date((job.at || 0) * 1000);
    var bits = [dayLabel(d) + ' ' + hhmm(d.getHours(), d.getMinutes())];
    if (job.done || job.total) {
      bits.push('进行中 ' + (job.done || 0) + ' / ' + (job.total || 0));
    } else if (job.count > 0) {
      bits.push('解锁 ' + job.count + ' 个');
    } else {
      bits.push('全部剩余');
    }
    if (job.minGap && job.maxGap) bits.push('间隔 ' + fmtGap(job.minGap) + '–' + fmtGap(job.maxGap));
    bits.push(countdownText(d));
    return bits.join(' · ');
  }

  function openScheduleListModal() {
    closeModal();
    var jobs = (state.schedule.jobs || []).slice();
    var el = document.createElement('div');
    el.className = 'modal modal--wide';
    el.setAttribute('role', 'dialog');
    el.setAttribute('aria-modal', 'true');

    var parts = [];
    parts.push('<div class="modal__body">');
    parts.push('<div class="modal__head">');
    parts.push('<span class="modal__ico modal__ico--info">' + ICON.info + '</span>');
    parts.push('<h2 class="modal__title">定时解锁任务</h2>');
    parts.push('</div>');
    if (jobs.length === 0) {
      parts.push('<p class="modal__text">还没有定时任务。在游戏页点「定时解锁」，或在游戏库点「定时批量解锁」。</p>');
    } else {
      parts.push('<p class="modal__text">到点前请保持本工具和 Steam 都开着。任务只在开始前 25 秒拉起解锁进程，之后可以在任务上取消。</p>');
      parts.push('<div class="sch-jobs">');
      for (var i = 0; i < jobs.length; i++) {
        var job = jobs[i];
        parts.push('<div class="sch-job">');
        parts.push('<div class="sch-job__main">');
        parts.push('<span class="sch-job__name">' + esc(job.gameName || ('App ' + job.appId)) + '</span>');
        parts.push('<span class="sch-job__meta">' + esc(jobMetaText(job)) + '</span>');
        parts.push('</div>');
        parts.push('<button type="button" class="btn btn--sm btn--ghost" data-role="drop" data-id="' +
          esc(job.id) + '">取消</button>');
        parts.push('</div>');
      }
      parts.push('</div>');
    }
    parts.push('</div>');
    parts.push('<div class="modal__foot">');
    parts.push('<button type="button" class="btn btn--primary" data-role="close">关闭</button>');
    parts.push('</div>');
    el.innerHTML = parts.join('');

    var prevFocus = document.activeElement;
    el.addEventListener('click', function (e) {
      var hit = e.target.closest ? e.target.closest('[data-role]') : null;
      var role = hit ? hit.getAttribute('data-role') : '';
      if (role === 'close') {
        closeModal();
        return;
      }
      if (role === 'drop') {
        var id = hit.getAttribute('data-id');
        if (!id) return;
        if (state.mock) {
          var kept = [];
          for (var i = 0; i < state.schedule.jobs.length; i++) {
            if (state.schedule.jobs[i].id !== id) kept.push(state.schedule.jobs[i]);
          }
          state.schedule.jobs = kept;
          renderScheduleChip();
          renderStatus();
          openScheduleListModal();
          return;
        }
        send('schedule/cancel', { id: id });
      }
    });

    els.modalRoot.innerHTML = '';
    els.modalRoot.appendChild(el);
    setHidden(els.modalRoot, false);
    modalState = { el: el, prevFocus: prevFocus, kind: 'scheduleList' };
    window.setTimeout(function () {
      var close = el.querySelector('[data-role="close"]');
      if (close && close.focus) close.focus();
    }, 30);
  }

  /* ---------- 提交 ---------- */

  function submitSchedule(job) {
    closeModal();
    if (state.mock) {
      job.id = 'mock-' + String(Date.now());
      state.schedule.jobs = state.schedule.jobs.concat([job]);
      renderScheduleChip();
      renderStatus();
      toast('success', '已排入定时解锁',
        '《' + job.gameName + '》将在 ' + clockLabel(new Date(job.at * 1000)) + ' 开始解锁。');
      return;
    }
    send('schedule/create', job);
  }

  /* ---------- 游戏页：给当前游戏定一个时刻 ---------- */

  function onScheduleGame() {
    if (state.game.loading) return;
    var pool = randomCandidates();
    if (pool.length === 0) {
      toast('info', '没有可解锁的成就', '该游戏所有可修改的成就都已经解锁了。');
      return;
    }
    openScheduleGameModal(pool);
  }

  function openScheduleGameModal(pool) {
    closeModal();
    var list = Array.isArray(pool) ? pool : [];
    var sel = { count: 0, gap: 1, rare: 0, hour: 22, minute: 22 };
    var el = document.createElement('div');
    el.className = 'modal modal--wide';
    el.setAttribute('role', 'dialog');
    el.setAttribute('aria-modal', 'true');

    var body = [];
    body.push('<div class="modal__head">');
    body.push('<span class="modal__ico modal__ico--info">' + ICON.info + '</span>');
    body.push('<h2 class="modal__title">定时解锁</h2>');
    body.push('</div>');
    body.push('<p class="modal__text" data-role="summary"></p>');
    body.push('<div class="sch-block">');
    body.push(timeRowHtml(sel.hour, sel.minute));
    body.push('<p class="gap-custom__hint" data-role="timeHint"></p>');
    body.push('</div>');
    body.push('<div data-role="rareBox">' + optGroupHtml('rare', '解锁范围', schedRareItems(list), 0) + '</div>');
    body.push('<div data-role="countBox">' + optGroupHtml('count', '解锁数量', countOptions(list.length), 0) + '</div>');
    body.push(optGroupHtml('gap', '解锁间隔', schedGaps(), 1));

    var parts = [];
    parts.push('<div class="modal__body">' + body.join('') + '</div>');
    parts.push('<div class="modal__foot">');
    parts.push('<button type="button" class="btn btn--ghost" data-role="cancel">取消</button>');
    parts.push('<button type="button" class="btn btn--primary" data-role="ok">排入定时</button>');
    parts.push('</div>');
    el.innerHTML = parts.join('');

    var prevFocus = document.activeElement;
    var summaryText = el.querySelector('[data-role="summary"]');
    var timeHint = el.querySelector('[data-role="timeHint"]');
    var hourInput = el.querySelector('[data-role="hour"]');
    var minuteInput = el.querySelector('[data-role="minute"]');
    var okBtn = el.querySelector('[data-role="ok"]');

    function targetDate() {
      var c = readClock(hourInput, minuteInput);
      sel.hour = c.hour;
      sel.minute = c.minute;
      return nextAt(c.hour, c.minute);
    }

    function syncAll() {
      var target = targetDate();
      var poolNow = schedPool(list, sel.rare);
      var opts = countOptions(poolNow.length);
      var picked = opts[sel.count] || opts[0];
      var n = picked.value > 0 ? Math.min(picked.value, poolNow.length) : poolNow.length;
      var gaps = schedGaps();
      var gap = gaps[sel.gap] || gaps[1];
      var delay = Math.min(5, gap.min);
      var totalLow = n > 0 ? delay + (n - 1) * gap.min : 0;
      var totalHigh = n > 0 ? delay + (n - 1) * gap.max : 0;
      var clock = hhmm(target.getHours(), target.getMinutes());

      if (timeHint) {
        timeHint.textContent = dayLabel(target) + ' ' + clock + ' 开始（' + countdownText(target) + '）';
      }
      var qs = el.querySelectorAll('[data-quick]');
      for (var qi = 0; qi < qs.length; qi++) {
        if (qs[qi].getAttribute('data-quick') === hhmm(sel.hour, sel.minute)) qs[qi].classList.add('is-active');
        else qs[qi].classList.remove('is-active');
      }

      if (summaryText) {
        if (poolNow.length <= 0) {
          summaryText.textContent = '当前范围里没有可解锁的成就。换一个更大的范围试试。';
        } else {
          var lines = [];
          lines.push('将在 ' + dayLabel(target) + ' ' + clock + ' 开始解锁 ' + n + ' 个成就，每两个之间随机等 ' +
            fmtGap(gap.min) + (gap.max > gap.min ? '到 ' + fmtGap(gap.max) : '') + '，整轮大约 ' +
            (totalHigh === totalLow ? fmtGap(totalLow) : fmtGap(totalLow) + ' – ' + fmtGap(totalHigh)) + '。');
          var pct = (RARE_OPTIONS[sel.rare] || RARE_OPTIONS[0]).pct;
          if (pct > 0) {
            lines.push('候选里的 ' + poolNow.length + ' 个成就都属于全服解锁率低于 ' + pct + '% 的稀有成就。');
          }
          lines.push('到点前请保持本工具和 Steam 都开着；中途可以在标题栏的定时任务里取消，已经解锁的不会回退。');
          summaryText.textContent = lines.join('\n');
        }
      }
      if (okBtn) okBtn.textContent = n > 0 ? '排入定时（' + n + ' 个）' : '排入定时';
    }

    if (hourInput) hourInput.addEventListener('input', syncAll);
    if (hourInput) hourInput.addEventListener('change', syncAll);
    if (minuteInput) minuteInput.addEventListener('input', syncAll);
    if (minuteInput) minuteInput.addEventListener('change', syncAll);

    el.addEventListener('click', function (e) {
      var quick = e.target.closest ? e.target.closest('[data-quick]') : null;
      if (quick) {
        var vs = String(quick.getAttribute('data-quick')).split(':');
        var h = Math.floor(Number(vs[0]));
        var m = Math.floor(Number(vs[1]));
        if (hourInput) hourInput.value = String(isFinite(h) ? h : 0);
        if (minuteInput) minuteInput.value = String(isFinite(m) ? m : 0);
        syncAll();
        return;
      }
      var opt = e.target.closest ? e.target.closest('.opt') : null;
      if (opt) {
        var g = opt.closest('.opt-group');
        if (!g) return;
        var key = g.getAttribute('data-key');
        sel[key] = Number(opt.getAttribute('data-value')) || 0;
        var btns = g.querySelectorAll('.opt');
        for (var i = 0; i < btns.length; i++) {
          if (btns[i] === opt) btns[i].classList.add('is-active');
          else btns[i].classList.remove('is-active');
        }
        if (key === 'rare') {
          sel.count = 0;
          var box = el.querySelector('[data-role="countBox"]');
          if (box) box.innerHTML = optGroupHtml('count', '解锁数量', countOptions(schedPool(list, sel.rare).length), 0);
        }
        syncAll();
        return;
      }
      var hit = e.target.closest ? e.target.closest('[data-role]') : null;
      var role = hit ? hit.getAttribute('data-role') : '';
      if (role === 'cancel') {
        closeModal();
        return;
      }
      if (role === 'ok') {
        var poolNow = schedPool(list, sel.rare);
        if (poolNow.length <= 0) {
          toast('info', '这个范围没有可解锁的成就', '换个更大的范围吧，当前范围里没有满足条件的成就。');
          return;
        }
        var target = targetDate();
        var opts = countOptions(poolNow.length);
        var entry = opts[sel.count] || opts[0];
        var gaps = schedGaps();
        var gap = gaps[sel.gap] || gaps[1];
        var pct = (RARE_OPTIONS[sel.rare] || RARE_OPTIONS[0]).pct;
        submitSchedule({
          appId: state.game.appId,
          gameName: state.game.name || ('App ' + state.game.appId),
          at: Math.floor(target.getTime() / 1000),
          count: entry.value,
          minGap: gap.min,
          maxGap: gap.max,
          rareOnly: pct > 0,
          rarePct: pct > 0 ? pct : RARE_PCT,
          language: state.meta.language || 'english'
        });
      }
    });

    els.modalRoot.innerHTML = '';
    els.modalRoot.appendChild(el);
    setHidden(els.modalRoot, false);
    modalState = { el: el, prevFocus: prevFocus, kind: 'scheduleGame' };
    syncAll();
    window.setTimeout(function () {
      if (okBtn && okBtn.focus) okBtn.focus();
    }, 30);
  }

  /* ---------- 游戏库：多个游戏同一分钟 ---------- */

  function onScheduleBatch() {
    var games = state.picker.games;
    if (!games || games.length === 0) {
      toast('info', '还没有可选的游戏', '等游戏库扫描完成后再试。');
      return;
    }
    openScheduleBatchModal(games);
  }

  function openScheduleBatchModal(games) {
    closeModal();
    var sel = { count: 0, gap: 1, hour: 22, minute: 22 };
    var el = document.createElement('div');
    el.className = 'modal modal--wide';
    el.setAttribute('role', 'dialog');
    el.setAttribute('aria-modal', 'true');

    var body = [];
    body.push('<div class="modal__head">');
    body.push('<span class="modal__ico modal__ico--info">' + ICON.info + '</span>');
    body.push('<h2 class="modal__title">定时批量解锁</h2>');
    body.push('</div>');
    body.push('<p class="modal__text" data-role="summary"></p>');
    body.push('<div class="sch-block">');
    body.push(timeRowHtml(sel.hour, sel.minute));
    body.push('<p class="gap-custom__hint" data-role="timeHint"></p>');
    body.push('</div>');

    body.push('<div class="sch-block">');
    body.push('<div class="sch-block__head">');
    body.push('<span class="opt-group__label">选择游戏（<span data-role="picked">0</span> / ' + games.length + '）</span>');
    body.push('<span class="sch-block__acts">');
    body.push('<button type="button" class="opt" data-role="all">全选</button>');
    body.push('<button type="button" class="opt" data-role="none">清空</button>');
    body.push('</span>');
    body.push('</div>');
    body.push('<div class="sch-games" data-role="games">');
    for (var i = 0; i < games.length; i++) {
      var g = games[i];
      body.push('<label class="sch-game">');
      body.push('<input class="sch-game__box" type="checkbox" data-id="' + num(g.id, 0) + '" />');
      body.push('<span class="sch-game__name">' + esc(g.name || ('App ' + g.id)) + '</span>');
      body.push('<span class="sch-game__meta">' + esc(TYPE_LABEL[g.type] || '') + '</span>');
      body.push('</label>');
    }
    body.push('</div>');
    body.push('</div>');

    body.push(optGroupHtml('count', '每个游戏解锁', SCHEDULE_BATCH_COUNTS, 0));
    body.push(optGroupHtml('gap', '解锁间隔', schedGaps(), 1));

    var parts = [];
    parts.push('<div class="modal__body">' + body.join('') + '</div>');
    parts.push('<div class="modal__foot">');
    parts.push('<button type="button" class="btn btn--ghost" data-role="cancel">取消</button>');
    parts.push('<button type="button" class="btn btn--primary" data-role="ok">排入定时</button>');
    parts.push('</div>');
    el.innerHTML = parts.join('');

    var prevFocus = document.activeElement;
    var summaryText = el.querySelector('[data-role="summary"]');
    var timeHint = el.querySelector('[data-role="timeHint"]');
    var hourInput = el.querySelector('[data-role="hour"]');
    var minuteInput = el.querySelector('[data-role="minute"]');
    var pickedEl = el.querySelector('[data-role="picked"]');
    var gamesBox = el.querySelector('[data-role="games"]');
    var okBtn = el.querySelector('[data-role="ok"]');

    function targetDate() {
      var c = readClock(hourInput, minuteInput);
      sel.hour = c.hour;
      sel.minute = c.minute;
      return nextAt(c.hour, c.minute);
    }

    function pickedIds() {
      var out = [];
      if (!gamesBox) return out;
      var boxes = gamesBox.querySelectorAll('.sch-game__box');
      for (var i = 0; i < boxes.length; i++) {
        if (!boxes[i].checked) continue;
        var id = Number(boxes[i].getAttribute('data-id'));
        if (isFinite(id) && id > 0) out.push(id);
      }
      return out;
    }

    function syncAll() {
      var target = targetDate();
      var ids = pickedIds();
      var gaps = schedGaps();
      var gap = gaps[sel.gap] || gaps[1];
      var countOpt = SCHEDULE_BATCH_COUNTS[sel.count] || SCHEDULE_BATCH_COUNTS[0];
      var clock = hhmm(target.getHours(), target.getMinutes());

      if (pickedEl) pickedEl.textContent = String(ids.length);
      if (timeHint) {
        timeHint.textContent = dayLabel(target) + ' ' + clock + ' 开始（' + countdownText(target) + '）';
      }
      var qs = el.querySelectorAll('[data-quick]');
      for (var qi = 0; qi < qs.length; qi++) {
        if (qs[qi].getAttribute('data-quick') === hhmm(sel.hour, sel.minute)) qs[qi].classList.add('is-active');
        else qs[qi].classList.remove('is-active');
      }
      if (summaryText) {
        if (ids.length === 0) {
          summaryText.textContent = '先在下面选好游戏，再定一个开始时刻。到点后每个游戏都会单独拉起一个解锁进程，互不影响。';
        } else {
          var lines = [];
          lines.push('将在 ' + dayLabel(target) + ' ' + clock + ' 为 ' + ids.length + ' 个游戏开始解锁：每个游戏' +
            (countOpt.value > 0 ? '解锁 ' + countOpt.value + ' 个成就' : '解锁全部剩余成就') + '，' +
            '每两个之间随机等 ' + fmtGap(gap.min) + (gap.max > gap.min ? '到 ' + fmtGap(gap.max) : '') + '。');
          lines.push('到点前请保持本工具和 Steam 都开着；中途可以在标题栏的定时任务里逐个取消，已经解锁的不会回退。');
          summaryText.textContent = lines.join('\n');
        }
      }
      if (okBtn) okBtn.textContent = ids.length > 0 ? '排入定时（' + ids.length + ' 个游戏）' : '排入定时';
    }

    if (hourInput) hourInput.addEventListener('input', syncAll);
    if (hourInput) hourInput.addEventListener('change', syncAll);
    if (minuteInput) minuteInput.addEventListener('input', syncAll);
    if (minuteInput) minuteInput.addEventListener('change', syncAll);
    if (gamesBox) gamesBox.addEventListener('change', syncAll);

    el.addEventListener('click', function (e) {
      var quick = e.target.closest ? e.target.closest('[data-quick]') : null;
      if (quick) {
        var vs = String(quick.getAttribute('data-quick')).split(':');
        var h = Math.floor(Number(vs[0]));
        var m = Math.floor(Number(vs[1]));
        if (hourInput) hourInput.value = String(isFinite(h) ? h : 0);
        if (minuteInput) minuteInput.value = String(isFinite(m) ? m : 0);
        syncAll();
        return;
      }
      var roleHit = e.target.closest ? e.target.closest('[data-role]') : null;
      var role = roleHit ? roleHit.getAttribute('data-role') : '';
      if (role === 'cancel') {
        closeModal();
        return;
      }
      if (role === 'all' || role === 'none') {
        if (gamesBox) {
          var boxes = gamesBox.querySelectorAll('.sch-game__box');
          for (var i = 0; i < boxes.length; i++) boxes[i].checked = (role === 'all');
        }
        syncAll();
        return;
      }
      var opt = e.target.closest ? e.target.closest('.opt') : null;
      if (opt) {
        var g = opt.closest('.opt-group');
        if (!g) return;
        var key = g.getAttribute('data-key');
        sel[key] = Number(opt.getAttribute('data-value')) || 0;
        var btns = g.querySelectorAll('.opt');
        for (var bi = 0; bi < btns.length; bi++) {
          if (btns[bi] === opt) btns[bi].classList.add('is-active');
          else btns[bi].classList.remove('is-active');
        }
        syncAll();
        return;
      }
      if (role === 'ok') {
        var ids = pickedIds();
        if (ids.length === 0) {
          toast('warn', '还没有选游戏', '先在列表里勾选至少一个游戏。');
          return;
        }
        var target = targetDate();
        var gaps = schedGaps();
        var gap = gaps[sel.gap] || gaps[1];
        var countOpt = SCHEDULE_BATCH_COUNTS[sel.count] || SCHEDULE_BATCH_COUNTS[0];
        var payload = {
          at: Math.floor(target.getTime() / 1000),
          minGap: gap.min,
          maxGap: gap.max,
          language: state.meta.language || 'english',
          games: []
        };
        for (var gi = 0; gi < ids.length; gi++) {
          var gid = ids[gi];
          var nm = 'App ' + gid;
          for (var gj = 0; gj < games.length; gj++) {
            if (num(games[gj].id, 0) === gid) { nm = games[gj].name || nm; break; }
          }
          payload.games.push({ appId: gid, gameName: nm, count: countOpt.value, rareOnly: false, rarePct: RARE_PCT });
        }
        closeModal();
        if (state.mock) {
          var now = payload.at;
          for (var mi = 0; mi < payload.games.length; mi++) {
            var mg = payload.games[mi];
            state.schedule.jobs = state.schedule.jobs.concat([{
              id: 'mock-' + String(Date.now()) + '-' + String(mi),
              appId: mg.appId,
              gameName: mg.gameName,
              at: now,
              count: mg.count,
              minGap: payload.minGap,
              maxGap: payload.maxGap
            }]);
          }
          renderScheduleChip();
          renderStatus();
          toast('success', '已排入定时解锁',
            payload.games.length + ' 个游戏将在 ' + clockLabel(new Date(now * 1000)) + ' 开始解锁。');
          return;
        }
        send('schedule/createBatch', payload);
      }
    });

    els.modalRoot.innerHTML = '';
    els.modalRoot.appendChild(el);
    setHidden(els.modalRoot, false);
    modalState = { el: el, prevFocus: prevFocus, kind: 'scheduleBatch' };
    syncAll();
    window.setTimeout(function () {
      if (okBtn && okBtn.focus) okBtn.focus();
    }, 30);
  }

  /* ---------------------------------------------------------------------------
     11. Mock 数据（离线预览用）
     --------------------------------------------------------------------------- */

  var MOCK_GAMES = [
    { id: 1091500, name: '赛博朋克 2077', type: 'normal', capsule: 'https://cdn.cloudflare.steamstatic.com/steam/apps/1091500/header.jpg' },
    { id: 1245620, name: '艾尔登法环', type: 'normal', capsule: 'https://cdn.cloudflare.steamstatic.com/steam/apps/1245620/header.jpg' },
    { id: 292030, name: 'The Witcher 3: Wild Hunt', type: 'normal', capsule: 'https://cdn.cloudflare.steamstatic.com/steam/apps/292030/header.jpg' },
    { id: 730, name: 'Counter-Strike 2', type: 'normal', capsule: 'https://cdn.cloudflare.steamstatic.com/steam/apps/730/header.jpg' },
    { id: 570, name: 'Dota 2', type: 'normal', capsule: 'https://cdn.cloudflare.steamstatic.com/steam/apps/570/header.jpg' },
    { id: 413150, name: '星露谷物语', type: 'normal', capsule: 'https://cdn.cloudflare.steamstatic.com/steam/apps/413150/header.jpg' },
    { id: 1145360, name: 'Hades', type: 'normal', capsule: 'https://cdn.cloudflare.steamstatic.com/steam/apps/1145360/header.jpg' },
    { id: 367520, name: 'Hollow Knight', type: 'normal', capsule: 'https://cdn.cloudflare.steamstatic.com/steam/apps/367520/header.jpg' },
    { id: 814380, name: '只狼：影逝二度', type: 'normal', capsule: 'https://cdn.cloudflare.steamstatic.com/steam/apps/814380/header.jpg' },
    { id: 1174180, name: 'Red Dead Redemption 2', type: 'normal', capsule: 'https://cdn.cloudflare.steamstatic.com/steam/apps/1174180/header.jpg' },
    { id: 620, name: 'Portal 2', type: 'normal', capsule: null },
    { id: 892970, name: 'Valheim', type: 'normal', capsule: 'https://cdn.cloudflare.steamstatic.com/steam/apps/892970/header.jpg' },
    { id: 105600, name: 'Terraria', type: 'normal', capsule: null },
    { id: 480, name: 'Spacewar 演示客户端', type: 'demo', capsule: null },
    { id: 900001, name: 'Black Mesa: 民间重制版', type: 'mod', capsule: null },
    { id: 900002, name: 'Third Age: 全面战争模组', type: 'mod', capsule: null },
    { id: 228980, name: 'Steamworks 公共测试程序', type: 'junk', capsule: null }
  ];

  var ACH_POOL = [
    { name: '初次启程', desc: '完成新手教程并踏上旅途。' },
    { name: '初露锋芒', desc: '完成你的第一个任务。' },
    { name: '万事开头难', desc: '第一次成功击败精英敌人。' },
    { name: '老练的旅人', desc: '累计游玩满 10 小时。' },
    { name: '收集癖', desc: '收集全部稀有等级的素材。' },
    { name: '完美主义者', desc: '在单次游玩中达成全部次要目标。' },
    { name: '速通达人', desc: '在 2 小时以内完成主线剧情。' },
    { name: '无伤通关', desc: '全程不受任何伤害完成一个章节。' },
    { name: '一击必杀', desc: '用单次攻击击败一名首领。' },
    { name: '百人斩', desc: '累计击败 100 名敌人。' },
    { name: '千锤百炼', desc: '把任意装备强化至满级。' },
    { name: '富可敌国', desc: '持有金币一度超过 100 万。' },
    { name: 'Speedrunner', desc: 'Finish the campaign in under 90 minutes.' },
    { name: 'Completionist', desc: 'Unlock every other achievement in the game.' },
    { name: 'Untouchable', desc: 'Win a match without taking any damage.' },
    { name: 'First Blood', desc: 'Score the first kill of a competitive match.' },
    { name: 'Master Craftsman', desc: 'Craft every item from the artisan recipe book.' },
    { name: 'Explorer', desc: 'Discover every region on the world map.' },
    { name: '探索者', desc: '发现地图上所有未标记的隐藏区域。' },
    { name: '全图鉴完成', desc: '把图鉴中的每一种生物都记录一次。' },
    { name: '隐藏结局', desc: '在不被任何人察觉的情况下完成终章。' },
    { name: '真·结局', desc: '解锁全部剧情分支并看到真正的终幕。' },
    { name: '团队协作', desc: '与三名队友共同完成一次团队挑战。' },
    { name: '孤狼', desc: '独自一人完成四人难度的副本。' },
    { name: '和平主义者', desc: '全程不击杀任何敌人完成一个章节。' },
    { name: '以彼之道', desc: '用反弹的方式击败一名远程敌人。' },
    { name: '万事通', desc: '把全部技能树都点上至少一点。' },
    { name: '夜间行动', desc: '在夜间时段完成十个委托任务。' },
    { name: '黎明之前', desc: '在游戏内一天之内完成主线第一章。' },
    { name: '深海恐惧', desc: '下潜到 200 米以下的深渊并安全返回。' },
    { name: '高塔攀登者', desc: '登上世界最高处的瞭望塔。' },
    { name: '徽章收藏家', desc: '收集全部 24 枚活动徽章。' },
    { name: '装备全满', desc: '同时装备三件传说级物品。' },
    { name: '满级', desc: '将角色等级提升到上限。' },
    { name: '第一次死亡', desc: '嗯……每个人都会经历一次。' },
    { name: '死亡一百次', desc: '累计死亡 100 次，依然没有放弃。' },
    { name: '一命通关', desc: '在最高难度下不死亡完成主线。' },
    { name: '幸运一击', desc: '以 1% 的暴击概率完成致命一击。' },
    { name: '神之手', desc: '连续 50 次攻击不落空。' },
    { name: '传说级装备', desc: '获得任意一件传说品质的武器。' },
    { name: '商店大亨', desc: '在商店中累计消费超过 50 万金币。' },
    { name: '谈判专家', desc: '通过对话说服十名敌对 NPC。' },
    { name: '一诺千金', desc: '完成全部支线委托，一个都不要落下。' },
    { name: '声名远播', desc: '让你的名声达到最高等级。' },
    { name: 'Nemesis', desc: 'Defeat your rival in single combat.' },
    { name: 'Speed Demon', desc: 'Reach top speed on every track.' },
    { name: 'Iron Will', desc: 'Survive 30 waves without leaving the arena.' },
    { name: 'Legend of the Fall', desc: 'Complete the legendary difficulty campaign.' }
  ];

  var STAT_POOL = [
    { name: '游戏时长（小时）', isFloat: true, min: 0, max: 99999, incrementOnly: false, protect: false },
    { name: '总击杀数', isFloat: false, min: 0, max: 999999, incrementOnly: true, protect: false },
    { name: '死亡次数', isFloat: false, min: 0, max: 99999, incrementOnly: true, protect: false },
    { name: '完成任务数', isFloat: false, min: 0, max: 9999, incrementOnly: false, protect: false },
    { name: '累计收集物品', isFloat: false, min: 0, max: 99999, incrementOnly: true, protect: false },
    { name: '最高连击', isFloat: false, min: 0, max: 9999, incrementOnly: true, protect: false },
    { name: '累计造成伤害', isFloat: false, min: 0, max: 999999999, incrementOnly: true, protect: false },
    { name: '移动距离（公里）', isFloat: true, min: 0, max: 999999, incrementOnly: true, protect: false },
    { name: '建造数量', isFloat: false, min: 0, max: 999999, incrementOnly: false, protect: false },
    { name: '金币余额', isFloat: false, min: 0, max: 99999999, incrementOnly: false, protect: false },
    { name: '排名分数', isFloat: false, min: 0, max: 9999999, incrementOnly: false, protect: true },
    { name: '胜场', isFloat: false, min: 0, max: 99999, incrementOnly: true, protect: false },
    { name: '败场', isFloat: false, min: 0, max: 99999, incrementOnly: true, protect: false },
    { name: '通关次数', isFloat: false, min: 0, max: 999, incrementOnly: true, protect: false },
    { name: '技能使用次数', isFloat: false, min: 0, max: 999999, incrementOnly: true, protect: false },
    { name: '成就点数', isFloat: false, min: 0, max: 10000, incrementOnly: true, protect: true },
    { name: '已探索区域', isFloat: false, min: 0, max: 500, incrementOnly: false, protect: false },
    { name: '交易次数', isFloat: false, min: 0, max: 99999, incrementOnly: false, protect: true }
  ];

  var mockCache = {};

  function buildMockGames() {
    return MOCK_GAMES.map(function (g) {
      return { id: g.id, name: g.name, type: g.type, capsule: g.capsule };
    });
  }

  function capsuleForAppId(appId) {
    var list = state.picker.games;
    var key = Number(appId);
    for (var i = 0; i < list.length; i++) {
      if (Number(list[i].id) === key) return list[i].capsule;
    }
    return null;
  }

  function mockPayloadFor(appId) {
    var id = Number(appId);
    if (mockCache[id]) return mockCache[id];
    var rng = makeRng(hash32('sam-' + id) || 7);
    var nameMap = {};
    var i;
    for (i = 0; i < MOCK_GAMES.length; i++) nameMap[MOCK_GAMES[i].id] = MOCK_GAMES[i].name;

    var achCount = 40 + Math.floor(rng() * 9);
    var achievements = [];
    var used = {};
    for (i = 0; i < achCount; i++) {
      var pick = Math.floor(rng() * ACH_POOL.length);
      var guard = 0;
      while (used[pick] && guard < 200) { pick = (pick + 1) % ACH_POOL.length; guard++; }
      used[pick] = true;
      var src = ACH_POOL[pick];
      var achieved = rng() < 0.42;
      var base = (id * 13 + i * 977) % 100000;
      achievements.push({
        id: 'ACH_' + String(base).padStart(5, '0') + '_' + i,
        name: src.name,
        desc: src.desc,
        icon: null,
        iconLocked: null,
        achieved: achieved,
        unlockTime: achieved ? Math.floor(Date.now() / 1000) - Math.floor(rng() * 30000000) : null,
        hidden: rng() < 0.08,
        protect: rng() < 0.06,
        // 演示数据：约三分之一落到稀有区间，方便验证「稀有」筛选与「仅解锁稀有成就」
        rarity: (function () {
          var roll = rng();
          if (roll < 0.06) return Math.round(rng() * 90) / 100 + 0.05;
          if (roll < 0.18) return Math.round((1 + rng() * 4) * 10) / 10;
          if (roll < 0.34) return Math.round((5 + rng() * 5) * 10) / 10;
          return Math.round((10 + rng() * 60) * 10) / 10;
        })()
      });
    }

    var statCount = 15 + Math.floor(rng() * 4);
    var stats = [];
    var usedStat = {};
    for (i = 0; i < statCount; i++) {
      var sp = i % STAT_POOL.length;
      var g2 = 0;
      while (usedStat[sp] && g2 < 60) { sp = (sp + 1) % STAT_POOL.length; g2++; }
      usedStat[sp] = true;
      var s = STAT_POOL[sp];
      var raw = s.min + (s.max - s.min) * rng();
      var value = s.isFloat ? Math.round(raw * 100) / 100 : Math.round(raw);
      stats.push({
        id: 'STAT_' + String((id + sp * 31) % 100000).padStart(5, '0'),
        name: s.name,
        value: value,
        isFloat: s.isFloat,
        min: s.min,
        max: s.max,
        incrementOnly: s.incrementOnly,
        protect: s.protect
      });
    }

    var payload = {
      appId: id,
      name: nameMap[id] || '未知游戏',
      achievements: achievements,
      stats: stats,
      summary: {
        total: achievements.length,
        unlocked: achievements.filter(function (a) { return a.achieved; }).length
      }
    };
    mockCache[id] = payload;
    return payload;
  }

  function mockOpenGame(appId) {
    var payload = mockPayloadFor(appId);
    state.game.capsule = capsuleForAppId(appId);
    state.game.loading = false;
    onGame({
      ev: 'game',
      appId: payload.appId,
      name: payload.name,
      achievements: payload.achievements,
      stats: payload.stats,
      summary: payload.summary
    });
  }

  function mockPersist(payload) {
    mockCache[payload.appId] = payload;
  }

  function mockStore(changes) {
    var g = state.game;
    var i;
    for (i = 0; i < g.achievements.length; i++) {
      var a = g.achievements[i];
      if (own(changes.achievements, a.id)) {
        a.achieved = !!changes.achievements[a.id];
        a.unlockTime = a.achieved ? Math.floor(Date.now() / 1000) : null;
      }
    }
    for (i = 0; i < g.stats.length; i++) {
      var s = g.stats[i];
      if (own(changes.stats, s.id)) s.value = Number(changes.stats[s.id]);
    }
    state.saving = true;
    renderSaveButton();
    window.setTimeout(function () {
      var payload = mockPayloadFor(g.appId);
      payload.achievements = g.achievements.map(function (x) { return x; });
      payload.stats = g.stats.map(function (x) { return x; });
      payload.summary = {
        total: g.achievements.length,
        unlocked: g.achievements.filter(function (x) { return x.achieved; }).length
      };
      mockPersist(payload);
      onStored({
        ev: 'stored',
        achievements: Object.keys(changes.achievements).length,
        stats: Object.keys(changes.stats).length
      });
      mockOpenGame(g.appId);
    }, 520);
  }

  function mockBatch(kind) {
    var list = state.game.achievements;
    var i;
    for (i = 0; i < list.length; i++) {
      if (kind === 'unlockAll') {
        list[i].achieved = true;
        list[i].unlockTime = list[i].unlockTime || Math.floor(Date.now() / 1000);
      } else if (kind === 'lockAll') {
        list[i].achieved = false;
        list[i].unlockTime = null;
      } else {
        list[i].achieved = !list[i].achieved;
        list[i].unlockTime = list[i].achieved
          ? (list[i].unlockTime || Math.floor(Date.now() / 1000))
          : null;
      }
    }
    var label = kind === 'unlockAll' ? '已全部解锁' : (kind === 'lockAll' ? '已全部锁定' : '已反选全部成就');
    toast('success', label, '演示模式下立即生效，未写入 Steam。');
    mockOpenGame(state.game.appId);
  }

  function mockReset(clearAchievements) {
    var g = state.game;
    var i;
    for (i = 0; i < g.stats.length; i++) {
      if (!g.stats[i].protect) g.stats[i].value = g.stats[i].min;
    }
    if (clearAchievements) {
      for (i = 0; i < g.achievements.length; i++) {
        g.achievements[i].achieved = false;
        g.achievements[i].unlockTime = null;
      }
    }
    var payload = mockPayloadFor(g.appId);
    payload.achievements = g.achievements;
    payload.stats = g.stats;
    payload.summary = {
      total: g.achievements.length,
      unlocked: g.achievements.filter(function (x) { return x.achieved; }).length
    };
    mockPersist(payload);
    toast('success', '重置完成', clearAchievements ? '统计与成就状态已清零（演示）。' : '统计数值已清零（演示）。');
    mockOpenGame(g.appId);
  }

  function enterMock() {
    if (state.mock) {
      render();
      return;
    }
    state.mock = true;
    state.booted = true;
    state.mode = 'picker';
    state.meta = { appId: 0, gameName: '', steamId: '76561198000000000', userName: '演示账号', language: 'zh-CN' };
    state.picker.games = buildMockGames();
    state.picker.total = state.picker.games.length;
    state.picker.loading = false;
    state.status.text = '演示模式：未检测到主机，正在使用内置数据';
    state.status.busy = false;
    render();
    toast('info', '已进入演示模式', '未检测到 C# 主机，界面使用内置假数据渲染。');
  }

  /* ---------------------------------------------------------------------------
     12. 事件绑定
     --------------------------------------------------------------------------- */

  function cacheEls() {
    els.brandSub = document.getElementById('brandSub');
    els.brandMark = document.querySelector('.brand__mark');
    els.mockChip = document.getElementById('mockChip');
    els.userChip = document.getElementById('userChip');
    els.scheduleChip = document.getElementById('scheduleChip');
    els.scheduleChipText = document.getElementById('scheduleChipText');
    els.btnMin = document.getElementById('btnMin');
    els.btnMax = document.getElementById('btnMax');
    els.btnClose = document.getElementById('btnClose');
    els.toolbar = document.querySelector('.toolbar');
    els.tbPicker = document.getElementById('tbPicker');
    els.tbGame = document.getElementById('tbGame');
    els.pickerSearch = document.getElementById('pickerSearch');
    els.pickerSearchClear = document.getElementById('pickerSearchClear');
    els.pickerTypeSeg = document.getElementById('pickerTypeSeg');
    els.btnScheduleAll = document.getElementById('btnScheduleAll');
    els.btnReload = document.getElementById('btnReload');
    els.gameTabs = document.getElementById('gameTabs');
    els.achSearchWrap = document.getElementById('achSearchWrap');
    els.achSearch = document.getElementById('achSearch');
    els.achSearchClear = document.getElementById('achSearchClear');
    els.achFilterSeg = document.getElementById('achFilterSeg');
    els.achActions = document.getElementById('achActions');
    els.btnSave = document.getElementById('btnSave');
    els.saveSpinner = document.getElementById('saveSpinner');
    els.saveLabel = document.getElementById('saveLabel');
    els.saveBadge = document.getElementById('saveBadge');
    els.viewPicker = document.getElementById('viewPicker');
    els.viewGame = document.getElementById('viewGame');
    els.gameGrid = document.getElementById('gameGrid');
    els.pickerSkeleton = document.getElementById('pickerSkeleton');
    els.pickerEmpty = document.getElementById('pickerEmpty');
    els.pickerEmptyTitle = document.getElementById('pickerEmptyTitle');
    els.pickerEmptyText = document.getElementById('pickerEmptyText');
    els.btnBack = document.getElementById('btnBack');
    els.sideCover = document.getElementById('sideCover');
    els.sideTitle = document.getElementById('sideTitle');
    els.sideMeta = document.getElementById('sideMeta');
    els.sideFacts = document.getElementById('sideFacts');
    els.progUnlocked = document.getElementById('progUnlocked');
    els.progTotal = document.getElementById('progTotal');
    els.progPct = document.getElementById('progPct');
    els.progRest = document.getElementById('progRest');
    els.progFill = document.getElementById('progFill');
    els.btnRefresh = document.getElementById('btnRefresh');
    els.btnReset = document.getElementById('btnReset');
    els.btnRandom = document.getElementById('btnRandom');
    els.randomLabel = document.getElementById('randomLabel');
    els.btnSchedule = document.getElementById('btnSchedule');
    els.unlockBar = document.getElementById('unlockBar');
    els.unlockBarText = document.getElementById('unlockBarText');
    els.unlockBarFill = document.getElementById('unlockBarFill');
    els.btnUnlockStop = document.getElementById('btnUnlockStop');
    els.achGrid = document.getElementById('achGrid');
    els.achEmpty = document.getElementById('achEmpty');
    els.achEmptyTitle = document.getElementById('achEmptyTitle');
    els.achEmptyText = document.getElementById('achEmptyText');
    els.statWrap = document.getElementById('statWrap');
    els.statBody = document.getElementById('statBody');
    els.gameLoading = document.getElementById('gameLoading');
    els.statusText = document.getElementById('statusText');
    els.statusSpinner = document.getElementById('statusSpinner');
    els.statusMeta = document.getElementById('statusMeta');
    els.toasts = document.getElementById('toasts');
    els.modalRoot = document.getElementById('modalRoot');
  }

  /* 标题栏右上角的「最大化 / 还原」图标随窗口状态切换：主机最大化后会把状态推过来。 */
  var WIN_ICON_MAX = '<svg viewBox="0 0 10 10" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"><rect x="2.4" y="2.4" width="5.2" height="5.2" rx="1.1" /></svg>';
  var WIN_ICON_RESTORE = '<svg viewBox="0 0 10 10" fill="none" stroke="currentColor" stroke-width="1.2" stroke-linejoin="round"><rect x="2.1" y="3.7" width="4.2" height="4.2" rx="1" /><path d="M3.9 3.7v-.6a1 1 0 0 1 1-1h2.9a1 1 0 0 1 1 1v2.9a1 1 0 0 1-1 1h-.6" /></svg>';

  function applyWindowState(maximized) {
    if (!els.btnMax) return;
    var next = maximized ? WIN_ICON_RESTORE : WIN_ICON_MAX;
    if (els.btnMax.getAttribute('data-maximized') === (maximized ? '1' : '0')) return;
    els.btnMax.setAttribute('data-maximized', maximized ? '1' : '0');
    els.btnMax.innerHTML = next;
    els.btnMax.title = maximized ? '还原' : '最大化';
    els.btnMax.setAttribute('aria-label', maximized ? '还原' : '最大化');
  }

  function bindWindowControls() {
    if (els.btnMin) els.btnMin.addEventListener('click', function () { send('win/minimize'); });
    if (els.btnMax) els.btnMax.addEventListener('click', function () { send('win/maximize'); });
    if (els.btnClose) els.btnClose.addEventListener('click', function () { send('win/close'); });
    if (els.scheduleChip) els.scheduleChip.addEventListener('click', openScheduleListModal);

    var bar = document.getElementById('titlebar');
    if (bar) {
      // 按下时不立刻进入系统拖动：一旦 ReleaseCapture + WM_NCLBUTTONDOWN，鼠标就被拖动循环独占，
      // 页面收不到第二次点击，双击标题栏最大化会失效。改成移动超过阈值才发起拖动，
      // 单击/双击都留在页面里，拖动的手感几乎没有差别。
      var dragOrigin = null;
      bar.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return;
        if (e.target.closest && e.target.closest('.winbtns')) return;
        dragOrigin = { x: e.screenX, y: e.screenY };
      });
      window.addEventListener('mousemove', function (e) {
        if (dragOrigin === null) return;
        if (Math.abs(e.screenX - dragOrigin.x) < 3 && Math.abs(e.screenY - dragOrigin.y) < 3) return;
        dragOrigin = null;
        send('win/drag');
      });
      window.addEventListener('mouseup', function () { dragOrigin = null; });
      window.addEventListener('blur', function () { dragOrigin = null; });
      bar.addEventListener('dblclick', function (e) {
        if (e.target.closest && e.target.closest('.winbtns')) return;
        send('win/maximize');
      });
    }
    if (els.brandMark) {
      els.brandMark.addEventListener('click', function () {
        if (state.mode !== 'picker') goBackToPicker();
      });
    }
  }

  /* 返回游戏库：演示模式直接切视图，真实模式让主进程换回 picker 进程。 */
  function goBackToPicker() {
    if (bodyBusyFor('back')) return;
    if (state.mock) {
      state.mode = 'picker';
      state.game = { appId: 0, name: '', capsule: null, capsules: null, achievements: [], stats: [], summary: { total: 0, unlocked: 0 }, loading: false };
      state.ui = { tab: 'achievements', query: '', filter: 'all' };
      state.pending = { ach: {}, stats: {} };
      render();
      return;
    }
    state.status.text = '正在返回游戏库…';
    state.status.busy = true;
    renderStatus();
    send('game/back');
  }

  function bodyBusyFor(kind) {
    if (kind === 'back' && state.saving) {
      toast('warn', '正在保存', '请等保存完成后再返回游戏库。');
      return true;
    }
    return false;
  }

  function bindPicker() {
    if (els.pickerSearch) {
      var queuePickerRender = debounce(function () {
        renderPicker();
        renderStatus();
      }, 120);
      els.pickerSearch.addEventListener('input', function () {
        state.picker.query = els.pickerSearch.value;
        setHidden(els.pickerSearchClear, state.picker.query.length === 0);
        queuePickerRender();
      });
      els.pickerSearch.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') {
          els.pickerSearch.value = '';
          state.picker.query = '';
          setHidden(els.pickerSearchClear, true);
          renderPicker();
          renderStatus();
        } else if (e.key === 'Enter') {
          queuePickerRender.cancel();
          renderPicker();
          renderStatus();
        }
      });
    }
    if (els.pickerSearchClear) {
      els.pickerSearchClear.addEventListener('click', function () {
        els.pickerSearch.value = '';
        state.picker.query = '';
        setHidden(els.pickerSearchClear, true);
        if (queuePickerRender) queuePickerRender.cancel();
        renderPicker();
        renderStatus();
        els.pickerSearch.focus();
      });
    }
    if (els.pickerTypeSeg) {
      els.pickerTypeSeg.addEventListener('click', function (e) {
        var btn = e.target.closest ? e.target.closest('.seg__item') : null;
        if (!btn) return;
        var type = btn.getAttribute('data-type');
        if (!type || type === state.picker.type) return;
        state.picker.type = type;
        setActive(els.pickerTypeSeg, 'data-type', type);
        renderPicker();
        renderStatus();
      });
    }
    if (els.btnScheduleAll) els.btnScheduleAll.addEventListener('click', onScheduleBatch);
    if (els.btnReload) els.btnReload.addEventListener('click', onReload);
    if (els.gameGrid) {
      els.gameGrid.addEventListener('click', function (e) {
        var card = e.target.closest ? e.target.closest('.game-card') : null;
        if (!card) return;
        var raw = card.getAttribute('data-id');
        var id = Number(raw);
        if (!isFinite(id)) return;
        if (state.mock) {
          mockOpenGame(id);
          return;
        }
        send('game/open', { id: id });
        state.status.text = '正在打开《' + (card.getAttribute('title') || '游戏') + '》…';
        state.status.busy = true;
        renderStatus();
      });
    }
  }

  function bindGame() {
    if (els.btnBack) {
      els.btnBack.addEventListener('click', goBackToPicker);
    }
    if (els.gameTabs) {
      els.gameTabs.addEventListener('click', function (e) {
        var btn = e.target.closest ? e.target.closest('.seg__item') : null;
        if (!btn) return;
        var tab = btn.getAttribute('data-tab');
        if (!tab || tab === state.ui.tab) return;
        state.ui.tab = tab;
        renderToolbar();
        renderGame();
      });
    }
    if (els.achSearch) {
      var queueAchRender = debounce(function () {
        renderGame();
      }, 120);
      els.achSearch.addEventListener('input', function () {
        state.ui.query = els.achSearch.value;
        setHidden(els.achSearchClear, state.ui.query.length === 0);
        queueAchRender();
      });
      els.achSearch.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') {
          els.achSearch.value = '';
          state.ui.query = '';
          setHidden(els.achSearchClear, true);
          queueAchRender.cancel();
          renderGame();
        } else if (e.key === 'Enter') {
          queueAchRender.cancel();
          renderGame();
        }
      });
    }
    if (els.achSearchClear) {
      els.achSearchClear.addEventListener('click', function () {
        els.achSearch.value = '';
        state.ui.query = '';
        setHidden(els.achSearchClear, true);
        if (queueAchRender) queueAchRender.cancel();
        renderGame();
        els.achSearch.focus();
      });
    }
    if (els.achFilterSeg) {
      els.achFilterSeg.addEventListener('click', function (e) {
        var btn = e.target.closest ? e.target.closest('.seg__item') : null;
        if (!btn) return;
        var filter = btn.getAttribute('data-filter');
        if (!filter || filter === state.ui.filter) return;
        state.ui.filter = filter;
        setActive(els.achFilterSeg, 'data-filter', filter);
        renderGame();
      });
    }
    if (els.achActions) {
      els.achActions.addEventListener('click', function (e) {
        var btn = e.target.closest ? e.target.closest('[data-act]') : null;
        if (!btn) return;
        var act = btn.getAttribute('data-act');
        if (act === 'unlockAll' || act === 'lockAll' || act === 'invertAll') onBatch(act);
      });
    }
    if (els.achGrid) {
      els.achGrid.addEventListener('click', function (e) {
        var card = e.target.closest ? e.target.closest('.ach') : null;
        if (!card) return;
        var id = card.getAttribute('data-id');
        if (id) toggleAch(id);
      });
    }
    if (els.btnSave) els.btnSave.addEventListener('click', onSave);
    if (els.btnRefresh) els.btnRefresh.addEventListener('click', onRefreshGame);
    if (els.btnReset) els.btnReset.addEventListener('click', onReset);
    if (els.btnRandom) els.btnRandom.addEventListener('click', onRandomUnlock);
    if (els.btnSchedule) els.btnSchedule.addEventListener('click', onScheduleGame);
    if (els.btnUnlockStop) els.btnUnlockStop.addEventListener('click', onStopRandomUnlock);

    if (els.statBody) {
      els.statBody.addEventListener('input', function (e) {
        commitStatInput(e.target, false);
      });
      els.statBody.addEventListener('change', function (e) {
        commitStatInput(e.target, true);
      });
      els.statBody.addEventListener('blur', function (e) {
        commitStatInput(e.target, true);
      }, true);
      els.statBody.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && e.target && e.target.classList.contains('stat-input')) {
          commitStatInput(e.target, true);
        }
      });
    }
  }

  function bindModal() {
    if (!els.modalRoot) return;
    els.modalRoot.addEventListener('mousedown', function (e) {
      if (e.target === els.modalRoot) closeModal();
    });
  }

  function bindImageFallback() {
    document.addEventListener('load', function (e) {
      var img = e.target;
      if (img && img.tagName === 'IMG') img.classList.add('is-loaded');
    }, true);
    document.addEventListener('error', function (e) {
      var img = e.target;
      if (!img || img.tagName !== 'IMG') return;
      var holder = img.parentElement;
      if (!holder) return;

      /* 先按候选列表换下一个地址，全部失败才退回字母封面。 */
      var caps = img.getAttribute('data-caps');
      if (caps) {
        var list = caps.split('|');
        var idx = parseInt(img.getAttribute('data-caps-idx') || '0', 10);
        if (isNaN(idx)) idx = 0;
        for (var i = idx + 1; i < list.length; i++) {
          if (!list[i]) continue;
          img.setAttribute('data-caps-idx', String(i));
          img.classList.remove('is-loaded');
          img.src = list[i];
          return;
        }
        img.removeAttribute('data-caps');
      }

      var mono = holder.getAttribute('data-mono');
      if (!mono) {
        var card = holder.closest ? holder.closest('.ach') : null;
        var nameEl = card ? card.querySelector('.ach__name') : null;
        mono = monogram(nameEl ? nameEl.textContent : '?');
      }
      if (holder.classList.contains('ach__icon')) {
        holder.innerHTML = fallbackIcon(mono);
      } else {
        holder.innerHTML = fallbackCover(mono, hash32(mono) % 300);
      }
    }, true);
  }

  function bindKeys() {
    document.addEventListener('keydown', function (e) {
      var mod = e.ctrlKey || e.metaKey;
      if (e.key === 'Escape') {
        if (modalState) {
          e.preventDefault();
          closeModal();
          return;
        }
        if (state.mode === 'game' && state.game.unlock && state.game.unlock.active) {
          e.preventDefault();
          onStopRandomUnlock();
          return;
        }
        if (state.mode === 'picker' && state.picker.query) {
          state.picker.query = '';
          if (els.pickerSearch) els.pickerSearch.value = '';
          setHidden(els.pickerSearchClear, true);
          renderPicker();
          renderStatus();
        } else if (state.mode === 'game' && state.ui.query) {
          state.ui.query = '';
          if (els.achSearch) els.achSearch.value = '';
          setHidden(els.achSearchClear, true);
          renderGame();
        } else if (state.mode === 'game') {
          e.preventDefault();
          goBackToPicker();
        }
        return;
      }
      if (mod && (e.key === 's' || e.key === 'S')) {
        e.preventDefault();
        if (state.mode === 'game') onSave();
        return;
      }
      if (mod && (e.key === 'f' || e.key === 'F')) {
        e.preventDefault();
        if (state.mode === 'picker' && els.pickerSearch) els.pickerSearch.focus();
        else if (state.mode === 'game' && els.achSearch) els.achSearch.focus();
      }
    });
  }

  /* ---------------------------------------------------------------------------
     13. 启动
     --------------------------------------------------------------------------- */

  function parseMaybeJson(data) {
    if (typeof data === 'string') {
      try {
        return JSON.parse(data);
      } catch (err) {
        return null;
      }
    }
    return data;
  }

  function boot() {
    loadProgress();
    cacheEls();
    bindWindowControls();
    bindPicker();
    bindGame();
    bindModal();
    bindImageFallback();
    bindKeys();

    // 游戏页是另一个进程：它写进 localStorage 的进度，游戏库这边要等窗口被唤回
    // 才看得到。这里在窗口重新获得焦点时对比一次，只有真的变了才重画卡片。
    window.addEventListener('focus', function () {
      if (state.mode !== 'picker') return;
      if (loadProgress()) renderPicker();
    });

    var bridge = getBridge();
    if (bridge && typeof bridge.addEventListener === 'function') {
      bridge.addEventListener('message', function (e) {
        handleEvent(parseMaybeJson(e && e.data));
      });
    }

    if (!hasBridge()) {
      enterMock();
      return;
    }

    state.booted = true;
    state.mode = 'picker';
    state.picker.loading = true;
    state.status.text = '正在等待主机握手…';
    state.status.busy = true;
    render();
    window.setTimeout(function () {
      if (!handshakeSeen && state.picker.games.length === 0) {
        state.status.text = '已连接主机，等待数据…';
        state.status.busy = true;
        renderStatus();
      }
    }, 2500);
  }

  window.SAM = {
    version: '1.9.0',
    handleEvent: handleEvent,
    mock: enterMock,
    getState: function () {
      return {
        mode: state.mode,
        mock: state.mock,
        games: state.picker.games.length,
        achievements: state.game.achievements.length,
        stats: state.game.stats.length,
        dirty: dirtyCount()
      };
    }
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
