using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopDock
{
    // 图标包：把系统自带图标换成统一风格的圆角磁贴（macOS 那种）。
    // 目录结构（和 AirDeck 的 mac-tiles 一致，可以直接指过去）：
    //   <包目录>\pack.json   { "icons": { "exe:chrome": "icons/chrome.png", ... } }
    // 没有 pack.json 时，退化成"按 png 文件名匹配 exe 名"。
    internal static class IconPack
    {
        public static bool Enabled = true;
        public static string Folder;
        public static int Count { get { return map.Count; } }

        private static Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool loaded;

        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                string dir = Folder;
                if (string.IsNullOrEmpty(dir))
                {
                    string mine = Path.Combine(ConfigStore.Dir, "icons");
                    if (File.Exists(Path.Combine(mine, "pack.json")) || Directory.Exists(mine)) dir = mine;
                }
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                Folder = dir;

                string manifest = Path.Combine(dir, "pack.json");
                if (File.Exists(manifest))
                {
                    // 用正则解析，避免 System.Web.Extensions 里对重复键的严格校验
                    string raw = File.ReadAllText(manifest, Encoding.UTF8);
                    MatchCollection ms = Regex.Matches(raw, "\"exe:([^\"]+)\"\\s*:\\s*\"([^\"]+)\"");
                    foreach (Match m in ms)
                    {
                        string exeName = m.Groups[1].Value;
                        string rel = m.Groups[2].Value.Replace('/', '\\');
                        string full = Path.Combine(dir, rel);
                        if (!File.Exists(full)) continue;
                        map[exeName] = full;
                    }
                }
                if (map.Count == 0)
                {
                    foreach (string png in Directory.GetFiles(dir, "*.png", SearchOption.AllDirectories))
                    {
                        string key = Path.GetFileNameWithoutExtension(png);
                        if (!map.ContainsKey(key)) map[key] = png;
                    }
                }
            }
            catch { }
        }

        // 这些是"通用启动器/安装器"名字，图标包里往往对应某个特定软件，
        // 硬套会张冠李戴（比如 鹰角启动器 Launcher.exe 被套成别的游戏的图标），所以直接跳过。
        private static readonly string[] generic = new string[]
        {
            "launcher", "game", "start", "startgame", "client", "updater", "update",
            "setup", "install", "installer", "helper", "bootstrap", "bootstrapper",
            "main", "app", "run", "play", "tool", "driver", "uninstall", "unins000"
        };

        private static bool IsGeneric(string exe)
        {
            string e = exe.ToLowerInvariant();
            for (int i = 0; i < generic.Length; i++)
            {
                if (e == generic[i]) return true;
            }
            return false;
        }

        // 用 .exe 文件名（不带扩展名）匹配；匹配不到再尝试去掉结尾数字（chrome1 → chrome）
        public static string Resolve(string target)
        {
            EnsureLoaded();
            if (map.Count == 0 || string.IsNullOrEmpty(target)) return null;
            string exe;
            try { exe = Path.GetFileNameWithoutExtension(target); }
            catch { return null; }
            if (string.IsNullOrEmpty(exe)) return null;
            if (IsGeneric(exe)) return null;

            string hit;
            if (map.TryGetValue(exe, out hit)) return hit;

            // 有些快捷方式指向 .lnk 里记录的旧名字，做两次宽松匹配
            string trimmed = exe.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', ' ');
            if (trimmed.Length > 0 && map.TryGetValue(trimmed, out hit)) return hit;
            if (trimmed.Length > 0 && map.TryGetValue(trimmed + "64", out hit)) return hit;

            foreach (KeyValuePair<string, string> kv in map)
            {
                if (string.Equals(kv.Key, exe, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return null;
        }
    }
}
