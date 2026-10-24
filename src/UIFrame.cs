using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Media;

namespace DesktopDock
{
    // 逐帧驱动（照 AirDeck 的 frame-driven 思路）
    // 和 DispatcherTimer 的区别：
    //   - 回调挂在 CompositionTarget.Rendering 上，跟显示器刷新对齐（60/120/165Hz 都能跑满）；
    //   - dt 用 Stopwatch 实测，掉了帧也不会"补跑几次"，动画不会突然跳；
    //   - 顺手做帧率自测，日志里能拿到 avg/min fps，避免"感觉卡"这种糊涂账。
    internal static class UIFrame
    {
        private static readonly List<Action<double>> subs = new List<Action<double>>();
        private static Stopwatch clock;
        private static bool hooked;
        private static double lastMs;

        private static int frames;
        private static double winStartMs;
        private static double sumMs;
        private static double minMs = 1e9;
        private static int nMs;

        public static bool Running { get { return hooked; } }
        public static int Count { get { return subs.Count; } }

        public static void Add(Action<double> step)
        {
            if (step == null) return;
            if (!subs.Contains(step)) subs.Add(step);
            Ensure();
        }

        public static void Remove(Action<double> step)
        {
            if (step == null) return;
            subs.Remove(step);
            if (subs.Count == 0) Stop();
        }

        private static void Ensure()
        {
            if (hooked) return;
            hooked = true;
            if (clock == null) clock = new Stopwatch();
            clock.Restart();
            lastMs = 0;
            frames = 0;
            winStartMs = 0;
            sumMs = 0;
            minMs = 1e9;
            nMs = 0;
            CompositionTarget.Rendering += OnRendering;
        }

        private static void Stop()
        {
            if (!hooked) return;
            hooked = false;
            try { CompositionTarget.Rendering -= OnRendering; } catch { }
            Report("stop");
        }

        private static void OnRendering(object sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            double dt = now - lastMs;
            lastMs = now;
            if (dt <= 0 || dt > 250) dt = 16.7;     // 首帧/暂停回来后的异常值丢掉
            frames++;
            sumMs += dt;
            nMs++;
            if (dt < minMs) minMs = dt;

            // 每秒报一次真实帧率
            if (now - winStartMs >= 1000)
            {
                double span = now - winStartMs;
                double fps = frames * 1000.0 / span;
                if (frames > 0 && Program.VerboseFrame)
                {
                    Program.Log("ui frame: " + fps.ToString("0") + "fps (avg=" + (sumMs / Math.Max(1, nMs)).ToString("0.0")
                        + "ms, worst=" + minMs.ToString("0.0") + "ms, subs=" + subs.Count.ToString() + ")");
                }
                frames = 0;
                winStartMs = now;
                sumMs = 0; nMs = 0; minMs = 1e9;
            }

            // 复制一份再遍历：回调里可能 Remove 自己
            Action<double>[] list = subs.ToArray();
            for (int i = 0; i < list.Length; i++)
            {
                try { list[i](dt); } catch (Exception ex) { Program.Log("ui frame step failed: " + ex.Message); }
            }
        }

        private static void Report(string why)
        {
            if (nMs <= 0) return;
            double avg = sumMs / nMs;
            Program.Log("ui frame " + why + ": avg=" + avg.ToString("0.0") + "ms (" + (1000.0 / Math.Max(0.1, avg)).ToString("0")
                + "fps), dt_min=" + minMs.ToString("0.0") + "ms, frames=" + nMs.ToString());
        }

        // 这段时间里进程占到多少 CPU（单核百分比）。>80% = 真是我们的渲染/逻辑吃满了；
        // 低就是被垂直同步或别的东西挡着，优化方向完全不同。
        private static double CpuPercent(TimeSpan cpu0, double elapsedMs)
        {
            try
            {
                if (elapsedMs < 1) return 0;
                TimeSpan cpu1 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                return (cpu1 - cpu0).TotalMilliseconds / elapsedMs * 100.0;
            }
            catch { return 0; }
        }

        private static string Join(System.Collections.Generic.List<double> list)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(" ");
                sb.Append(list[i].ToString("0.0"));
            }
            return sb.ToString();
        }

        // 动画窗口期的一次性帧率采样：用于"打开顺不顺"这种验收，不看感觉看数字。
        // 只挂 ms 毫秒就自己摘掉；记录真实帧间隔的 avg / worst（worst 大 = 卡了一下）。
        public static void Bench(string tag, int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            double last = 0, sum = 0, worst = 0;
            int frames = 0;
            TimeSpan cpu0 = TimeSpan.Zero;
            try { cpu0 = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime; } catch { }
            // 把"卡在哪一帧"也记下来：前 10 个间隔 + 任何超过 20ms 的间隔（最多 40 条），
            // 只报 avg/worst 分不清是开头要传纹理，还是中途被别的东西打断。
            System.Text.StringBuilder gaps = new System.Text.StringBuilder();
            int noted = 0;
            System.Collections.Generic.List<double> tail = new System.Collections.Generic.List<double>();
            EventHandler h = null;
            h = delegate(object s, EventArgs e)
            {
                double now = sw.Elapsed.TotalMilliseconds;
                if (frames > 0)
                {
                    double dt = now - last;
                    if (dt > 0 && dt < 250)
                    {
                        sum += dt; if (dt > worst) worst = dt;
                        if (noted < 40 && (noted < 10 || dt > 20))
                        {
                            gaps.Append(noted == 0 ? "" : " ");
                            gaps.Append(now.ToString("0"));
                            gaps.Append("ms:");
                            gaps.Append(dt.ToString("0.0"));
                            noted++;
                        }
                        tail.Add(dt);
                        if (tail.Count > 6) tail.RemoveAt(0);
                    }
                }
                last = now;
                frames++;
                if (now >= ms)
                {
                    try { CompositionTarget.Rendering -= h; } catch { }
                    double n = frames - 1;
                    double avg = n > 0 ? sum / n : 0;
                    Program.Log("bench " + tag + ": " + frames.ToString() + " 帧 / " + ms.ToString() + "ms"
                        + (avg > 0 ? " (avg=" + avg.ToString("0.0") + "ms → " + (1000.0 / avg).ToString("0") + "fps, worst=" + worst.ToString("0.0") + "ms)" : "")
                        + (noted > 0 ? " gaps[" + gaps.ToString() + "]" : "")
                        + " tail[" + Join(tail) + "] tier=" + (System.Windows.Media.RenderCapability.Tier >> 16).ToString()
                        + " cpu=" + CpuPercent(cpu0, now).ToString("0") + "%");
                }
            };
            CompositionTarget.Rendering += h;
        }

        // ---- 缓动（只留够用的几条，名字按 Apple 那套）----
        public static double QuinticOut(double t)
        {
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double u = 1 - t;
            return 1 - u * u * u * u * u;
        }

        public static double CubicOut(double t)
        {
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double u = 1 - t;
            return 1 - u * u * u;
        }

        public static double CubicInOut(double t)
        {
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }

        // 指数趋近（临界阻尼的廉价版）：dt 越大追得越多，天然和帧率无关
        public static double Approach(double cur, double target, double dtMs, double tauMs)
        {
            if (tauMs <= 0.1) return target;
            double k = 1 - Math.Exp(-dtMs / tauMs);
            return cur + (target - cur) * k;
        }
    }
}
