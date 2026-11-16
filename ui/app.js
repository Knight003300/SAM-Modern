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
  var SVG_LOCK = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><rect x="3.4" y="7" width="9.2" height="6.4" rx="1.7"/><path d="M5.7 7V5.3a2.3 2.3 0 0 1 4.6 0V7"/></svg>';

  var TYPE_LABEL = { normal: '正式版', demo: '试玩', mod: '模组', junk: '其他' };
  var RING_C = 2 * Math.PI * 52;

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
    saving: false
  };

  var els = {};
  var modalState = null;
  var saveGuard = 0;
  var handshakeSeen = false;

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

  function fmtTime(ts) {
    var sec = Number(ts);
    if (!sec || !isFinite(sec) || sec <= 0) return '';
    var d = new Date(sec * 1000);
    return d.getFullYear() + '-' + pad2(d.getMonth() + 1) + '-' + pad2(d.getDate()) +
      ' ' + pad2(d.getHours()) + ':' + pad2(d.getMinutes());
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

  function monoStyle(size) {
    return 'font-size:' + size + 'px;font-weight:800;letter-spacing:-1.5px;line-height:1;' +
      'background:linear-gradient(135deg,#7c5cff,#4aa8ff);-webkit-background-clip:text;' +
      'background-clip:text;color:transparent;opacity:.92;';
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
      case 'mock':
        if (!state.mock) enterMock();
        break;
      default:
        console.warn('[SAM] 未识别的事件：' + name, ev);
    }
  }

  function onReady(ev) {
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
      state.game.loading = true;
      state.status.text = '正在读取成就与统计数据…';
      state.status.busy = true;
    } else {
      state.picker.loading = true;
      state.status.text = '正在扫描游戏库…';
      state.status.busy = true;
    }
    render();
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
    state.picker.loading = false;
    state.status.busy = false;
    state.status.text = '扫描完成，共找到 ' + state.picker.total + ' 个游戏';
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
      protect: !!(a && a.protect)
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
    state.game.loading = false;
    state.pending = { ach: {}, stats: {} };
    state.saving = false;
    window.clearTimeout(saveGuard);
    state.status.busy = false;
    state.status.text = '已载入《' + state.game.name + '》：' +
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
    setHidden(els.saveBadge, dirty === 0);
    els.saveBadge.textContent = String(dirty);
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

  function gameCardHtml(g, index) {
    var mono = monogram(g.name);
    var hue = hash32('' + g.id) % 300;
    var delay = Math.min(index * 22, 400);
    var out = [];
    out.push('<button type="button" class="game-card" data-id="' + esc(g.id) + '"');
    out.push(' style="animation-delay:' + delay + 'ms"');
    out.push(' title="' + esc(g.name) + '">');
    out.push('<span class="game-card__cover" data-mono="' + esc(mono) + '">');
    var cover = coverImgHtml(g, true);
    out.push(cover || fallbackCover(mono, hue));
    out.push('</span>');
    out.push('<span class="game-card__tags">' + typeTagHtml(g.type) + '</span>');
    out.push('<span class="game-card__name">' + esc(g.name) + '</span>');
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

  function achMatches(a, on) {
    var filter = state.ui.filter;
    if (filter === 'unlocked' && !on) return false;
    if (filter === 'locked' && on) return false;
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
    if (a.protect) {
      out.push('<span class="tag tag--protect">' + SVG_LOCK + '受保护</span>');
    }
    if (a.hidden) {
      out.push('<span class="tag tag--hidden">隐藏</span>');
    }
    if (out.length === 0) {
      out.push('<span class="tag">未解锁</span>');
    }
    return out.join('');
  }

  function achCardHtml(a, index) {
    var on = desiredAch(a);
    var classes = 'ach ' + (on ? 'is-on' : 'is-locked');
    var desc = (a.hidden && !on) ? '隐藏成就：解锁前无法查看描述。' : (a.desc || '暂无描述');
    var delay = Math.min(index * 14, 260);
    var out = [];
    out.push('<button type="button" class="' + classes + '" data-id="' + esc(a.id) + '"');
    out.push(' style="animation-delay:' + delay + 'ms"');
    out.push(' aria-pressed="' + (on ? 'true' : 'false') + '">');
    out.push('<span class="ach__icon">' + achIconHtml(a, on) + '</span>');
    out.push('<span class="ach__body">');
    out.push('<span class="ach__name" title="' + esc(a.name) + '">' + esc(a.name) + '</span>');
    out.push('<span class="ach__desc" title="' + esc(desc) + '">' + esc(desc) + '</span>');
    out.push('<span class="ach__foot">' + achTagsHtml(a, on) + '</span>');
    out.push('</span>');
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
    els.ringUnlocked.textContent = String(p.unlocked);
    els.ringTotal.textContent = String(p.total);
    els.ringPct.textContent = (pct % 1 === 0 ? String(Math.round(pct)) : pct.toFixed(1)) + '%';
    els.ringFill.setAttribute('stroke-dasharray', RING_C + ' ' + RING_C);
    els.ringFill.setAttribute('stroke-dashoffset', String(RING_C * (1 - ratio)));

    var d = dirtyCount();
    els.sideFacts.innerHTML = [
      '<div><dt>成就进度</dt><dd>' + p.unlocked + ' / ' + p.total + '</dd></div>',
      '<div><dt>统计项目</dt><dd>' + state.game.stats.length + ' 项</dd></div>',
      '<div><dt>待保存改动</dt><dd>' + (d > 0 ? d + ' 处' : '无') + '</dd></div>'
    ].join('');
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
        els.achEmptyText.textContent = '换个关键词，或切换「全部 / 已解锁 / 未解锁」。';
      }
    } else {
      els.achGrid.innerHTML = list.map(achCardHtml).join('');
    }
    markLoadedImages();
    renderSideFacts();
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
    parts.push('<div class="modal__head">');
    parts.push('<span class="modal__ico' + (danger ? '' : ' modal__ico--info') + '">' +
      (danger ? ICON.warn : ICON.info) + '</span>');
    parts.push('<h2 class="modal__title">' + esc(o.title || '提示') + '</h2>');
    parts.push('</div>');
    if (o.text) parts.push('<p class="modal__text">' + esc(o.text) + '</p>');
    if (o.checkboxLabel) {
      parts.push('<label style="display:flex;align-items:center;gap:8px;font-size:12.5px;' +
        'color:#a6b0c3;cursor:pointer;user-select:none;">' +
        '<input type="checkbox" id="samModalCheck" style="width:15px;height:15px;accent-color:#7c5cff;cursor:pointer;" />' +
        '<span>' + esc(o.checkboxLabel) + '</span></label>');
    }
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

    window.setTimeout(function () {
      if (okBtn && okBtn.focus) okBtn.focus();
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
    openRandomModal(pool.length);
  }

  function openRandomModal(available) {
    closeModal();
    var sel = { count: 0, gap: 1 };
    var el = document.createElement('div');
    el.className = 'modal';
    el.setAttribute('role', 'dialog');
    el.setAttribute('aria-modal', 'true');

    var parts = [];
    parts.push('<div class="modal__head">');
    parts.push('<span class="modal__ico modal__ico--info">' + ICON.info + '</span>');
    parts.push('<h2 class="modal__title">随机时间解锁</h2>');
    parts.push('</div>');
    parts.push('<p class="modal__text">Steam 只认“解锁那一刻”的真实时间，不接受伪造的历史时间。' +
      '所以这里按随机间隔逐个写入，每个成就自然拿到不同的解锁时间。' +
      '当前有 ' + available + ' 个可解锁成就。解锁期间请保持窗口打开。</p>');

    function groupHtml(key, label, items) {
      var h = ['<div class="opt-group" data-key="' + key + '">'];
      h.push('<span class="opt-group__label">' + esc(label) + '</span>');
      h.push('<div class="opt-row">');
      for (var i = 0; i < items.length; i++) {
        h.push('<button type="button" class="opt' + (i === sel[key] ? ' is-active' : '') +
          '" data-value="' + i + '">' + esc(items[i].label) + '</button>');
      }
      h.push('</div></div>');
      return h.join('');
    }

    var counts = countOptions(available);

    parts.push(groupHtml('count', '解锁数量', counts));
    parts.push(groupHtml('gap', '解锁间隔', RANDOM_GAPS));

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

    parts.push('<div class="gap-custom" data-role="customGap" hidden>');
    parts.push(gapRowHtml('最短', 'gapMin', 30));
    parts.push(gapRowHtml('最长', 'gapMax', 90));
    parts.push('<p class="gap-custom__hint" data-role="gapHint"></p>');
    parts.push('</div>');

    parts.push('<div class="modal__foot">');
    parts.push('<button type="button" class="btn btn--ghost" data-role="cancel">取消</button>');
    parts.push('<button type="button" class="btn btn--primary" data-role="ok">开始解锁</button>');
    parts.push('</div>');
    el.innerHTML = parts.join('');

    var prevFocus = document.activeElement;

    var customBox = el.querySelector('[data-role="customGap"]');
    var gapHint = el.querySelector('[data-role="gapHint"]');
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
      return { minGap: lo, maxGap: hi, gapMid: Math.round((lo + hi) / 2) };
    }

    function syncCustom() {
      if (!customBox) return;
      var isCustom = !!(RANDOM_GAPS[sel.gap] && RANDOM_GAPS[sel.gap].custom);
      setHidden(customBox, !isCustom);
      if (!isCustom) return;
      var lo = readCustom();
      if (gapHint) {
        var picked = counts[sel.count] || counts[0];
        var n = picked.value > 0 ? Math.min(picked.value, available) : available;
        gapHint.textContent = '每个成就之间随机等 ' + fmtGap(lo.minGap) + ' – ' + fmtGap(lo.maxGap) +
          '；共 ' + n + ' 个成就，整轮大约需要 ' + fmtGap(n * lo.gapMid) + '。';
      }
    }

    if (customBox) {
      customBox.addEventListener('input', syncCustom);
      customBox.addEventListener('change', syncCustom);
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
        if (key === 'gap' || key === 'count') syncCustom();
        return;
      }
      var hit = e.target.closest ? e.target.closest('[data-role]') : null;
      var role = hit ? hit.getAttribute('data-role') : '';
      if (role === 'cancel') {
        closeModal();
        return;
      }
      if (role === 'ok') {
        var gap = RANDOM_GAPS[sel.gap] || RANDOM_GAPS[1];
        var entry = counts[sel.count] || counts[0];
        var lo = gap.custom ? readCustom().minGap : gap.min;
        var hi = gap.custom ? readCustom().maxGap : gap.max;
        startRandomUnlock({ count: entry.value, minGap: lo, maxGap: hi, delay: Math.min(5, lo) });
      }
    });

    els.modalRoot.innerHTML = '';
    els.modalRoot.appendChild(el);
    setHidden(els.modalRoot, false);
    modalState = { el: el, prevFocus: prevFocus };
    syncCustom();
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
      delay: cfg.delay
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
        protect: rng() < 0.06
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
    els.btnMin = document.getElementById('btnMin');
    els.btnMax = document.getElementById('btnMax');
    els.btnClose = document.getElementById('btnClose');
    els.toolbar = document.querySelector('.toolbar');
    els.tbPicker = document.getElementById('tbPicker');
    els.tbGame = document.getElementById('tbGame');
    els.pickerSearch = document.getElementById('pickerSearch');
    els.pickerSearchClear = document.getElementById('pickerSearchClear');
    els.pickerTypeSeg = document.getElementById('pickerTypeSeg');
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
    els.ringFill = document.getElementById('ringFill');
    els.ringUnlocked = document.getElementById('ringUnlocked');
    els.ringTotal = document.getElementById('ringTotal');
    els.ringPct = document.getElementById('ringPct');
    els.btnRefresh = document.getElementById('btnRefresh');
    els.btnReset = document.getElementById('btnReset');
    els.btnRandom = document.getElementById('btnRandom');
    els.randomLabel = document.getElementById('randomLabel');
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

  function bindWindowControls() {
    if (els.btnMin) els.btnMin.addEventListener('click', function () { send('win/minimize'); });
    if (els.btnMax) els.btnMax.addEventListener('click', function () { send('win/maximize'); });
    if (els.btnClose) els.btnClose.addEventListener('click', function () { send('win/close'); });

    var bar = document.getElementById('titlebar');
    if (bar) {
      bar.addEventListener('mousedown', function (e) {
        if (e.button !== 0) return;
        if (e.target.closest && e.target.closest('.winbtns')) return;
        send('win/drag');
      });
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
      els.pickerSearch.addEventListener('input', function () {
        state.picker.query = els.pickerSearch.value;
        setHidden(els.pickerSearchClear, state.picker.query.length === 0);
        renderPicker();
        renderStatus();
      });
      els.pickerSearch.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') {
          els.pickerSearch.value = '';
          state.picker.query = '';
          setHidden(els.pickerSearchClear, true);
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
      els.achSearch.addEventListener('input', function () {
        state.ui.query = els.achSearch.value;
        setHidden(els.achSearchClear, state.ui.query.length === 0);
        renderGame();
      });
      els.achSearch.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') {
          els.achSearch.value = '';
          state.ui.query = '';
          setHidden(els.achSearchClear, true);
          renderGame();
        }
      });
    }
    if (els.achSearchClear) {
      els.achSearchClear.addEventListener('click', function () {
        els.achSearch.value = '';
        state.ui.query = '';
        setHidden(els.achSearchClear, true);
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
    cacheEls();
    bindWindowControls();
    bindPicker();
    bindGame();
    bindModal();
    bindImageFallback();
    bindKeys();

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
    version: '1.0.0',
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
