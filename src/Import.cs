using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DesktopDock
{
    // 一次性导入 GlassBoard 里已有的应用清单（layout.json 的 Apps 数组），
    // 避免手打 89 个条目。字段：Name / Path / LaunchTarget / LaunchArguments / Pinned
    internal static class GlassBoardImporter
    {
        public static string LayoutPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GlassBoard", "layout.json");
            }
        }

        public class ImportResult
        {
            public List<AppItem> Pinned = new List<AppItem>();
            public List<AppItem> All = new List<AppItem>();
            public string Error;
        }

        public static ImportResult Import()
        {
            ImportResult r = new ImportResult();
            try
            {
                if (!File.Exists(LayoutPath))
                {
                    r.Error = "没有找到 " + LayoutPath;
                    return r;
                }
                string json = File.ReadAllText(LayoutPath, Encoding.UTF8);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 64 * 1024 * 1024;
                object root = ser.DeserializeObject(json);
                List<object> apps = FindAppArray(root, 0);
                if (apps == null)
                {
                    r.Error = "layout.json 里没有找到 Apps 数组";
                    return r;
                }

                foreach (object o in apps)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string name = Str(d, "Name");
                    if (string.IsNullOrEmpty(name)) continue;

                    string path = Str(d, "Path");
                    string launch = Str(d, "LaunchTarget");
                    string args = Str(d, "LaunchArguments");

                    AppItem item = new AppItem();
                    item.Name = name;
                    item.Target = string.IsNullOrEmpty(launch) ? path : launch;
                    item.Arguments = args;
                    item.IconSource = string.IsNullOrEmpty(path) ? item.Target : path;
                    item.Pinned = BoolOf(d, "Pinned");

                    r.All.Add(item);
                    // 配置/管理/更新类工具不进常驻栏 —— 用户明确要求抽屉和栏里都别放这些
                    if (item.Pinned && !AppScanner.IsNoise(name)) r.Pinned.Add(item);
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }
            return r;
        }

        private static List<object> FindAppArray(object node, int depth)
        {
            if (node == null || depth > 6) return null;

            Dictionary<string, object> d = node as Dictionary<string, object>;
            if (d != null)
            {
                foreach (KeyValuePair<string, object> kv in d)
                {
                    if (string.Equals(kv.Key, "Apps", StringComparison.OrdinalIgnoreCase))
                    {
                        IEnumerable seq = kv.Value as IEnumerable;
                        if (seq != null && !(kv.Value is string))
                        {
                            List<object> list = new List<object>();
                            foreach (object o in seq) list.Add(o);
                            if (list.Count > 0) return list;
                        }
                    }
                    List<object> nested = FindAppArray(kv.Value, depth + 1);
                    if (nested != null) return nested;
                }
                return null;
            }

            IEnumerable seq2 = node as IEnumerable;
            if (seq2 != null && !(node is string))
            {
                foreach (object o in seq2)
                {
                    List<object> nested = FindAppArray(o, depth + 1);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            foreach (KeyValuePair<string, object> kv in d)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value == null ? null : kv.Value.ToString();
                }
            }
            return null;
        }

        private static bool BoolOf(Dictionary<string, object> d, string key)
        {
            foreach (KeyValuePair<string, object> kv in d)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    if (kv.Value is bool) return (bool)kv.Value;
                    string s = kv.Value == null ? "" : kv.Value.ToString();
                    return s.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
            return false;
        }
    }
}
