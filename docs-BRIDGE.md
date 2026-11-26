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
- `{ ev:"ready", mode:"picker"|"game", appId, gameName, steamId, userName, language, cachedGames?, cachedGame? }`
  启动完成时发一次。`mode` 决定显示哪个界面。
  带 `cachedGames` / `cachedGame` 时，页面必须把这份上次的快照**和 ready 一起画完**再回 `ui/painted`：拆成两条消息会先渲染出「0 个成就 / 骨架屏」，那一两帧就是切换时的闪烁。C# 随后仍会发一次真实数据覆盖。
- `{ ev:"status", text:"...", busy:true|false }` 状态栏文案 + 是否转圈。
- `{ ev:"toast", kind:"info"|"success"|"error"|"warn", title:"...", text:"..." }` 右上角浮层提示（自动消失）。
- `{ ev:"error", title:"...", text:"..." }` 需用户确认的错误（模态）。
- `{ ev:"appInfo", version:"1.9.9", releasesUrl:"https://..." }` 启动完成（`ui/ready` 处理完之后）发一次。版本号只有 C# 的 `AppInfo.Version` 这一份，页面「关于」直接显示它，**不要在 JS 里再写一遍**；`releasesUrl` 是发布页兜底地址。
- `{ ev:"updateResult", current:"1.9.9", ok:boolean, failure?, tag?, name?, url?, body?, newer? }` 对应 `app/checkUpdate` 的回包。`ok=false` 时只有 `failure`（可直接展示的原因文案）；`ok=true` 时 `tag`/`name`/`url`/`body` 来自 GitHub 最新 release，`newer` 是 C# 逐段比较版本号的结果（远程解析不出来时一律 `false`，当作没有新版）。
- `{ ev:"window", maximized:boolean }` 窗口最大化状态变化（按钮、双击标题栏、Win+↑/↓、拖到屏幕顶部触发 Aero Snap 都会发）。UI 据此把最大化按钮的图标与 `title` 在「最大化 / 还原」之间切换。`ui/ready` 之后也会补发一次当前状态。

### Picker（选游戏界面）
- `{ ev:"games", games:[{ id:number, name:string, type:"normal"|"demo"|"mod"|"junk", capsule:string|null }], total:number, cached:boolean }`
  `capsule` 是完整 https 图片 URL（可能为 null → 显示占位图）。
  `cached=true` 表示这是上次扫描的缓存快照（先上屏），后台扫描完成后会再发一次 `cached=false` 的完整列表。
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

### 定时解锁（两种模式都可用）
- `{ ev:"scheduleList", jobs:[{ id, appId, gameName, at, count, minGap, maxGap, rareOnly, rarePct, order, achievements?, done?, total? }] }`
  当前排队的定时任务（按 `at` 升序）。`at` 是 Unix 秒。`done/total` 只在任务已开始执行时出现。
  任务签名（id 拼接）没变时不会重发，所以 UI 侧必须缓存上一次的列表，不能假设每次事件都是全量刷新之外的增量。
- `{ ev:"scheduleStarted"|"scheduleDone"|"scheduleMissed", job:{...}, result:{ ok, done, total, message } }`
  任务开始执行 / 执行结束 / 已经错过（到点后一小时内没能跑上，任务会被删掉）。UI 据此增删列表并弹 toast。
- `{ ev:"schedulePool", appId, cached:boolean, achievements:[{ id, name, rarity }] }`
  对应 `schedule/pool` 的回包。`cached=false` 表示没有本地快照（该游戏还没在游戏页打开过）；`rarity` 为 -1 表示 Steam 没给出百分比。

## 4. 命令表（JS → C#）
窗口控制（两种模式都可用）：
- `{cmd:"win/minimize"}` / `{cmd:"win/maximize"}` / `{cmd:"win/close"}` / `{cmd:"win/drag"}`
  `win/maximize` 是**切换**（Normal ↔ Maximized），不是单向最大化。
  `win/drag` 是「开始拖动窗口」：C# 收到后 `ReleaseCapture()` + `WM_NCLBUTTONDOWN(HtCaption)`，把鼠标交给系统拖动循环。**一旦发出，页面就收不到后续的 mouseup / dblclick**，所以必须在指针移动超过阈值（约 3px）时才发，不能按下即发，否则双击标题栏最大化会失效。

生命周期上报（**必须实现**，否则窗口不会显示）：
- `{cmd:"ui/ready"}`  页面 `load` 完成时发一次，C# 据此推数据。
- `{cmd:"ui/painted", layoutMs:number, imageMs:number}`  首帧真正画完（含布局与可见图片解码）时发一次。C# 收到它才把窗口从屏幕外挪进来，保证用户看不到空壳或半截渲染。
- `{cmd:"ui/error", kind, message, source, stack}`  页面异常回传，C# 只记日志（`sam-modern.log` 里是 `ui <kind>:`）。

应用级（两种模式都可用，命令在 `WebShell` 基类里）：
- `{cmd:"app/checkUpdate"}`  后台线程请求 GitHub `releases/latest`（12 秒超时），结果回 `updateResult` 事件。检查期间状态栏显示「正在检查更新…」。失败不弹错误框。
- `{cmd:"app/openUrl", url:string}`  用系统默认浏览器打开链接。**只放行 `http://` / `https://`**，其它协议一律拒绝并 toast 提示。

Picker：
- `{cmd:"picker/reload"}`  重新扫描游戏库
- `{cmd:"game/open", id:number}`  打开某个游戏的成就管理窗口

Game：
- `{cmd:"game/refresh"}`  重新拉取成就/统计数据
- `{cmd:"game/store", achievements:{[id]:boolean}, stats:{[id]:number}}`  保存改动（只含被修改项）
- `{cmd:"game/reset", resetAchievements:boolean}`  重置所有统计（危险操作，UI 需二次确认）
- `{cmd:"game/unlockAll"}` / `{cmd:"game/lockAll"}` / `{cmd:"game/invertAll"}`  批量勾选（由 C# 返回新的 game 事件）
- `{cmd:"game/unlockRandom", count:number, minGap:number, maxGap:number, delay:number, rareOnly?:boolean, rarePct?:number, order?:number, achievements?:string[]}`
  按随机间隔逐个真实解锁：`count=0` 表示全部剩余，`minGap/maxGap` 是间隔秒数区间，`delay` 是第一个成就前的等待秒数。
  `rareOnly=true` 时只挑全服解锁率低于 `rarePct`（默认 5，上限 100）的成就；候选不足会走「没有可解锁的稀有成就」提示。
  `order` 决定候选的排队顺序：`0`=随机（默认）、`1`=按全服解锁率从高到低（常见先）、`2`=从低到高（稀有先）；读不到百分比的成就排最后。
  `achievements` 非空时按这个显式列表解锁（忽略 `count`/`rareOnly`/`order`，顺序就是数组顺序）。
  每解锁一个都会立刻 `StoreStats()`，Steam 因此为每个成就记下各自的真实时间。进度通过 `unlockRun` 事件推送。
- `{cmd:"game/unlockRandom/cancel"}`  中止进行中的随机解锁（已解锁的不回滚）

定时解锁（命令在 `WebShell` 基类里，所以游戏库与游戏窗口都能发）：
- `{cmd:"schedule/create", appId:number, gameName:string, at:number(Unix秒), count:number, minGap:number, maxGap:number, rareOnly?:boolean, rarePct?:number, order?:number, jitter?:number, language?:string, achievements?:string[]}`
  排一条定时解锁任务，写入 `<程序目录>\schedule\job-<id>.json`。`count=0` 表示「全部剩余」；`achievements` 非空时按这个显式列表解锁（忽略 `count`/`rareOnly`/`order`）。
  `order` 同 `game/unlockRandom`：`0`=随机（默认）、`1`=常见先、`2`=稀有先。
  `at` 必须晚于当前时间 2 秒、且不超过一年；`minGap>=0`（`0` 与 `maxGap=0` 表示「立刻解锁」，到点后不等直接连续写入）、`maxGap>=minGap`、`maxGap<=7 天`、`rarePct` 0–100、`jitter` 0–3600 秒，越界值由 C# 侧夹紧。
- `{cmd:"schedule/createBatch", at:number, minGap:number, maxGap:number, order?:number, jitter?:number, language?:string, games:[{ appId, gameName, count, rareOnly?, rarePct?, achievements? }]}`
  一次排多条任务（多游戏同一秒开始）。公共字段会补进每个 game 项（只在缺键时补），逐条走与 `schedule/create` 相同的校验与落盘。
- `{cmd:"schedule/cancel", id:string}`  删掉一条任务（同时删 job / claim / progress / result 文件）
- `{cmd:"schedule/run", id:string}`  把某条任务的时刻改成「现在」并立刻拉起解锁进程（任务已进入准备中 / 解锁中时拒绝）
- `{cmd:"schedule/pool", appId:number}`  读 `<程序目录>\cache\game-<appId>.json` 的成就快照，回一帧 `schedulePool`（不连 Steam，所以游戏库窗口也能同步用）

定时任务的执行是**独立隐藏进程**：调度器在 `at - 40s` 时 `Process.Start(自身 exe, "--run-job <job 文件>")`，`UseShellExecute=false`、`CreateNoWindow=true`，该进程不建窗口、不初始化 WebView2，只做 `SetAchievement` + `StoreStats` 并写回 `result-<id>.json`。因此**程序退出后任务不会执行**。

## 5. Mock 模式
页面在 `chrome.webview` 不存在、或收到 `{ev:"mock"}` 时，用内置假数据渲染（≥12 个游戏、≥40 个成就、≥15 个统计），保证可以离线预览视觉效果。

## 6. 硬性要求
- 简体中文界面文案（成就名/描述来自 Steam 数据，可能是中文或英文，原样显示）。
- 克制的深色中性风格：四级灰阶背景 + 三级文字色，唯一强调色 `#4d8dff`，破坏性操作用 `#ff6b5e`；不用彩色色相区分类别，不用渐变/玻璃拟态/发光阴影。无外部字体/CDN 依赖（只用系统字体）。
- 单页应用，`ui/index.html` + `ui/app.css` + `ui/app.js`，纯原生 JS，无构建步骤。
- 自定义标题栏（frameless），最小化/最大化/关闭按钮放右上角，标题栏可拖动；双击标题栏切换最大化。
- 窗口虽然无边框，但必须保留系统级缩放/贴边能力：Win+↑/↓、拖到屏幕顶部 Aero Snap、拖动边缘改变大小都要能用（宿主侧通过 `WS_THICKFRAME` + `WM_GETMINMAXINFO` + `WM_NCCALCSIZE` 实现，最大化区域等于显示器工作区、不压任务栏）。
- 窗口边缘不能出现系统缩放边框：`WS_THICKFRAME` 会让 Windows 在非客户区画一圈 8px 浅色边框（深色系统下是 `#F2F5FC`），而无边框窗的 8px `Padding` 环正好把它露出来，表现为切换窗口时四边闪白框。宿主必须接管 `WM_NCPAINT` / `WM_NCACTIVATE`（直接不画），并用 `DWMWA_USE_IMMERSIVE_DARK_MODE` + `DWMWA_BORDER_COLOR` 把 DWM 边框压到界面底色；`WM_ERASEBKGND` 一律用 `BackColor` 填充，避免首帧浅色闪块。
- 成就行列表、搜索框、筛选（全部/已解锁/未解锁/稀有）、全选/反选按钮、数字进度条、统计表格可编辑。
- 视觉 token 固定：间距只用 4/8/12/16/24/32/48，圆角只有 8px 一档，字号 4 档（24/15/13/11），字重只有 400/600。
- 键盘可用（Esc 关闭弹窗，Ctrl+S 保存，Ctrl+F 聚焦搜索）。

## 7. 窗口接力与待命（C# 侧，供排查用）
`steamclient.dll` 在一个进程里只能绑定一个 appId，所以换游戏必须换进程。接力流程：
1. 旧进程 `Process.Start` 新进程，参数里带 `--hidden --ready <就绪文件> --bounds x,y,w,h [--max] [--parent-pid <旧进程PID>]`；
2. 新进程隐藏启动（窗口摆在 `-32000,-32000`），建好 WebView2、页面画完首帧后显示窗口，并写就绪文件（先写 `.tmp` 再改名）；
3. 旧进程每 15ms 轮询该文件，看到后：游戏库窗口 → `Hide()` 进入待命；游戏窗口 → 直接关闭。
4. 游戏窗口返回游戏库时，写 `<程序目录>\relay\sam-modern-resume-<父PID>.txt`（内容 `x,y,w,h[|max]`，或 `quit` 表示连游戏库一起退出），待命的游戏库窗口轮询到后 `Show()` 出来并回写 `.ack`，游戏窗口收到 ack 后关闭；8 秒无 ack 则退回「起一个新的游戏库进程」。
5. 游戏窗口没走唤回流程就退出（崩溃 / 被任务管理器结束）时，待命窗口检测到子进程消失后自己 `Show()` 出来。

排查日志关键字：`relay launch` / `ready file written` / `relay ready` / `standby: hiding picker window` / `resume parent pid=` / `standby: resumed by child` / `standby: child exited without resume` / `WebView2 init timing:` / `ui reported painted` / `reveal timing:`。
