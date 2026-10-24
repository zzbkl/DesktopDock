using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DesktopDock
{
    // 应用自动发现：光扫开始菜单/桌面会漏掉一大半（商店应用、Steam 游戏、
    // 装完不建快捷方式的绿色程序）。这里补三个来源，全部零依赖、只用系统自带能力。
    internal static class Discover
    {
        public static int LastUwp, LastSteam, LastDeep;

        public static List<AppItem> Collect(bool uwp, bool steam, bool deep, bool includeMenu, bool includeDesktop)
        {
            List<AppItem> all = new List<AppItem>();
            if (includeMenu) all.AddRange(AppScanner.ScanAll());
            if (includeDesktop) all.AddRange(AppScanner.ScanDesktopShortcuts());

            if (uwp)
            {
                List<AppItem> list = UwpApps.Scan();
                LastUwp = list.Count;
                all.AddRange(list);
            }
            if (steam)
            {
                List<AppItem> list = SteamApps.Scan();
                LastSteam = list.Count;
                all.AddRange(list);
            }
            if (deep)
            {
                List<AppItem> list = DeepApps.Scan();
                LastDeep = list.Count;
                all.AddRange(list);
            }
            return all;
        }
    }

    // ---- 商店应用（UWP）：走 shell:AppsFolder，启动用 explorer shell:AppsFolder\<AUMID> ----
    internal static class UwpApps
    {
        // 系统对话框 / 后台宿主，不是"应用"：扫进来只会让列表脏。
        // 注意：设置、相机、地图、商店、天气、Xbox 这些都是真应用，现在不再丢掉，
        // 改成归到"自带"页签（AppCategory.IsBuiltin 认 Microsoft/Windows 开头的 AUMID）。
        private static readonly string[] JunkAumid =
        {
            "windows.printdialog", "windows.secureassessment", "microsoft.sechealthui",
            "webexperiencehost", "pad.troubleshooter", "screenpadmaster"
        };

        public static bool IsSystemJunk(string aumid)
        {
            string a = (aumid ?? "").ToLowerInvariant();
            foreach (string j in JunkAumid)
                if (a.Contains(j)) return true;
            return false;
        }

        public static List<AppItem> Scan()
        {
            List<AppItem> result = new List<AppItem>();
            try
            {
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return result;
                object shell = Activator.CreateInstance(shellType);
                object folder = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell,
                    new object[] { "shell:AppsFolder" });
                if (folder == null) return result;
                object items = folder.GetType().InvokeMember("Items", BindingFlags.InvokeMethod, null, folder, null);
                int count = (int)items.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, items, null);

                for (int i = 0; i < count; i++)
                {
                    object entry = null;
                    try { entry = items.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, items, new object[] { i }); }
                    catch { continue; }
                    if (entry == null) continue;

                    string name = null, aumid = null;
                    try { name = (string)entry.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, entry, null); }
                    catch { }
                    try { aumid = (string)entry.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, entry, null); }
                    catch { }
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(aumid)) continue;
                    if (!aumid.Contains("!")) continue;              // 只认真正的 AUMID（xxxx!App）
                    if (AppScanner.IsNoise(name)) continue;
                    if (UwpApps.IsSystemJunk(aumid)) continue;       // 系统设置类小组件不进抽屉

                    AppItem item = new AppItem();
                    item.Name = name.Trim();
                    item.Target = "shell:AppsFolder\\" + aumid;
                    item.Launch = "shell:AppsFolder\\" + aumid;
                    item.Shortcut = "";
                    item.Source = "uwp";
                    result.Add(item);
                }
            }
            catch (Exception ex) { Program.Log("uwp scan failed: " + ex.Message); }
            return result;
        }
    }

    // ---- Steam 库：libraryfolders.vdf 找库目录，appmanifest_*.acf 找游戏 ----
    internal static class SteamApps
    {
        private static readonly string[] SkipWords =
        {
            "steamworks", "redistributable", "proton", "steam linux runtime", "steamvr",
            "wallpaper engine", "steam client", "steam 中国", "soundtrack", "dedicated server"
        };

        public static List<AppItem> Scan()
        {
            List<AppItem> result = new List<AppItem>();
            try
            {
                string steamRoot = FindSteam();
                if (string.IsNullOrEmpty(steamRoot)) return result;

                List<string> libs = new List<string>();
                libs.Add(steamRoot);
                string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf))
                {
                    string text = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
                    {
                        string p = m.Groups[1].Value.Replace("\\\\", "\\");
                        if (Directory.Exists(p) && !libs.Contains(p)) libs.Add(p);
                    }
                }

                foreach (string lib in libs)
                {
                    string appsDir = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(appsDir)) continue;
                    foreach (string acf in Directory.GetFiles(appsDir, "appmanifest_*.acf"))
                    {
                        AppItem item = ParseAcf(acf, lib);
                        if (item != null) result.Add(item);
                    }
                }
            }
            catch (Exception ex) { Program.Log("steam scan failed: " + ex.Message); }
            return result;
        }

        private static AppItem ParseAcf(string acf, string lib)
        {
            try
            {
                string text = File.ReadAllText(acf);
                string appid = Match(text, "\"appid\"\\s*\"(\\d+)\"");
                string name = Match(text, "\"name\"\\s*\"([^\"]+)\"");
                string dir = Match(text, "\"installdir\"\\s*\"([^\"]+)\"");
                if (string.IsNullOrEmpty(appid) || string.IsNullOrEmpty(name)) return null;
                string low = name.ToLowerInvariant();
                foreach (string w in SkipWords) if (low.Contains(w)) return null;
                if (AppScanner.IsNoise(name)) return null;

                string exe = "";
                if (!string.IsNullOrEmpty(dir))
                {
                    string gameDir = Path.Combine(lib, "steamapps", "common", dir);
                    exe = BestExe(gameDir, name);
                }

                AppItem item = new AppItem();
                item.Name = name;
                item.Target = string.IsNullOrEmpty(exe) ? ("steam://rungameid/" + appid) : exe;
                item.Launch = "steam://rungameid/" + appid;
                item.Shortcut = "";
                item.Source = "steam";
                return item;
            }
            catch { return null; }
        }

        // 游戏目录里挑一个"最像主程序"的 exe：优先名字接近游戏名，其次体积最大
        private static string BestExe(string dir, string gameName)
        {
            try
            {
                if (!Directory.Exists(dir)) return "";
                FileInfo best = null;
                foreach (string f in Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories))
                {
                    FileInfo fi = new FileInfo(f);
                    if (fi.Length < 400 * 1024) continue;                       // 太小的是启动器壳
                    string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (n.Contains("unins") || n.Contains("crash") || n.Contains("setup")) continue;
                    if (fi.FullName.Split('\\').Length - dir.Split('\\').Length > 4) continue;
                    if (best == null || fi.Length > best.Length) best = fi;
                }
                return best == null ? "" : best.FullName;
            }
            catch { return ""; }
        }

        private static string Match(string text, string pattern)
        {
            Match m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string FindSteam()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\Valve\Steam"))
                {
                    if (k != null)
                    {
                        string p = k.GetValue("SteamPath") as string;
                        if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p.Replace('/', '\\');
                    }
                }
            }
            catch { }
            string[] guesses = { @"C:\Program Files (x86)\Steam", @"D:\Steam", @"D:\SteamLibrary", @"E:\Steam" };
            foreach (string g in guesses) if (Directory.Exists(g)) return g;
            return null;
        }
    }

    // ---- 深度扫描：Program Files 里没被开始菜单收录的程序（默认关闭，用设置打开）----
    internal static class DeepApps
    {
        private static readonly string[] Roots =
        {
            @"C:\Program Files", @"C:\Program Files (x86)",
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + @"\Programs"
        };

        public static List<AppItem> Scan()
        {
            List<AppItem> result = new List<AppItem>();
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in Roots)
            {
                if (!Directory.Exists(root)) continue;
                ScanRoot(root, result, names, 0);
            }
            return result;
        }

        private static void ScanRoot(string dir, List<AppItem> result, HashSet<string> names, int depth)
        {
            if (depth > 2 || result.Count > 80) return;
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { return; }
            foreach (string sub in subdirs)
            {
                string folder = Path.GetFileName(sub);
                if (folder.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                if (folder.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase)) continue;

                string[] exes;
                try { exes = Directory.GetFiles(sub, "*.exe", SearchOption.TopDirectoryOnly); }
                catch { exes = new string[0]; }

                FileInfo best = null;
                foreach (string exe in exes)
                {
                    string stem = Path.GetFileNameWithoutExtension(exe);
                    if (AppScanner.IsNoise(stem)) continue;
                    if (stem.ToLowerInvariant().Contains("unins")) continue;
                    FileInfo fi = new FileInfo(exe);
                    if (fi.Length < 200 * 1024) continue;
                    // 名字和文件夹相似度优先，其次体积
                    if (best == null) { best = fi; continue; }
                    bool near = Similar(Path.GetFileNameWithoutExtension(best.Name), folder);
                    bool nearNew = Similar(Path.GetFileNameWithoutExtension(fi.Name), folder);
                    if (nearNew && !near) best = fi;
                    else if (nearNew == near && fi.Length > best.Length) best = fi;
                }

                if (best != null)
                {
                    string stem = Path.GetFileNameWithoutExtension(best.Name);
                    if (!names.Contains(stem.ToLowerInvariant()))
                    {
                        names.Add(stem.ToLowerInvariant());
                        AppItem item = new AppItem();
                        item.Name = stem;
                        item.Target = best.FullName;
                        item.Shortcut = "";
                        item.Source = "deep";
                        result.Add(item);
                    }
                }
                ScanRoot(sub, result, names, depth + 1);
            }
        }

        private static bool Similar(string a, string b)
        {
            string x = a.ToLowerInvariant().Replace(" ", "");
            string y = b.ToLowerInvariant().Replace(" ", "");
            return x.Contains(y) || y.Contains(x);
        }
    }
}
