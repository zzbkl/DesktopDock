using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DesktopDock
{
    internal static class Program
    {
        private static Mutex singleInstance;

        public static string LastScanText = "尚未检测";
        // --frames：把逐帧驱动的真实帧率打进日志（调试动画时用）
        public static bool VerboseFrame = false;
        private static bool scanning = false;
        private static DateTime lastScanUtc = DateTime.MinValue;

        [STAThread]
        public static void Main(string[] args)
        {
            bool autoDrawer = false;
            bool autoEdit = false;
            string hideName = null;
            string catName = null;
            bool forceScan = false;
            string testName = null;
            string folderName = null;
            double magX = -1;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    if (string.Equals(a, "--drawer", StringComparison.OrdinalIgnoreCase)) autoDrawer = true;
                    if (string.Equals(a, "--edit", StringComparison.OrdinalIgnoreCase)) autoEdit = true;
                    if (string.Equals(a, "--rescan", StringComparison.OrdinalIgnoreCase)) forceScan = true;
                    if (string.Equals(a, "--cat", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        catName = args[i + 1];
                        i++;
                    }
                    if (string.Equals(a, "--hide", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        hideName = args[i + 1];
                        i++;
                    }

                    if (string.Equals(a, "--mag", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        double.TryParse(args[i + 1], out magX);
                        i++;
                    }
                    if (string.Equals(a, "--test", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        testName = args[i + 1];
                        i++;
                    }
                    if (string.Equals(a, "--folder", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        folderName = args[i + 1];
                        i++;
                    }
                }
            }
            bool created;
            singleInstance = new Mutex(true, "DesktopDock_SingleInstance_v0", out created);
            if (!created)
            {
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Log("UNHANDLED: " + (e.ExceptionObject == null ? "(null)" : e.ExceptionObject.ToString()));
            };

            if (testName != null)
            {
                List<AppItem> probe = LoadOrScan(false);
                string msg = null;
                AppItem hit = null;
                foreach (AppItem it in probe)
                {
                    if (it.Name != null && it.Name.IndexOf(testName, StringComparison.OrdinalIgnoreCase) >= 0) { hit = it; break; }
                }
                if (hit == null) msg = "not found: " + testName;
                else
                {
                    Log("test target=" + hit.Target);

                    LaunchResult r = Launcher.ActivateOrLaunch(hit, out msg);
                    msg = "test-launch " + r.ToString() + ": " + msg;
                }
                Log(msg);
                return;
            }

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            List<AppItem> all = LoadOrScan(forceScan);

            // 图标包要在建栏（SetItems）之前准备好，否则第一帧还是系统图标
            ConfigStore.DockSettings early = ConfigStore.LoadSettings();
            IconPack.Enabled = early.UseIconPack;
            string ownPack = System.IO.Path.Combine(ConfigStore.Dir, "icons");
            if (System.IO.File.Exists(System.IO.Path.Combine(ownPack, "pack.json"))) IconPack.Folder = ownPack;
            else
            {
                string air = @"C:\Users\" + Environment.UserName +
                    @"\AppData\Local\Programs\AirDeck\Assets\IconPacks\mac-tiles";
                if (System.IO.File.Exists(System.IO.Path.Combine(air, "pack.json"))) IconPack.Folder = air;
                else IconPack.Folder = ownPack;
            }
            IconPack.EnsureLoaded();
            Log("icon pack: enabled=" + IconPack.Enabled.ToString() + " count=" + IconPack.Count.ToString()
                + " dir=" + (IconPack.Folder == null ? "(none)" : IconPack.Folder));

            // 常驻入口优先用 GlassBoard 已有的那份清单（一次导入，不手打）
            GlassBoardImporter.ImportResult imp = GlassBoardImporter.Import();
            List<AppItem> pinned;
            if (imp.Pinned.Count > 0)
            {
                pinned = imp.Pinned;
                Log("glassboard import: pinned=" + imp.Pinned.Count.ToString() + " total=" + imp.All.Count.ToString());
                string names = "";
                for (int i = 0; i < pinned.Count && i < 12; i++)
                {
                    names += pinned[i].Name + " | ";
                }
                Log("pinned list: " + names);
            }
            else
            {
                pinned = SelectPinned(all);
                Log("glassboard import unavailable: " + imp.Error);
            }

            DockWindow dock = new DockWindow();
            DrawerWindow drawer = new DrawerWindow();

            dock.Drawer = drawer;
            dock.SetItems(ApplyPinOrder(pinned));
            drawer.SetApps(all);

            dock.OnRescan = delegate
            {
                List<AppItem> rescanned = CollectApps();
                drawer.SetApps(rescanned);
                List<AppItem> newPinned = SelectPinned(rescanned);
                dock.SetItems(newPinned);
                ConfigStore.Save(rescanned);
                dock.SetStatus("已重新扫描：" + rescanned.Count.ToString() + " 个应用");
            };

            if (args != null && Array.IndexOf(args, "--heal-test") >= 0)
            {
                DispatcherTimer ht = new DispatcherTimer();
                ht.Interval = TimeSpan.FromSeconds(6);
                ht.Tick += delegate(object s7, EventArgs e7)
                {
                    ht.Stop();
                    // 人为制造"叫不出来"：停掉巡视定时器 + 收掉把手，看门狗应把它救回来
                    dock.StopTimers();
                    if (dock.Peek != null) dock.Peek.Hide();
                    Log("heal-test: timers stopped, strip hidden");
                };
                ht.Start();
            }

            // 看门狗：每 15 秒体检一次，防止"从游戏/全屏回来之后叫不出来"
            DispatcherTimer heal = new DispatcherTimer();
            heal.Interval = TimeSpan.FromSeconds(15);
            heal.Tick += delegate { dock.SelfHeal(); };
            heal.Start();





            Glass.BlurDip = ConfigStore.LoadSettings().FrostRadius;
            Glass.Downscale = ConfigStore.LoadSettings().FrostDownscale;

            StartAutoScan(dock, drawer);

            dock.Show();
            dock.PositionAtBottom();

            // 任务栏前提"把手"：dock 收起时地上留一根细条，鼠标碰一下探出来
            PeekWindow peek = new PeekWindow();
            peek.Dock = dock;
            dock.Peek = peek;
            peek.Place();
            int dsIdx = args == null ? -1 : Array.IndexOf(args, "--drawer-scroll");
            if (dsIdx >= 0 && drawer != null)
            {
                double off = 0;
                if (dsIdx + 1 < args.Length) double.TryParse(args[dsIdx + 1], out off);
                DispatcherTimer dst = new DispatcherTimer();
                dst.Interval = TimeSpan.FromMilliseconds(2600);
                dst.Tick += delegate { dst.Stop(); drawer.DebugScroll(off); };
                dst.Start();
            }
            if (args != null && Array.IndexOf(args, "--hotdbg") >= 0) dock.debugHot = true;
            // 调试用：--rebuild-test 隔 2.5s 连续 Rebuild 两次，验证图标槽复用（第二次应 fresh=0）
            if (args != null && Array.IndexOf(args, "--rebuild-test") >= 0)
            {
                DispatcherTimer rt = new DispatcherTimer();
                rt.Interval = TimeSpan.FromMilliseconds(2500);
                rt.Tick += delegate { rt.Stop(); dock.Rebuild(); dock.Rebuild(); };
                rt.Start();
            }
            if (args != null && Array.IndexOf(args, "--frames") >= 0) VerboseFrame = true;
            // 调试用：--hot-trigger TL|TR|BL|BR 直接走一遍热角动作，验证钩子→动作这条链
            int htIdx = args == null ? -1 : Array.IndexOf(args, "--hot-trigger");
            if (htIdx >= 0 && htIdx + 1 < args.Length)
            {
                string corner = args[htIdx + 1];
                DispatcherTimer ht = new DispatcherTimer();
                ht.Interval = TimeSpan.FromMilliseconds(1200);
                ht.Tick += delegate
                {
                    ht.Stop();
                    Log("hot-trigger debug: " + corner + " hook=" + HotCornerNative.HookInstalled.ToString());
                    dock.FireHotCorner(corner);
                };
                ht.Start();
            }
            if (args != null && Array.IndexOf(args, "--force") >= 0)
            {
                dock.ForceVisible = true;
                dock.ShowDock(true);
            }
            else
            {
                dock.HideDock();   // 默认收起：不挡任何窗口内容
            }

            dock.StartWatchers();

            // 预热：抽屉第一次打开要建 66 个图标位（实测 ~450ms，就是"点了半天不出来"的体感）。
            // 启动 2.5 秒后趁空闲先把它们建好，之后点开是 0ms。
            DispatcherTimer warm = new DispatcherTimer();
            warm.Interval = TimeSpan.FromMilliseconds(2500);
            warm.Tick += delegate(object ws, EventArgs we)
            {
                ((DispatcherTimer)ws).Stop();
                try { drawer.Prebuild(); } catch { }
                // 图标位建好后隔 1.2 秒抓一次背景缓存（后台线程）：首次开屏从 107ms 降到 58ms。
                // 注：曾试过在这里让抽屉"隐身露一次脸"预热合成（DrawerWindow.Warmup），
                // 五轮对比里只有一轮从 53fps 提到 71fps，其余无差别，代价是启动后 700ms 内
                // 一块看不见的置顶窗口吃掉点击 —— 收益不稳、风险实在，故不启用。
                DispatcherTimer warm2 = new DispatcherTimer();
                warm2.Interval = TimeSpan.FromMilliseconds(1200);
                warm2.Tick += delegate(object ws2, EventArgs we2)
                {
                    ((DispatcherTimer)ws2).Stop();
                    try { drawer.PrewarmFor(dock); } catch { }
                };
                warm2.Start();
            };
            warm.Start();

            // 游戏模式名单：窗口化的游戏靠进程名认，独占全屏的不用名单也能认出来
            Native.GameProcessNames = ConfigStore.LoadGames();
            // 桌面外壳名单：本机装了 AirDeck，切回桌面时前台是它而不是 Progman
            Native.DesktopShells = ConfigStore.LoadShells();
            ConfigStore.DockSettings st = ConfigStore.LoadSettings();
            DockWindow.IconSize = st.IconSize;
            DockWindow.BarHeight = st.BarHeight;
            DockWindow.TintTop = st.TintTop;
            DockWindow.TintBottom = st.TintBottom;
            dock.AllApps = all;
            if (args != null && Array.IndexOf(args, "--cats") >= 0)
            {
                Dictionary<string, string> ovr = ConfigStore.LoadCategories();
                foreach (AppItem ci in all) Log("cat: " + AppCategory.Classify(ci, ovr) + " | " + ci.Name);
            }
            if (args != null && Array.IndexOf(args, "--gm-debug") >= 0) dock.DebugGame = true;

            // 调试用：--add <路径> 直接走“添加应用”的同一段代码
            int addIdx = args == null ? -1 : Array.IndexOf(args, "--add");
            if (addIdx >= 0 && addIdx + 1 < args.Length) dock.Drawer.AddAppFiles(new string[] { args[addIdx + 1] });

            // 调试用：--wheel <1|-1> 直接模拟一次滚轮换窗（本机光标会被全屏程序抢走，没法真滚）
            int wi = args == null ? -1 : Array.IndexOf(args, "--wheel");
            if (wi >= 0 && wi + 1 < args.Length)
            {
                int dir = args[wi + 1] == "-1" ? -1 : 1;
                DispatcherTimer wt = new DispatcherTimer();
                wt.Interval = TimeSpan.FromMilliseconds(1800);
                wt.Tick += delegate(object ws3, EventArgs we3)
                {
                    ((DispatcherTimer)ws3).Stop();
                    dock.DebugCycle(dir);
                };
                wt.Start();
            }

            // 调试用：--settings-section <名字> 直接打开某一页
            int secIdx = args == null ? -1 : Array.IndexOf(args, "--settings-section");
            if (secIdx >= 0 && secIdx + 1 < args.Length) SettingsWindow.PendingSection = args[secIdx + 1];

            // 调试用：--search <拼音/关键字> 打开抽屉并预填搜索框（验证拼音首字母）
            int si = args == null ? -1 : Array.IndexOf(args, "--search");
            if (si >= 0 && si + 1 < args.Length)
            {
                string kw = args[si + 1];
                autoDrawer = true;
                DispatcherTimer qt = new DispatcherTimer();
                qt.Interval = TimeSpan.FromMilliseconds(2600);
                qt.Tick += delegate(object qs, EventArgs qe)
                {
                    ((DispatcherTimer)qs).Stop();
                    dock.Drawer.SetSearchText(kw);
                };
                qt.Start();
            }

            // 调试用：--preview-top 直接预览当前前台窗口，单测 DWM 缩略图这条路径
            if (args != null && Array.IndexOf(args, "--preview-top") >= 0)
            {
                DispatcherTimer tt = new DispatcherTimer();
                tt.Interval = TimeSpan.FromMilliseconds(2600);
                tt.Tick += delegate(object ts, EventArgs te)
                {
                    ((DispatcherTimer)ts).Stop();
                    dock.DebugPreviewTop();
                };
                tt.Start();
            }

            // 调试用：--preview <应用名> 直接弹某个应用的窗口预览（本机没法用代码悬停）
            int pi = args == null ? -1 : Array.IndexOf(args, "--preview");
            if (pi >= 0 && pi + 1 < args.Length)
            {
                string target = args[pi + 1];
                DispatcherTimer pt = new DispatcherTimer();
                pt.Interval = TimeSpan.FromMilliseconds(2600);
                pt.Tick += delegate(object ps, EventArgs pe)
                {
                    ((DispatcherTimer)ps).Stop();
                    dock.DebugPreview(target);
                };
                pt.Start();
            }

            if (args != null && Array.IndexOf(args, "--settings") >= 0)
            {
                DispatcherTimer stTimer = new DispatcherTimer();
                stTimer.Interval = TimeSpan.FromMilliseconds(900);
                stTimer.Tick += delegate(object s2, EventArgs e2)
                {
                    ((DispatcherTimer)s2).Stop();
                    SettingsWindow.Open(dock);
                };
                stTimer.Start();
            }

            // 使用统计：不管从哪儿启动的（任务栏、桌面、开始菜单），
            // 只要真的切到前台待够几秒就记一次，抽屉排序才有意义
            Usage.StartTracker(all);

            // 调试用：--mag <bar内X坐标> 直接把放大状态摆好再截图（本机鼠标会被全屏游戏抢走）
            if (magX >= 0)
            {
                DispatcherTimer magTimer = new DispatcherTimer();
                magTimer.Interval = TimeSpan.FromMilliseconds(1800);
                magTimer.Tick += delegate
                {
                    magTimer.Stop();
                    dock.DebugMagnify(magX);
                };
                magTimer.Start();
            }

            if (folderName != null)
            {
                DispatcherTimer ft = new DispatcherTimer();
                ft.Interval = TimeSpan.FromMilliseconds(1600);
                ft.Tick += delegate
                {
                    ft.Stop();
                    dock.DebugFolder(folderName);
                };
                ft.Start();
            }

            // 托盘图标：左键显示/收起 dock，右键弹菜单
            TrayIcon tray = new TrayIcon();
            tray.Create("DesktopDock");

            // 资源管理器（任务栏）健康监测：⑦ 改成"两个探针"而不是单纯比句柄
            //   ① shell hook：explorer 的窗口创建/销毁会主动通知我们（活着就有事件）
            //   ② 响应性探针：SendMessageTimeout(WM_NULL, 350ms)，挂起时立刻能看出来
            // 句柄变了 / 从挂起恢复 / 事件长时间不动 = 三种情况都补一次托盘和把手。
            DispatcherTimer shellWatch = new DispatcherTimer();
            shellWatch.Interval = TimeSpan.FromSeconds(5);
            IntPtr[] lastTray = new IntPtr[] { Native.FindWindow("Shell_TrayWnd", null) };
            long[] lastShellEventSeen = new long[] { 0 };
            int[] hungTicks = new int[] { 0 };
            shellWatch.Tick += delegate
            {
                try
                {
                    IntPtr now = Native.FindWindow("Shell_TrayWnd", null);
                    bool changed = now != lastTray[0];
                    bool responsive = Native.IsWindowResponsive(now, 350);
                    if (!responsive)
                    {
                        // explorer 忙/挂了：先记账，等它回来再修（此时动托盘也没用）
                        hungTicks[0]++;
                        if (hungTicks[0] == 1) Log("shell: taskbar not responding (explorer busy)");
                        return;
                    }
                    bool recoveredFromHung = hungTicks[0] > 0;
                    hungTicks[0] = 0;
                    if (changed)
                    {
                        Log("shell: taskbar window changed 0x" + lastTray[0].ToString("x") + " -> 0x" + now.ToString("x") + ", recovering");
                        lastTray[0] = now;
                    }
                    long ev = tray.ShellEventCount;
                    bool silent = lastShellEventSeen[0] != 0 && ev == lastShellEventSeen[0] && (Environment.TickCount - tray.LastShellEventAt) > 45000;
                    lastShellEventSeen[0] = ev;
                    bool trayGone = !tray.Exists();
                    if (trayGone && !changed && !recoveredFromHung && !silent)
                        Log("shell: tray icon missing while explorer is fine, re-adding");
                    if (changed || recoveredFromHung || silent || trayGone)
                    {
                        if (recoveredFromHung) Log("shell: taskbar responsive again, recovering");
                        if (silent) Log("shell: no shell hook events for 45s, recovering (hook=" + tray.ShellHookActive.ToString() + ")");
                        tray.ReAdd();
                        // 只有"explorer 真的重启过"才需要把底部把手摆回去；
                        // 托盘图标自己挂不上（trayGone）跟位置无关，别每 5 秒动一次把手。
                        if (changed || recoveredFromHung || silent) dock.RestoreStrip(true);
                    }
                }
                catch (Exception ex) { Log("shell watch failed: " + ex.Message); }
            };
            shellWatch.Start();
            tray.LeftClick = delegate { dock.TogglePinned(); };
            tray.RightClick = delegate
            {
                System.Windows.Controls.ContextMenu menu = dock.BarMenu;
                if (menu == null) return;
                dock.ShowDock(true);
                menu.PlacementTarget = dock;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen = true;
            };

            // 空闲时把工作集还给系统（DockBar 同款手法，任务管理器里会降到几 MB）
            DispatcherTimer trim = new DispatcherTimer();
            trim.Interval = TimeSpan.FromMinutes(2);
            trim.Tick += delegate
            {
                Native.EmptyWorkingSet(Native.GetCurrentProcess());
            };
            trim.Start();

            Log("started, items=" + all.Count.ToString() + ", pinned=" + pinned.Count.ToString());
            // 渲染路径要记账：tier=2 是硬件；tier=0 说明退回软件渲染（内存会成倍涨、动画也上不去）
            Log("gfx: tier=" + (System.Windows.Media.RenderCapability.Tier >> 16).ToString()
                + " windows=" + System.Windows.Application.Current.Windows.Count.ToString());

            if (autoDrawer)
            {
                drawer.PinOpen = true;
                drawer.StartInEdit = autoEdit;
                DispatcherTimer t = new DispatcherTimer();
                t.Interval = TimeSpan.FromMilliseconds(1200);
                t.Tick += delegate(object s, EventArgs e)
                {
                    t.Stop();
                    drawer.ShowPanel(dock);
                };
                t.Start();
            }

            // 自检用：--cat "分类" 等价于点一下分类页签
            if (!string.IsNullOrEmpty(catName))
            {
                DispatcherTimer ct = new DispatcherTimer();
                ct.Interval = TimeSpan.FromMilliseconds(2400);
                ct.Tick += delegate(object s, EventArgs e)
                {
                    ct.Stop();
                    drawer.SetCategory(catName);
                    Log("--cat " + catName);
                };
                ct.Start();
            }

            // 自检用：--hide "名字" 等价于在整理模式里点一下那个图标
            if (!string.IsNullOrEmpty(hideName))
            {
                DispatcherTimer ht = new DispatcherTimer();
                ht.Interval = TimeSpan.FromMilliseconds(2400);
                ht.Tick += delegate(object s, EventArgs e)
                {
                    ht.Stop();
                    bool ok = drawer.HideApp(hideName);
                    Log("--hide " + hideName + " -> " + ok.ToString());
                };
                ht.Start();
            }

            // 用户要求：开机自启动 + 后台运行（无任务栏按钮、不进 Alt-Tab）
            Startup.EnsureEnabled();

            app.Run();
        }

        // 合并开始菜单 + 桌面快捷方式，并按归一化名字去重（"Alice in Cradle" 和
        // "AliceInCradle.exe - 快捷方式" 只留一个）
        // 多来源收集：开始菜单 + 桌面快捷方式 + UWP 商店应用 + Steam 库 +（可选）程序目录深扫
        // 自动刷新：默认每 10 分钟后台扫一次，新装的应用自动出现，卸载的自动消失
        private static void StartAutoScan(DockWindow dock, DrawerWindow drawer)
        {
            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(20);
            timer.Tick += delegate
            {
                ConfigStore.DockSettings s = ConfigStore.LoadSettings();
                if (s.AutoRefreshMinutes <= 0) return;
                if (scanning) return;
                if (DateTime.UtcNow - lastScanUtc < TimeSpan.FromMinutes(s.AutoRefreshMinutes)) return;
                lastScanUtc = DateTime.UtcNow;
                scanning = true;
                System.Threading.Tasks.Task.Run(delegate
                {
                    List<AppItem> scanned = null;
                    try { scanned = CollectApps(s); }
                    catch (Exception ex) { Log("auto scan failed: " + ex.Message); }
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        scanning = false;
                        if (scanned == null) return;
                        int added, removed;
                        List<AppItem> merged = Merge(ConfigStore.Load(), scanned, out added, out removed);
                        // 集合比对：应用清单没变就完全不动 UI（AirDeck 的 [RESCAN] App set identical 那招）。
                        // 只比 added/removed 不够——数量相同但内容换过也会漏，所以再比一次指纹。
                        string sig = SetSignature(merged);
                        if (added == 0 && removed == 0 && lastAppSignature != null
                            && string.Equals(sig, lastAppSignature, StringComparison.Ordinal))
                        {
                            Log("auto scan: app set identical (" + merged.Count.ToString() + " apps), skip UI rebuild");
                            return;
                        }
                        Log("auto scan: sig " + (lastAppSignature == null ? "(none)" : lastAppSignature) + " -> " + sig);
                        lastAppSignature = sig;
                        Log("auto scan: +" + added + " -" + removed + " total=" + merged.Count);
                        if (added == 0 && removed == 0) return;
                        ConfigStore.Save(merged);
                        drawer.SetApps(merged);
                        dock.SetItems(ApplyPinOrder(SelectPinned(merged)));
                        dock.SetStatus("自动更新：新增 " + added + " 个应用");
                    }));
                });
            };
            timer.Start();
        }

        private static List<AppItem> CollectApps()        {
            return CollectApps(ConfigStore.LoadSettings());
        }

        // 应用清单指纹：把归一化名字排序后拼起来取哈希。集合没变 → 指纹相同 → 跳过重建。
        private static string lastAppSignature;

        private static string SetSignature(List<AppItem> list)
        {
            if (list == null) return "";
            List<string> keys = new List<string>();
            foreach (AppItem it in list)
            {
                string k = AppScanner.NameKey(it.Name);
                if (k.Length == 0) k = "x:" + it.Target;
                keys.Add(k);
            }
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            StringBuilder sb = new StringBuilder(keys.Count * 12);
            foreach (string k in keys) sb.Append(k).Append('\n');
            return list.Count.ToString() + ":" + sb.ToString().GetHashCode().ToString("X8");
        }

        private static List<AppItem> CollectApps(ConfigStore.DockSettings s)
        {
            List<AppItem> all = Discover.Collect(s.ScanUwp, s.ScanSteam, s.ScanDeep, true, true);
            List<AppItem> result = new List<AppItem>();
            System.Collections.Generic.HashSet<string> keys =
                new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int uwp = 0, steam = 0, builtin = 0;
            foreach (AppItem it in all)
            {
                string k = AppScanner.NameKey(it.Name);
                if (k.Length == 0) k = "x:" + it.Target;
                if (keys.Contains(k)) continue;
                keys.Add(k);
                result.Add(it);
                if (string.Equals(it.Source, "uwp", StringComparison.OrdinalIgnoreCase)) uwp++;
                else if (string.Equals(it.Source, "steam", StringComparison.OrdinalIgnoreCase)) steam++;
                if (AppCategory.IsBuiltin(it)) builtin++;
            }
            LastScanText = DateTime.Now.ToString("HH:mm") + " · 共 " + result.Count.ToString()
                + " 个（商店 " + uwp.ToString() + " · Steam " + steam.ToString()
                + " · 自带 " + builtin.ToString() + "）";
            return result;
        }

        // 增量合并：新出现的加入，装系统里已经消失的移出（手动添加的只在文件消失时移除）
        private static List<AppItem> Merge(List<AppItem> existing, List<AppItem> scanned, out int added, out int removed)
        {
            added = 0; removed = 0;
            if (existing == null) existing = new List<AppItem>();
            if (scanned == null) scanned = new List<AppItem>();

            System.Collections.Generic.HashSet<string> scanKeys =
                new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppItem it in scanned)
            {
                string k = AppScanner.NameKey(it.Name);
                if (k.Length == 0) k = "x:" + it.Target;
                scanKeys.Add(k);
            }

            List<AppItem> result = new List<AppItem>();
            System.Collections.Generic.HashSet<string> kept =
                new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppItem it in existing)
            {
                string k = AppScanner.NameKey(it.Name);
                if (k.Length == 0) k = "x:" + it.Target;
                if (kept.Contains(k)) { removed++; continue; }
                bool keep;
                if (it.Manual)
                    keep = string.IsNullOrEmpty(it.Target) || it.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || System.IO.File.Exists(it.Target);
                else if (!string.IsNullOrEmpty(it.Source))
                    keep = scanKeys.Contains(k);               // 商店/Steam/深扫：必须这次还扫得到
                else
                    keep = scanKeys.Contains(k) || (System.IO.File.Exists(it.Target) && !it.Target.StartsWith("shell:"));
                if (!keep) { removed++; continue; }
                kept.Add(k);
                result.Add(it);
            }

            foreach (AppItem it in scanned)
            {
                string k = AppScanner.NameKey(it.Name);
                if (k.Length == 0) k = "x:" + it.Target;
                if (kept.Contains(k)) continue;
                kept.Add(k);
                result.Add(it);
                added++;
            }
            return result;
        }

        private static List<AppItem> LoadOrScan(bool force)
        {
            if (!force)
            {
                List<AppItem> saved = ConfigStore.Load();
                if (saved != null && saved.Count > 0)
                {
                    int added, removed;
                    List<AppItem> merged = Merge(saved, CollectApps(), out added, out removed);
                    if (added > 0 || removed > 0)
                    {
                        ConfigStore.Save(merged);
                        Log("startup merge: +" + added + " -" + removed + " total=" + merged.Count);
                    }
                    return merged;
                }
            }

            List<AppItem> all = CollectApps();

            ConfigStore.Save(all);
            Log("first scan, items=" + all.Count.ToString());
            return all;
        }

        // 拖动排序后的顺序存在 pinorder.json，启动时按它重排
        private static List<AppItem> ApplyPinOrder(List<AppItem> pinned)
        {
            List<string> order = ConfigStore.LoadPinOrder();
            if (order == null || order.Count == 0) return pinned;
            List<AppItem> result = new List<AppItem>();
            foreach (string name in order)
            {
                foreach (AppItem it in pinned)
                {
                    if (it.Name == name && !result.Contains(it)) { result.Add(it); break; }
                }
            }
            foreach (AppItem it in pinned)
            {
                if (!result.Contains(it)) result.Add(it);
            }
            Program.Log("pin order applied: " + result.Count.ToString());
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (AppItem it in result)
            {
                if (sb.Length > 0) sb.Append(" | ");
                sb.Append(it.Name);
            }
            Program.Log("bar order: " + sb.ToString());
            return result;
        }

        // v0 先取前 N 个可用的作为常驻入口，之后用 GlassBoard 的 layout.json 覆盖
        private static List<AppItem> SelectPinned(List<AppItem> all)
        {
            List<AppItem> pinned = new List<AppItem>();
            foreach (AppItem it in all)
            {
                if (it.Pinned)
                {
                    pinned.Add(it);
                }
            }
            if (pinned.Count == 0)
            {
                int n = 0;
                foreach (AppItem it in all)
                {
                    if (!string.IsNullOrEmpty(it.Target) && it.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        pinned.Add(it);
                        n++;
                        if (n >= 10) break;
                    }
                }
            }
            return pinned;
        }

        internal static void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(ConfigStore.Dir);
                string path = Path.Combine(ConfigStore.Dir, "dock.log");
                File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
            }
            catch { }
        }
    }
}









