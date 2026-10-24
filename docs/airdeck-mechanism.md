# AirDeck 运行机制拆解（证据版）

> 样本：`C:\Users\asus\AppData\Local\Programs\AirDeck`，版本 1.2.3.0
> 证据来源：`AirDeck.runtimeconfig.json`、`AirDeck.dll` 字符串（ASCII 6.1MB / UTF-16 3.7MB 导出）、
> `airdeck.log`（355 KB）与 `airdeck.log.previous`（4.19 MB）、运行中进程 PID 29444 的窗口枚举与样式读取。
> 标注：**【实测】**= 本机当场测到；**【日志】**= 它自己写下的运行记录；**【字符串】**= 二进制里读到的符号；**【推断】**= 由前几项合理外推（未直接验证）。

---

## 0. 一句话结论

AirDeck 不是"一个 WPF 小工具"，而是一套**桌面层服务**：

```
单实例 WPF 进程
 ├─ 全屏层窗口（启动台/雨层/搜索）
 ├─ 分层停靠栏窗口（TOOLWINDOW + NOACTIVATE + LAYERED）
 ├─ shell 挂钩 + 寄存器广播（explorer 重启自愈、任务栏闪烁感知）
 ├─ 帧驱动动画引擎（plate/slot 生命周期 + 缓动 + FPS 自测）
 ├─ 分页懒加载的图标系统（128px，StartMenu/Desktop/UWP 三源）
 └─ 授权/更新/埋点/自愈伴生进程（TaskbarRecovery.exe）
```

它把"性能"当成一等公民：日志里几乎每个交互都带毫秒级耗时与掉帧统计。

---

## 1. 交付形态与进程模型

| 项 | 实测/证据 |
|---|---|
| 运行时 | **.NET 8.0.31** self-contained（`Microsoft.NETCore.App` + `Microsoft.WindowsDesktop.App` 随包）**【实测】** |
| 主程序 | `AirDeck.exe` 512.5 KB（启动壳）+ `AirDeck.dll` **76.32 MB**（全部代码、XAML/BAML、资源）**【实测】** |
| 依赖库 | 仅 `CommunityToolkit.Mvvm 8.4.2`、`Microsoft.Web.WebView2 1.0.2651.64`（含 Core/Wpf/WinForms）、`Microsoft.Windows.SDK.NET`（WinRT 投影）**【实测】** |
| 伴生进程 | `AirDeck.TaskbarRecovery.exe` / `.dll`（极小）**【实测】** + `Tools\PinToTaskbar.ps1` |
| 常驻内存 | 运行中 22 线程 / **73.2 MB** 工作集**【实测】** |
| 自启 | `HKCU\...\Run\AirDeck = "...\AirDeck.exe" --startup` **【实测】** |
| 安装器 | Inno Setup（`unins000.exe`）**【实测】** |
| 语言 | 13 套 `Resources`（cs/de/es/fr/it/ja/ko/pl/pt-BR/ru/tr/zh-Hans/zh-Hant）**【实测】** |
| 单实例 | `SingleInstanceService` **【字符串】** |
| 日志 | `airdeck.log` 滚动到 `.previous`；结构化标签 + 帧级诊断 **【实测】** |

**它自己的日志标签分布**（今天日志 1385+455+34… 条）：`DBG/DOCK-PILE`、`ICON-DIAG`、`DRIVEN`、`ICON-PAGE`、`RESIDENT`、`SWIPE-FPS`、`PAGE-PREPARE`、`FIELD-FPS`、`WARM`、`SHEET-CLICK`、`PRESS-T`、`CLOSE-T`、`OPEN-T`、`THEME`、`STEAM`、`RESCAN`、`LICENSE`、`WECHAT-ATTENTION`、`SYSTEM-TRAY`、`FIRST-OPEN-PRESENT`、`STARTUP-READY`、`DOCK-FROST`、`Aptabase`。

---

## 2. 窗口与图层模型（当场枚举 PID 29444）

| 窗口 | 尺寸/位置 | 扩展样式 | 说明 |
|---|---|---|---|
| `AirDeck` | 1707×1067 @0,0 | `TOOLWINDOW`（`st=0x06080000`：CLIPSIBLINGS+CLIPCHILDREN，无 LAYERED） | **全屏层**（启动台/雨层/搜索遮罩），不占任务栏与 Alt+Tab **【实测】** |
| `AirDeck Desktop Dock` | 1691×175 @8,884 | `TOOLWINDOW NOACTIVATE LAYERED CLIPSIBLINGS CLIPCHILDREN`（`ex=0x08080080`） | **停靠栏**：每像素透明 + 永不抢焦点 **【实测】** |
| `Hidden Window` ×2 | 1280×781 @177,177 / @228,228 | `WINDOWEDGE`（`st=0x04CF0000` 带标题框） | WPF 的隐藏宿主，被上面两个窗口设为 **owner**（避免出现任务栏按钮）**【实测】** |
| 无类名小窗 ×2 | 135×37 | `WINDOWEDGE` | 弹出层/提示宿主 **【实测】** |
| `SystemResourceNotifyWindow` / `MediaContextNotificationWindow` / `.NET-BroadcastEventWindow` | 0×0 | — | WPF 内部通知窗 **【实测】** |
| `CiceroUIWndFrame` / `MSCTFIME UI` / `IME` | — | — | 输入法（TIP）宿主 **【实测】** |

> 枚举时这 6 个窗口的 `IsWindowVisible` 全为 `False`（`st` 里没有 `WS_VISIBLE`），说明此刻 AirDeck 的停靠栏与启动台都是**隐藏态**——它靠显示/隐藏整体窗口切换，而不是移出屏幕。

关键发现：

1. **不预留屏幕边缘**：`SPI_GETWORKAREA` 返回 `0,0,1707,1067`＝整屏（任务栏本就是自动隐藏）。虽然二进制里引用了 `SHAppBarMessage` 与 `AppBarData` **【字符串】**，但运行态没有把工作区吃掉 → **它属于"覆盖式"停靠，不是"独占边缘"式** **【实测+推断】**。
2. **启动就 cloaked**：`[STARTUP-READY] cost=442ms cloaked=True sceneCompleted=True` **【日志】** — 先建好窗口与场景再解除隐藏，避免白屏/闪一下。
3. **Resident 层**：`[RESIDENT] desktop owner attach=False success=True owner=66894`、`[RESIDENT] compositor protection=False peek=0x0 transitions=0x0` **【日志】**，以及 `ResidentDesktopWindow` / `ResidentRootDesktopWindow` / `NeedsNativeDesktopRecovery` **【字符串】** — 它会尝试把窗口挂到桌面 owner 之下（RocketDock 式"桌面级"窗口），并盯着 peek/transitions 这类系统层窗口句柄决定退让。
4. 主题三元校验：`[THEME] requested=Dark effective=Dark onScreen=Dark` **【日志】** — 请求值、系统生效值、屏上实际值三者对齐，避免"设置成暗色但实际没生效"。

---

## 3. 任务栏耦合与自愈（它最容易崩、也最见功力的一块）

| 机制 | 证据 |
|---|---|
| 注册 shell 钩子 | `RegisterShellHookWindow` / `DeregisterShellHookWindow` **【字符串】**；`RegisterWindowMessage` 与 `TaskbarCreated` 字符串均在 **【字符串】** |
| WinEvent 钩子 | `SetWinEventHook` / `UnhookWinEvent` **【字符串】** |
| explorer 健康监测 | `ExplorerHealthMonitor`、`ExplorerResponsiveness`、`IsExplorerDesktopSurface`、`MoveWithExplorer` **【字符串】** |
| 崩溃恢复 | `TaskbarRecoveryLauncher` + 独立 `AirDeck.TaskbarRecovery.exe` **【字符串+实测】** |
| 微信任务栏闪烁感知 | 日志 7 条 `[WECHAT-ATTENTION] taskbar flash detected` + `WeChatAttentionMonitor` / `IsWeChatMainWindow` **【日志+字符串】** |
| 自身卡顿监测 | `UiResponsivenessMonitor` **【字符串】** |
| 全局鼠标/热键 | `MouseHook`、`SetWindowsHookEx`、`RegisterHotKey` **【字符串】** |

**为什么需要独立恢复进程**：装了全局钩子/窗口子类化的进程一旦卡死，explorer 的输入线程会跟着卡 → 任务栏"假死"。AirDeck 的做法是把恢复动作外置成小进程（`TaskbarRecoveryLauncher`），主进程挂掉/超时也能把任务栏救回来。

---

## 4. 玻璃/毛玻璃渲染链

- 引用：`SetWindowCompositionAttribute`、`DwmExtendFrameIntoClientArea`、`DwmSetWindowAttribute`、`DwmGetWindowAttribute`、`BitBlt`、`SetWindowRgn`+`CreateRoundRectRgn` **【字符串】**
- 自定义图层：`SheetFrostLayer`、`SheetTintLayer`、`DockFrostRadius`、`DockFrostDownscale`、`DockFrostTintOpacity` **【字符串】**
- 停靠栏窗口带 `LAYERED` **【实测】** → 每像素透明（WPF `AllowsTransparency` 路线），配 `BitBlt` 抓屏 + 模糊层做"真毛玻璃"。
- 它把模糊半径/下采样/染色透明度做成**可调三件套**（我们 DesktopDock 的"模糊半径/抓屏精度"就是照这个思路补的）。

> 注意：`[DOCK-FROST]` 只在特定场景出现 2 次，说明毛玻璃层按需重建（不是每帧抓屏）。

---

## 5. 停靠栏布局与动画引擎（最核心的私货）

### 5.1 帧诊断暴露的数据结构

```
[DBG][DOCK-PILE] frame=5698 rendered=0/0 misplaced=0 slots=0 gap=-1 room=0 law=0
   edges=[NaN..NaN] plateW=0.0 mag=[tgt=0.00 live=0.00 smax=1.00 driving=False]
```

逐项解读 **【日志+字符串】**：

| 字段 | 含义 |
|---|---|
| `frame` | 帧序号（每 15~23ms 一条，说明**隐藏时仍在逐帧跑**）|
| `rendered=0/0` | 本帧应渲染/实际渲染的图标数 |
| `slots` / `room` / `gap` | 图标槽位数、可用空间、间距 |
| `edges=[NaN..NaN]` | 停靠栏左右边界（无内容时为空）|
| `plateW` | 底座板宽度（`ItemPlate` / `LayoutPlate` / `RetireDockPlate`）|
| `law` | 放大"定律"编号（算法分支）|
| `mag=[tgt live smax driving]` | 放大：目标值、当前值、最大倍率、是否由驱动层接管 |

对应类名：`ItemPlate`、`LayoutPlate`、`RetireDockPlate`、`ParkSlot`、`PopDockSlot`、`LandDockSlot`、`ReleaseDockBand`、`RunningBand`、`RubberBand` **【字符串】** → 图标有完整的"入槽 → 停靠 → 抬起 → 落位 → 退役"生命周期。

### 5.2 帧驱动 + 缓动 + 自测

```
[DRIVEN] settle px -903->-1467 scale=1.50 dur=143ms ease=QuinticEase
[SWIPE]  commit dx=-903 proj=1911/147 rem=564 v=7756 tr=7756 peak=12223 -> 143ms QuinticEase ...
[SWIPE-FPS] phase=settle held=145ms events=0 presented=23 expect=24 dropped=0 avg=6.1ms(165fps) worst=6.1ms
[CLOSE-T] animationCallback=24.7ms
```

- 手势：**惯性投影**（proj=1911/147：位移/阈值）+ 速度上限（`tr=peak=12223` 被钳制）+ 手速 `hand=7.8` 与所需值 `needed=4.0` 比较决定是否翻页。
- 动画：**逐帧驱动**（`DRIVEN`），缓动 `QuinticEase`（另有 `CubicEase`），143ms 定长；`scale=1.50` 是 DPI 缩放。
- 自测：`presented/expect/dropped/worst/gaps` —— 每一段动画都对着 165Hz 刷新率（6.06ms/帧）核对有没有掉帧。
- 驱动源：`CompositionTarget` + `RenderingEventArgs` **【字符串】**。

---

## 6. 启动台（Launchpad）

| 机制 | 证据 |
|---|---|
| 全屏窗口 | `LaunchpadWindow`（34 次出现，主窗口）**【字符串】** |
| 雨/粒子层 | `LaunchpadRainLayer`、`DesktopRainService`、`RainPhysics`、`RainStartupRecovery`、`PositionRainHost` **【字符串】** |
| 分页 | `[ICON-PAGE] page=4 count=28`、`page=5 count=28` **【日志】** → 每页 28 个 |
| 懒加载 | 图标 128×128，来源 `StartMenu` / `Desktop` / `UWP` 三类打标 **【日志】** |
| 预取 | `[PAGE-PREPARE] target=3 ready=True cost=16.2ms` / `target=2 cost=29.2ms` **【日志】** → 相邻页提前铺好 |
| 冷启动优化 | `[WARM] cold fill page=4 at-press` **【日志】** → 按下先出当前页，其余异步填充 |
| 命中测试 | `[PRESS-T] hit=0.0ms total=2.3ms app=False` **【日志】** |
| 搜索 | `SearchPanel`、`SearchFieldHost`、`GetPinyinInitials`/`SearchPinyinInitials` **【字符串】** |
| 应用扫描 | `[STEAM] Scanned 5 installed games from 2 Steam library folders.`、`Scan complete: 225 apps, 0 manual` **【日志】** |
| 扫描结果比对 | `[RESCAN] App set identical (227 apps), skipping UI rebuild.` **【日志】** → 集合相同就不重建 UI |

---

## 7. 托盘 / 小组件 / 时钟 / 音乐

| 子系统 | 类名证据 **【字符串】** |
|---|---|
| 自绘系统托盘面板 | `SystemTrayService`、`SystemTrayMonitor`、`SystemTrayPanel`、`ITrayIconService`、`Shell_NotifyIcon`、`Shell_NotifyIconGetRect`、`SystemTrayPanel` |
| 时钟 | `DesktopClockService`、`ClockController`、`ClockFaceClassic/Digital/Elegant/Minimal/Vibrant`、`ClockPresetList`、`ClockLunar` |
| 系统监视 | `SystemMonitor`、`ISystemMonitorService`、`TopWidgetsPanel` |
| 音乐 | `SystemMusicService`、`MusicSpectrumService`、`CoversMonitor`、`DesktopMusicWindow`、`CoverSource` |
| 回收站 | `RecycleBinService` |
| 拖放接纳 | `TryCreateDropTargetHelper`、`DropTargetHelper` |
| 快捷方式/图标 | `ShellLinkHelper`、`OpenPropertyStore`、`SHGetFileInfo`、`SHCreateItemFromParsingName`、`IShellItemImageFactory` |

---

## 8. 多屏与交互

`MultiMonitorDockController`、`MonitorFromWindow`、`RepositionToCurrentMonitor`、`SetFixedMonitor`、`SelectedLaunchpadMonitor`、`FindHost`、`FindDesktopWindow` **【字符串】** → 停靠栏可跟随鼠标屏，也可固定在某块屏；启动台单独选屏。

---

## 9. 授权、更新、遥测（运行时联网行为）

```
[LICENSE] transport error https://file.xjtool.top/v1/apps/airdeck/check: HttpRequestException ... (127.0.0.1:7877)
[LICENSE] offline, keeping entitlement (0.8 days since last check)
[LICENSE] invite info unavailable: 无法连接激活服务器，请检查网络后重试
```

- 端点：`/v1/apps/airdeck/check`（授权校验）、`/invite/info?deviceId=ad-<32hex>`（设备绑定/邀请）、`/latest?os=windows`（更新）**【日志】**
- 设备号形如 `ad-206fb93d2c1fd63fe1628712e9e91cda` **【日志】**
- **离线宽限**：连不上时保留已授权状态并记录"距上次校验 0.8 天" **【日志】**
- 埋点：`AptabaseService`（9 处）**【字符串】**
- 本机代理把 `file.xjtool.top` 解析到 127.0.0.1:7877 → 一直处于离线宽限状态 **【实测】**

---

## 10. 启动时序（还原）

```
1. --startup → SingleInstanceService 抢锁
2. 建 Hidden Window（WPF 宿主，用于 owner 关系）
3. 建全屏 LaunchpadWindow（cloaked）+ 建 DesktopDockWindow（175 高，8px 内缩）
4. STARTUP-READY cost=442ms cloaked=True sceneCompleted=True → 解除隐藏
5. 扫描应用（StartMenu+Desktop+UWP+Steam）→ Scan complete: 225 apps
6. 注册 shell hook / 热键 / 托盘图标 / 主题同步（THEME 三值校验）
7. 进入常驻：frame 计数器持续累加（DOCK-PILE 诊断）
8. 交互时按需：放大律计算 → 逐帧驱动 → 手势惯性 → 分页预取 → 帧率自测
```

---

## 11. 值得警惕的三点（不是优点）

1. **隐藏时仍逐帧空转**：`rendered=0/0` 但 `frame` 一直涨。实测两段：`20:22:42.481 frame=5658` → `20:23:03.391 frame=7005`＝**20.9 秒推进 1347 帧（≈64 fps）**，同期 `airdeck.log` 里 2146 行有 1348 行是 `DOCK-PILE`；`airdeck.log.previous`（4 MB）从 `2026-10-18 22:57:57 frame=282736` 开始，按 64 fps 折算 `282736/64≈4417 秒≈74 分钟`，即**诊断全开时约 3 MB/小时**并自动滚动到 `.previous`。这是"随时可瞬发"换来的持续开销。
2. **全局钩子 + shell hook** 让它与 explorer 强耦合，才需要独立的 `TaskbarRecovery.exe` 兜底——稳定性是"补"出来的，不是"没有风险"。
3. **联网授权/埋点常驻**：离线只是宽限，不是不校验。

---

## 12. 对 DesktopDock 的可用结论

**值得搬（按性价比排序）**
1. 逐帧驱动 + 缓动 + 帧率自测（我们目前是 200ms 轮询 + 离散动画）。
2. 图标"入槽/抬起/落位/退役"生命周期（我们现在是直接增删 Border）。
3. 分页懒加载 + 相邻页预取 + "按下先出当前页"（我们是一次性建 103 个 tile）。
4. 扫描结果集合比对、相同就不重建 UI（我们每次 `Rebuild` 全清）。
5. 主题三元校验（requested/effective/onScreen）。
6. 热角用低级鼠标钩子（我们是 200ms 轮询 `GetCursorPos`，会有最多 200ms 延迟）。
7. shell hook + `TaskbarCreated` + 响应性监测的组合自愈（我们只做了 `TaskbarCreated` + 10s 句柄比对）。

**不建议搬**
- AppBar 预留屏幕边缘（你要求保留系统任务栏，覆盖式就够）。
- 自绘系统托盘面板（复杂度极高，收益低）。
- 联网授权/埋点。
- 隐藏时空转的渲染循环（我们可以按需唤醒）。
