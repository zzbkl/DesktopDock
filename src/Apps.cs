using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;

namespace DesktopDock
{
    internal class AppItem
    {
        public string Name { get; set; }
        public string Target { get; set; }
        // 原始快捷方式（.lnk）路径：启动时优先用它，绝不改动别人原本的文件地址
        public string Shortcut { get; set; }
        public string Arguments { get; set; }
        public string IconSource { get; set; }
        public bool Pinned { get; set; }
        // 非 exe 的启动方式（UWP 的 shell:AppsFolder\AUMID、Steam 的 steam://rungameid/id）
        public string Launch { get; set; }
        // 来源：menu / desktop / uwp / steam / deep / manual
        public string Source { get; set; }
        // 用户手动加的（自动清理时不删）
        public bool Manual { get; set; }

        // 图标路径：优先用 exe 本体（256px 清晰），不行再用 .lnk
        public string IconPath
        {
            get
            {
                if (!string.IsNullOrEmpty(IconSource)) return IconSource;
                if (!string.IsNullOrEmpty(Target) && Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Target;
                return Target;
            }
        }
    }

    internal static class AppScanner
    {
        private static readonly string[] Noise =
        {
            "uninstall", "卸载", "readme", "help", "帮助", "release notes",
            "文档", "documentation", "license", "许可", "website", "网站", "homepage",
            // 配置 / 管理 / 更新 / 诊断类工具：抽屉里不该出现，用户明确要求
            "管理", "配置", "更新", "updater", "update", "swapper", "reporter",
            "崩溃", "crash", "repair", "修复", "diagnostic", "诊断", "setup", "安装程序",
            "cleanup", "recovery", "smartmanager", "helper", "服务管理器",
            // 手册 / 说明 / 向导 / 附属工具：这些不是"要打开的应用"
            "手册", "指南", "说明书", "常见故障", "编程手册", "安装手册", "文档说明",
            "向导", "wizard", "support tool", "worker agent", "installation center",
            "remote debugger", "monitor", "task scheduler", "treehouse", "toolbox",
            "性能测试", "后台下载", "my products", "我的产品", "configuration wizard",
            "mode change", "iscsi", "语言首选项", "language preferences", "快速启动",
            "on-screen keyboard", "narrator", "magnify", "livecaptions", "voiceaccess",
            "steps recorder", "performance analyzer", "performance recorder", "gpuview",
            "windows media player legacy", "powershell ise",
            // 开发 / 命令行 / 模板类附属工具：用户明确点名不要
            "git bash", "git cmd", "git gui", "模板编制", "模板编辑器", "模板编辑",
            "e-manual", "e-manual viewer", "manual viewer", "技术文档", "示例"
        };

        // 系统自带的命令行 / 管理控制台：这些不该当成"应用"放进抽屉
        private static readonly string[] SystemExe =
        {
            "cmd.exe", "powershell.exe", "pwsh.exe", "mmc.exe", "control.exe", "dfrgui.exe",
            "cleanmgr.exe", "charmap.exe", "regedit.exe", "taskmgr.exe", "msconfig.exe",
            "msinfo32.exe", "perfmon.exe", "eventvwr.exe", "services.msc", "diskmgmt.msc",
            "devmgmt.msc", "compmgmt.msc", "comexp.msc", "dcomcnfg.exe", "odbcad32.exe",
            "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "winver.exe",
            "systempropertiesadvanced.exe", "systempropertiesperformance.exe"
        };

        public static List<AppItem> ScanAll()
        {
            List<AppItem> result = new List<AppItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<string> dirs = new List<string>();
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"));

            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                ScanDir(dir, result, seen, 0);
            }
            return result;
        }

        public static List<AppItem> ScanDesktopShortcuts()
        {
            List<AppItem> result = new List<AppItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ScanDir(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), result, seen, 1);
            ScanDir(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), result, seen, 1);
            return result;
        }

        // 名称归一化：去掉" - 快捷方式"/".exe"/空格，用来去掉同一程序的重复入口
        public static string NameKey(string name)
        {
            if (name == null) return "";
            string n = name.Trim();
            string[] tails = { " - 快捷方式", "- 快捷方式", " 快捷方式", " - shortcut", "- shortcut" };
            foreach (string t in tails)
            {
                if (n.EndsWith(t, StringComparison.OrdinalIgnoreCase))
                {
                    n = n.Substring(0, n.Length - t.Length).Trim();
                    break;
                }
            }
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4).Trim();
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in n)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static void ScanDir(string dir, List<AppItem> result, HashSet<string> seen, int depth)
        {
            if (depth > 6 || !Directory.Exists(dir)) return;
            FileInfo[] files;
            try { files = new DirectoryInfo(dir).GetFiles(); }
            catch { return; }

            foreach (FileInfo fi in files)
            {
                if (!fi.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !fi.Extension.Equals(".url", StringComparison.OrdinalIgnoreCase)) continue;

                string name = Path.GetFileNameWithoutExtension(fi.Name);
                if (IsNoise(name)) continue;
                if (seen.Contains("n:" + name.ToLowerInvariant())) continue;
                if (seen.Contains("k:" + NameKey(name))) continue;

                string target = null;
                string args = null;
                if (fi.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    Shortcuts.Resolve(fi.FullName, out target, out args);
                }
                if (string.IsNullOrEmpty(target))
                {
                    // 指向 UWP / shell 的快捷方式，直接交给 shell 启动
                    target = fi.FullName;
                }
                // 只保留真正的可执行程序：滤掉文件夹/.msc/.cpl/URL 之类的系统项
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsSystemExe(target)) continue;
                string tkey = "t:" + target.ToLowerInvariant();
                if (seen.Contains(tkey)) continue;

                AppItem item = new AppItem();
                item.Name = name;
                item.Target = target;
                if (fi.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) item.Shortcut = fi.FullName;
                item.Arguments = args;
                item.IconSource = target;
                item.Pinned = false;
                result.Add(item);
                seen.Add("n:" + name.ToLowerInvariant());
                seen.Add("k:" + NameKey(name));
                seen.Add(tkey);
            }

            DirectoryInfo[] subs = null;
            try { subs = new DirectoryInfo(dir).GetDirectories(); }
            catch { subs = null; }
            if (subs != null)
            {
                foreach (DirectoryInfo sub in subs)
                {
                    ScanDir(sub.FullName, result, seen, depth + 1);
                }
            }
        }

        public static bool IsNoise(string name)
        {
            string n = name.ToLowerInvariant();
            foreach (string bad in Noise)
            {
                if (n.Contains(bad)) return true;
            }
            return false;
        }

        private static bool IsSystemExe(string target)
        {
            string exe = Path.GetFileName(target).ToLowerInvariant();
            foreach (string bad in SystemExe)
            {
                if (exe == bad) return true;
            }
            return false;
        }
    }

    // 抽屉顶部的分类页签。默认按关键词自动归类，右键改过就按 categories.json 里的来。
    internal static class AppCategory
    {
        public static readonly string[] All = { "全部", "常用", "办公", "开发", "设计", "游戏", "工控", "系统", "自带", "其他" };

        private static readonly string[] Office =
        {
            "word", "excel", "powerpoint", "onenote", "outlook", "publisher", "access", "office",
            "wps", "金山", "pdf", "acrobat", "foxit", "福昕", "xmind", "mindmaster", "visio",
            "钉钉", "飞书", "微信", "wechat", "qq", "企业微信", "tim", "teams", "zoom", "腾讯会议",
            "便笺", "sticky", "印象笔记", "evernote", "notion", "obsidian", "typora", "记事本", "notepad",
            "百度网盘", "阿里云盘", "onedrive", "坚果云", "迅雷", "夸克", "豆包", "chatbox",
            "e-manual", "网易云", "qqmusic", "spotify", "potplayer", "vlc", "播放器", "媒体播放",
            "计算器", "calculator", "截图", "snipping", "画笔", "paint", "照片", "photos", "画图",
            "日历", "邮件", "邮箱", "翻译", "词典", "桌面女友", "edge", "chrome", "firefox", "浏览器",
            "airdeck", "to do", "power automate", "clipchamp", "手机连接", "录音机", "时钟", "remote desktop", "远程桌面"
        };

        private static readonly string[] Dev =
        {
            "visual studio", "vscode", "vs code", "code", "git", "github", "gitlab", "sourcetree",
            "codex", "dsh", "deepseek", "claude", "chatgpt", "copilot", "cursor", "ollama", "cc switch",
            "arduino", "python", "idle", "pycharm", "keil", "platformio", "stm32", "esp32",
            "node", "npm", "docker", "wsl", "ubuntu", "终端",
            "xshell", "putty", "mobaxterm", "navicat", "dbeaver", "postman", "apifox", "redis", "mysql",
            "jetbrains", "intellij", "webstorm", "rider", "unity", "unreal", "godot",
            "deskbox", "agent", "串口", "serial", "winhex", "ida", "wireshark", "virbox"
        };

        private static readonly string[] Design =
        {
            "solidworks", "edrawings", "autocad", "cad", "fusion", "inventor", "creo", "proe",
            "catia", "siemens nx", "sketchup", "blender", "3ds", "keyshot", "rhino", "犀牛",
            "ansys", "abaqus", "comsol", "solid edge", "中望", "浩辰", "caxa", "开目",
            "photoshop", "illustrator", "figma", "adobe", "剪映", "capcut", "达芬奇", "davinci",
            "建模", "渲染", "工程图", "模具", "钣金", "属性标签"
        };

        private static readonly string[] Game =
        {
            "steam", "epic", "ea app", "battle.net", "ubisoft", "uplay", "gog", "wegame",
            "鹰角", "hypergryph", "arknight", "endfield", "明日方舟", "终末地", "鸣潮", "特工",
            "hearts of iron", "taptap", "tap tap", "vortex", "nexus mods", "alice in cradle",
            "galgame", "saves & logs", "curseforge", "加速器", "雷神", "迅游", "奇游",
            "dlss", "游戏", "游戏加加", "小黑盒", "天选姬", "华硕大厅", "armoury crate",
            "kingdom rush", "left 4 dead", "slay the spire", "matchmaking", "paradox", "nexus", "sinisistar"
        };

        private static readonly string[] System =
        {
            "nvidia", "intel", "realtek", "defender", "rapid storage", "translucenttb",
            "clash", "watt toolkit", "steam++", "7-zip", "解压", "解压专家",
            "asus", "华硕", "glidex", "screenmaker", "电脑管家", "管家",
            "显卡", "声卡", "音频", "驱动", "控制中心", "terminal", "powershell", "cmd"
        };

        private static readonly string[] Industrial =
        {
            "gx works", "gx developer", "itp", "hmi", "inotouch", "威纶通", "威纶",
            "autoshop", "汇川", "inovance", "step 7", "step7", "micro/win", "tia portal", "博途", "wincc",
            "mcgs", "昆仑通态", "组态", "触摸屏", "plc", "欧姆龙", "cx-one", "sysmac", "三菱", "siemens",
            "codesys", "twincat", "beckhoff", "robotstudio", "abb", "kuka", "fanuc", "epson rc",
            "mr configurator", "伺服", "变频", "eplan", "electric p8", "电气", "仿真运行环境",
            "vnc", "teamviewer", "anydesk", "远程", "modbus", "串口调试"
        };

        // Windows 自带的那批（设置 / 相机 / 商店 / 记事本 / 计算器 …）单独成组：
        // 它们混在"办公""其他"里最扎眼，但也确实是系统的一部分，所以给一个自己的页签。
        public static bool IsBuiltin(AppItem item)
        {
            if (item == null) return false;

            if (string.Equals(item.Source, "uwp", StringComparison.OrdinalIgnoreCase))
            {
                string a = (item.Target ?? item.Launch ?? "").ToLowerInvariant();
                int cut = a.LastIndexOf('\\');
                if (cut >= 0) a = a.Substring(cut + 1);
                return a.StartsWith("microsoft", StringComparison.Ordinal)
                    || a.StartsWith("windows", StringComparison.Ordinal)
                    || a.StartsWith("msteams", StringComparison.Ordinal);   // Teams 是随系统预装的，但包名不带 microsoft
            }

            string t = (item.Target ?? "").ToLowerInvariant();
            if (t.Length == 0) return false;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLowerInvariant();
            // MSI 装的第三方软件（SolidWorks / eDrawings 这类）快捷方式常指向 C:\Windows\Installer\{GUID}\…，
            // 那是安装缓存的占位 exe，不是系统自带，别把它们收进「自带」。
            if (win.Length > 0 && t.StartsWith(win + "\\installer\\", StringComparison.Ordinal)) return false;
            if (t.StartsWith("%windir%", StringComparison.Ordinal) || t.StartsWith("%systemroot%", StringComparison.Ordinal)) return true;
            return win.Length > 0 && t.StartsWith(win + "\\", StringComparison.Ordinal);
        }

        // 分类真的有用：先看右键覆盖，再按关键词猜；都猜不出才落"其他"
        public static string Classify(AppItem item, Dictionary<string, string> overrides)
        {
            if (item == null || string.IsNullOrEmpty(item.Name)) return "其他";
            string ov;
            if (overrides != null && overrides.TryGetValue(item.Name, out ov) && !string.IsNullOrEmpty(ov)) return ov;

            if (IsBuiltin(item)) return "自带";

            string n = item.Name.ToLowerInvariant();
            string stem = "";
            if (!string.IsNullOrEmpty(item.Target)) stem = Path.GetFileNameWithoutExtension(item.Target).ToLowerInvariant();
            string hay = n + " | " + stem;

            if (Match(hay, Design)) return "设计";
            if (Match(hay, Industrial)) return "工控";
            if (Match(hay, Game)) return "游戏";
            if (Match(hay, Dev)) return "开发";
            if (Match(hay, Office)) return "办公";
            if (Match(hay, System)) return "系统";
            if (n == "ea" || n == "ea app") return "游戏";
            return "其他";
        }

        // "常用"不是静态归类，而是"钉在栏上的 + 真正用过的"
        public static bool IsCommon(AppItem item)
        {
            if (item == null) return false;
            if (item.Pinned) return true;
            return ConfigStore.UsageCount(item.Name) > 0;
        }

        private static bool Match(string n, string[] keys)
        {
            foreach (string k in keys)
            {
                if (n.Contains(k)) return true;
            }
            return false;
        }
    }

    internal static class ConfigStore
    {
        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopDock"); }
        }

        public static string ItemsPath
        {
            get { return Path.Combine(Dir, "items.json"); }
        }

        public static string HiddenPath
        {
            get { return Path.Combine(Dir, "hidden.json"); }
        }

        public static string CategoryPath
        {
            get { return Path.Combine(Dir, "categories.json"); }
        }

        public static string UsagePath
        {
            get { return Path.Combine(Dir, "usage.json"); }
        }

        public static string GamesPath
        {
            get { return Path.Combine(Dir, "games.json"); }
        }

        public static string ShellsPath
        {
            get { return Path.Combine(Dir, "shells.json"); }
        }

        public static string SettingsPath
        {
            get { return Path.Combine(Dir, "settings.json"); }
        }

        // 外观 / 行为设置（设置窗口写的就是这个文件）
        internal class DockSettings
        {
            public double IconSize { get; set; }
            public double BarHeight { get; set; }
            public int TintTop { get; set; }
            public int TintBottom { get; set; }
            public bool UseIconPack { get; set; }
            public bool ShowWidgets { get; set; }
        // true = 小组件只在桌面/栏展开时出现，不压住其它窗口
        public bool WidgetsDesktopOnly { get; set; }
            // 回到桌面时自动亮出 dock（false = 只能手点把手 / 托盘 / 热角叫出）
            public bool AutoShowOnDesktop { get; set; }
            public int WidgetX { get; set; }
            public int WidgetY { get; set; }
            // 应用自动发现：来源开关 + 自动刷新间隔（分钟，0=关）
            public bool ScanUwp { get; set; }
            public bool ScanSteam { get; set; }
            public bool ScanDeep { get; set; }
            public int AutoRefreshMinutes { get; set; }
            // 毛玻璃：模糊半径 + 抓屏下采样（对应 AirDeck 的 DockFrostRadius / DockFrostDownscale）
            public double FrostRadius { get; set; }
            public int FrostDownscale { get; set; }
            // 热角：四角动作，空串=不触发，可选 drawer / dock
            public bool HotCorners { get; set; }
            public string HotCornerTL { get; set; }
            public string HotCornerTR { get; set; }
            public string HotCornerBL { get; set; }
            public string HotCornerBR { get; set; }

            public DockSettings()
            {
                IconSize = 52;
                BarHeight = 96;
                TintTop = 0x36;
                TintBottom = 0x62;
                UseIconPack = true;
                ShowWidgets = true;
            WidgetsDesktopOnly = true;
                AutoShowOnDesktop = true;
                WidgetX = -1;
                WidgetY = -1;
                ScanUwp = true;
                ScanSteam = true;
                ScanDeep = false;
                AutoRefreshMinutes = 10;
                FrostRadius = 16;
                FrostDownscale = 2;
                HotCorners = true;
                HotCornerTL = "";
                HotCornerTR = "";
                HotCornerBL = "";
                HotCornerBR = "";
            }
        }

        public static DockSettings LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new DockSettings();
                JavaScriptSerializer ser = new JavaScriptSerializer();
                DockSettings s = ser.Deserialize<DockSettings>(File.ReadAllText(SettingsPath, System.Text.Encoding.UTF8));
                if (s == null) return new DockSettings();
                if (s.IconSize < 24 || s.IconSize > 96) s.IconSize = 52;
                if (s.BarHeight < s.IconSize || s.BarHeight > 180) s.BarHeight = 96;
                if (s.TintTop < 0x10 || s.TintTop > 0xF0) s.TintTop = 0x36;
                if (s.TintBottom < 0x10 || s.TintBottom > 0xF0) s.TintBottom = 0x62;
                if (s.AutoRefreshMinutes < 0 || s.AutoRefreshMinutes > 240) s.AutoRefreshMinutes = 10;
                if (s.FrostRadius < 4 || s.FrostRadius > 48) s.FrostRadius = 16;
                if (s.FrostDownscale < 2 || s.FrostDownscale > 4) s.FrostDownscale = 2;
                return s;
            }
            catch { return new DockSettings(); }
        }

        public static void SaveSettings(DockSettings s)
        {
            try
            {
                if (s == null) return;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(SettingsPath, ser.Serialize(s), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        public static void SaveGames(string[] names)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(GamesPath, ser.Serialize(names == null ? new string[0] : names), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        // 游戏/高性能模式名单：进程名（可带 .exe，大小写无关）。
        // 不在名单里但"独占全屏"的程序也会触发游戏模式，所以这里只需要补窗口化的游戏。
        // 桌面外壳名单：这些进程在前台时当成"桌面"。
        // 本机实测：装了 AirDeck 之后，Win+D 出来的前台是 AirDeck 的全屏层而不是 Progman，
        // 旧逻辑把它当全屏应用 → 游戏模式 → dock 和叫出把手一起消失。
        // 想临时关掉就把 shells.json 改成 []。
        public static string[] LoadShells()
        {
            string[] def = { "AirDeck" };
            try
            {
                if (!File.Exists(ShellsPath))
                {
                    JavaScriptSerializer ser0 = new JavaScriptSerializer();
                    File.WriteAllText(ShellsPath, ser0.Serialize(def), new System.Text.UTF8Encoding(false));
                    return def;
                }
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string[] got = ser.Deserialize<string[]>(File.ReadAllText(ShellsPath, System.Text.Encoding.UTF8));
                if (got == null) return def;
                return got;
            }
            catch { return def; }
        }

        public static string[] LoadGames()
        {
            string[] def =
            {
                "Arknights", "Endfield", "Wuthering Waves", "Client-Win64-Shipping",
                "cs2", "csgo", "VALORANT", "GenshinImpact", "YuanShen", "StarRail",
                "Overwatch", "Hearthstone", "DNF", "tgp", "LeagueClient", "LeagueClientUx",
                "DeltaForceClient-Win64-Shipping", "Game", "UnityCrashHandler64"
            };
            try
            {
                if (!File.Exists(GamesPath))
                {
                    JavaScriptSerializer ser0 = new JavaScriptSerializer();
                    File.WriteAllText(GamesPath, ser0.Serialize(def), new System.Text.UTF8Encoding(false));
                    return def;
                }
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string[] got = ser.Deserialize<string[]>(File.ReadAllText(GamesPath, System.Text.Encoding.UTF8));
                if (got == null || got.Length == 0) return def;
                return got;
            }
            catch { return def; }
        }

        // 点击次数（按应用名存：名字 → 次数）。抽屉按这个从多到少排。
        private static Dictionary<string, int> usageCache;

        public static Dictionary<string, int> LoadUsage()
        {
            if (usageCache != null) return usageCache;
            try
            {
                if (!File.Exists(UsagePath))
                {
                    usageCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    return usageCache;
                }
                string json = File.ReadAllText(UsagePath, System.Text.Encoding.UTF8);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, int> map = ser.Deserialize<Dictionary<string, int>>(json);
                usageCache = map == null
                    ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                usageCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
            return usageCache;
        }

        public static int UsageCount(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            int n;
            return LoadUsage().TryGetValue(name, out n) ? n : 0;
        }

        // 点一次记一次，立刻落盘（数量很小，不心疼）
        public static void BumpUsage(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                Dictionary<string, int> map = LoadUsage();
                int n;
                map.TryGetValue(name, out n);
                map[name] = n + 1;
                Directory.CreateDirectory(Dir);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(UsagePath, ser.Serialize(map), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        // 用户手动改过的分类（按应用名存：名字 → 分类）
        public static Dictionary<string, string> LoadCategories()
        {
            try
            {
                if (!File.Exists(CategoryPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                string json = File.ReadAllText(CategoryPath, System.Text.Encoding.UTF8);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, string> map = ser.Deserialize<Dictionary<string, string>>(json);
                return map == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public static void SaveCategories(Dictionary<string, string> map)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(CategoryPath, ser.Serialize(map), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        // 用户手动右键藏起来的条目（按名字存，重扫也不会跑回来）
        public static List<string> LoadHidden()
        {
            try
            {
                if (!File.Exists(HiddenPath)) return new List<string>();
                string json = File.ReadAllText(HiddenPath, System.Text.Encoding.UTF8);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                List<string> list = ser.Deserialize<List<string>>(json);
                return list == null ? new List<string>() : list;
            }
            catch
            {
                return new List<string>();
            }
        }

        public static void SaveHidden(List<string> names)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(HiddenPath, ser.Serialize(names), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        // 常驻栏的图标顺序（拖动排序后存下来，名字即稳定标识）
        public static string PinOrderPath
        {
            get { return Path.Combine(Dir, "pinorder.json"); }
        }

        public static List<string> LoadPinOrder()
        {
            try
            {
                if (!File.Exists(PinOrderPath)) return new List<string>();
                string json = File.ReadAllText(PinOrderPath, System.Text.Encoding.UTF8);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                List<string> list = ser.Deserialize<List<string>>(json);
                return list == null ? new List<string>() : list;
            }
            catch
            {
                return new List<string>();
            }
        }

        public static void SavePinOrder(List<string> names)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(PinOrderPath, ser.Serialize(names), new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        public static List<AppItem> Load()
        {
            try
            {
                if (!File.Exists(ItemsPath)) return null;
                string json = File.ReadAllText(ItemsPath, System.Text.Encoding.UTF8);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                List<AppItem> list = ser.Deserialize<List<AppItem>>(json);
                return list;
            }
            catch
            {
                return null;
            }
        }

        public static void Save(List<AppItem> items)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(items);
                string tmp = ItemsPath + ".tmp";
                File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(false));
                if (File.Exists(ItemsPath))
                {
                    string bak = ItemsPath + ".bak";
                    try { File.Delete(bak); } catch { }
                    File.Replace(tmp, ItemsPath, bak);
                }
                else
                {
                    File.Move(tmp, ItemsPath);
                }
            }
            catch
            {
            }
        }
    }

    internal enum LaunchResult
    {
        Activated,
        Launched,
        Failed
    }

    internal static class Launcher
    {
        // 点击 = 有窗口就激活，没有才启动（这是 v1 的门控能力，先做基础版）
        public static LaunchResult ActivateOrLaunch(AppItem item, out string message)
        {
            message = null;
            IntPtr hwnd = FindWindowForItem(item);
            if (hwnd != IntPtr.Zero)
            {
                if (Native.ForceForegroundHwnd(hwnd))
                {
                    message = "已切到：" + item.Name;
                    return LaunchResult.Activated;
                }
                // 切换失败时绝不再开一个副本 —— 那才是最难受的体验
                message = "切不过去（系统前台锁定）：" + item.Name;
                return LaunchResult.Failed;
            }

            try
            {
                // 带专用启动命令的（商店应用 / Steam 游戏）优先走它：
                // Steam 游戏能正常拉起 Steam 客户端，商店应用也能被 shell 正确关联启动。
                if (!string.IsNullOrEmpty(item.Launch))
                {
                    ProcessStartInfo lp = new ProcessStartInfo();
                    lp.FileName = item.Launch;
                    lp.UseShellExecute = true;
                    Process.Start(lp);
                    message = "已启动：" + item.Name;
                    return LaunchResult.Launched;
                }

                ProcessStartInfo psi = new ProcessStartInfo();
                // 优先直接跑用户原本的快捷方式：参数、工作目录、管理员标记、UWP 关联都原样保留，
                // 不去改、也不去猜它指向哪儿。
                bool useShortcut = !string.IsNullOrEmpty(item.Shortcut) && File.Exists(item.Shortcut);
                psi.FileName = useShortcut ? item.Shortcut : item.Target;
                psi.Arguments = useShortcut ? "" : (item.Arguments == null ? "" : item.Arguments);
                string dir = Path.GetDirectoryName(psi.FileName);
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
                psi.UseShellExecute = true;
                Process.Start(psi);
                message = "已启动：" + item.Name;
                return LaunchResult.Launched;
            }
            catch (Exception ex)
            {
                message = "启动失败：" + ex.Message;
                return LaunchResult.Failed;
            }
        }

        public static bool IsRunning(AppItem item)
        {
            return FindWindowForItem(item) != IntPtr.Zero;
        }

        // 批量版：用一次枚举得到的进程名集合判断，避免每个图标各扫一遍窗口（性能热点，见 Native.VisibleWindowProcessNames）
        public static bool IsRunning(AppItem item, HashSet<string> procs)
        {
            if (item == null || string.IsNullOrEmpty(item.Target) || procs == null) return false;
            if (!item.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            return ProcessMatches(Path.GetFileNameWithoutExtension(item.Target), procs);
        }

        private static bool ProcessMatches(string stem, HashSet<string> procs)
        {
            if (string.IsNullOrEmpty(stem)) return false;
            if (procs.Contains(stem)) return true;
            foreach (string p in procs)
            {
                if (p.Length == 0) continue;
                if (p.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // 判断某个应用是不是"当前前台"（滚轮轮换时用来定位起点）
        public static bool IsForegroundApp(AppItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Target)) return false;
            if (!item.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            string stem = Path.GetFileNameWithoutExtension(item.Target);
            string cur = Native.ProcessNameOfWindow(Native.GetForegroundWindow());
            if (string.IsNullOrEmpty(cur)) return false;
            if (string.Equals(stem, cur, StringComparison.OrdinalIgnoreCase)) return true;
            // 有的应用真正的窗口进程和快捷方式不是同一个名字（如 Chrome→chrome），放宽一次
            return cur.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith(cur, StringComparison.OrdinalIgnoreCase);
        }

        // 枚举某个应用（按进程名匹配）当前所有有标题的可见顶层窗口，最多 4 个，
        // 供"悬停图标出预览"用。前台窗口排在最前面。
        public static List<IntPtr> WindowsForItem(AppItem item)
        {
            List<IntPtr> result = new List<IntPtr>();
            if (item == null || string.IsNullOrEmpty(item.Target)) return result;
            if (!item.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return result;

            string stem = Path.GetFileNameWithoutExtension(item.Target);
            int pidSelf = Process.GetCurrentProcess().Id;
            IntPtr fg = Native.GetForegroundWindow();

            List<IntPtr> wins = Native.VisibleTopLevelWindows();
            foreach (IntPtr h in wins)
            {
                int pid = Native.GetPidOfWindow(h);
                if (pid == pidSelf) continue;
                string pname;
                try { pname = Process.GetProcessById(pid).ProcessName; }
                catch { continue; }
                if (!string.Equals(pname, stem, StringComparison.OrdinalIgnoreCase)
                    && !pname.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
                    && !stem.StartsWith(pname, StringComparison.OrdinalIgnoreCase)) continue;
                if (Native.TitleOfWindow(h).Length == 0 && !Native.IsIconic(h)) continue;
                result.Add(h);
            }
            result.Sort(delegate(IntPtr a, IntPtr b)
            {
                if (a == fg) return -1;
                if (b == fg) return 1;
                return 0;
            });
            if (result.Count > 4) result.RemoveRange(4, result.Count - 4);
            return result;
        }

        private static IntPtr FindWindowForItem(AppItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.Target)) return IntPtr.Zero;
            if (!item.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;

            string stem = Path.GetFileNameWithoutExtension(item.Target);
            List<string> names = new List<string>();
            names.Add(stem);

            int pidSelf = Process.GetCurrentProcess().Id;
            List<IntPtr> wins = Native.VisibleTopLevelWindows();
            foreach (IntPtr h in wins)
            {
                int pid = Native.GetPidOfWindow(h);
                if (pid == pidSelf) continue;
                string pname;
                try { pname = Process.GetProcessById(pid).ProcessName; }
                catch { continue; }
                foreach (string n in names)
                {
                    if (string.Equals(pname, n, StringComparison.OrdinalIgnoreCase))
                    {
                        return h;
                    }
                }
            }
            return IntPtr.Zero;
        }
    }
}
