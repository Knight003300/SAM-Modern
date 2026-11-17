# SAM Modern — UI ⇄ C# 通信协议（唯一契约）

页面由 C# 主进程用 WebView2 加载，地址 `https://sam.ui/index.html`（虚拟主机映射到磁盘 ui 目录）。
网络在 WebView2 里做（图片直接用 <img> 加载 CDN）；C# 只提供 Steam API 数据。

## 1. JS → C#（发送命令）
```js
window.chrome.webview.postMessage({ cmd: "...", ...payload });
```
外部浏览器里没有 `chrome.webview` 时，页面必须自动进入 mock 模式（不报错）。

## 2. C# → JS（事件）
```js
window.chrome.webview.addEventListener('message', e => handle(e.data));
```
页面必须暴露 `window.SAM.handleEvent(ev)`（C# 也允许直接 ExecuteScript 调它）。

## 3. 事件表（C# → JS）

### 通用
- `{ ev:"ready", mode:"picker"|"game", appId, gameName, steamId, userName, language }`
  启动完成时发一次。`mode` 决定显示哪个界面。
- `{ ev:"status", text:"...", busy:true|false }` 状态栏文案 + 是否转圈。
- `{ ev:"toast", kind:"info"|"success"|"error"|"warn", title:"...", text:"..." }` 右上角浮层提示（自动消失）。
- `{ ev:"error", title:"...", text:"..." }` 需用户确认的错误（模态）。

### Picker（选游戏界面）
- `{ ev:"games", games:[{ id:number, name:string, type:"normal"|"demo"|"mod"|"junk", capsule:string|null }], total:number }`
  `capsule` 是完整 https 图片 URL（可能为 null → 显示占位图）。
- `{ ev:"gamesProgress", scanning:number }` 扫描进度（可选，用于加载态文案）。

### Game（成就管理界面）
- `{ ev:"game", appId, name, achievements:[...], stats:[...], summary:{ total:number, unlocked:number } }`
  achievement: `{ id, name, desc, icon:string|null, iconLocked:string|null, achieved:boolean, unlockTime:number|null(Unix秒), hidden:boolean, protect:boolean, rarity:number }`
  `rarity` 是全服解锁率百分比（0–100），读不到时为 `-1`（UI 视为未知，不判稀有）。
  stat: `{ id, name, value:number, isFloat:boolean, min:number, max:number, incrementOnly:boolean, protect:boolean }`
  `protect=true` 表示受保护（permission&3!=0 或 permission&2!=0），UI 必须禁用编辑。
- `{ ev:"stored", achievements:number, stats:number }` 保存成功（C# 随后会再发一次 game 事件刷新）。
- `{ ev:"unlockRun", active:boolean, done:number, total:number, next:number, id?:string, unlockTime?:number, finished?:boolean, cancelled?:boolean }`
  「随机时间解锁」进度事件。`active=true` 表示进行中：`done/total` 是已解锁数量，`next` 是距下一个成就的秒数（0 表示已结束），带 `id` 时表示该成就刚刚解锁、`unlockTime` 为读回的 Unix 秒。
  结束时会再发一帧 `active:false`，并用 `finished:true` 或 `cancelled:true` 区分正常完成与用户中止。

## 4. 命令表（JS → C#）
窗口控制（两种模式都可用）：
- `{cmd:"win/minimize"}` / `{cmd:"win/maximize"}` / `{cmd:"win/close"}` / `{cmd:"win/drag"}`（标题栏按下时发，C# 接管拖动）

Picker：
- `{cmd:"picker/reload"}`  重新扫描游戏库
- `{cmd:"game/open", id:number}`  打开某个游戏的成就管理窗口

Game：
- `{cmd:"game/refresh"}`  重新拉取成就/统计数据
- `{cmd:"game/store", achievements:{[id]:boolean}, stats:{[id]:number}}`  保存改动（只含被修改项）
- `{cmd:"game/reset", resetAchievements:boolean}`  重置所有统计（危险操作，UI 需二次确认）
- `{cmd:"game/unlockAll"}` / `{cmd:"game/lockAll"}` / `{cmd:"game/invertAll"}`  批量勾选（由 C# 返回新的 game 事件）
- `{cmd:"game/unlockRandom", count:number, minGap:number, maxGap:number, delay:number, rareOnly?:boolean, rarePct?:number}`
  按随机间隔逐个真实解锁：`count=0` 表示全部剩余，`minGap/maxGap` 是间隔秒数区间，`delay` 是第一个成就前的等待秒数。
  `rareOnly=true` 时只挑全服解锁率低于 `rarePct`（默认 5，上限 100）的成就；候选不足会走「没有可解锁的稀有成就」提示。
  每解锁一个都会立刻 `StoreStats()`，Steam 因此为每个成就记下各自的真实时间。进度通过 `unlockRun` 事件推送。
- `{cmd:"game/unlockRandom/cancel"}`  中止进行中的随机解锁（已解锁的不回滚）

## 5. Mock 模式
页面在 `chrome.webview` 不存在、或收到 `{ev:"mock"}` 时，用内置假数据渲染（≥12 个游戏、≥40 个成就、≥15 个统计），保证可以离线预览视觉效果。

## 6. 硬性要求
- 简体中文界面文案（成就名/描述来自 Steam 数据，可能是中文或英文，原样显示）。
- 克制的深色现代风格，单一系统蓝强调色；无外部字体/CDN 依赖（只用系统字体）。
- 单页应用，`ui/index.html` + `ui/app.css` + `ui/app.js`，纯原生 JS，无构建步骤。
- 自定义标题栏（frameless），最小化/最大化/关闭按钮放右上角，标题栏可拖动。
- 成就卡片网格、搜索框、筛选（全部/已解锁/未解锁/稀有）、全选/反选按钮、进度环、统计表格可编辑。
- 键盘可用（Esc 关闭弹窗，Ctrl+S 保存，Ctrl+F 聚焦搜索）。
