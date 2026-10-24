using System;
using System.Globalization;

namespace DesktopDock
{
    // 拼音首字母：不查词典，直接用 Windows 自带的 zh-CN 排序规则（汉字在 zh-CN 下按拼音排序），
    // 用 23 个"锚点字"做二分查找就能拿到任意汉字的首字母，零数据文件、零网络。
    internal static class Pinyin
    {
        private static readonly char[] Letters = { 'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'w', 'x', 'y', 'z' };
        private static readonly string[] Anchors = { "阿", "八", "擦", "搭", "蛾", "发", "噶", "哈", "击", "喀", "拉", "妈", "拿", "哦", "啪", "期", "然", "撒", "塌", "挖", "昔", "压", "匝" };

        private static readonly CompareInfo Cmp = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;

        private static char InitialOf(char c)
        {
            if (c >= 'a' && c <= 'z') return c;
            if (c >= 'A' && c <= 'Z') return (char)(c + 32);
            if (c >= '0' && c <= '9') return c;
            if (c < 0x3400) return '\0';

            // 二分：找最后一个 <= c 的锚点
            int lo = 0, hi = Anchors.Length - 1, best = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Cmp.Compare(Anchors[mid], c.ToString()) <= 0) { best = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (best < 0) return '\0';
            return Letters[best];
        }

        // "微信" -> "wx"；"DSH Desktop" -> "dshe"（英文按原字母、跳过标点与空格）
        public static string Initials(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = InitialOf(text[i]);
                if (c != '\0') sb.Append(c);
            }
            return sb.ToString();
        }

        // 搜索判定：名称子串命中，或"拼音首字母"里包含查询串
        public static bool Matches(string name, string loweredQuery)
        {
            if (string.IsNullOrEmpty(loweredQuery)) return true;
            string n = (name == null ? "" : name).ToLowerInvariant();
            if (n.Contains(loweredQuery)) return true;
            string ini = Initials(name == null ? "" : name);
            return ini.Length > 0 && ini.Contains(loweredQuery);
        }
    }
}
