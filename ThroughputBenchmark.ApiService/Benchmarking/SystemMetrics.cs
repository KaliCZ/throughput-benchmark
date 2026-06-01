using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace ThroughputBenchmark.ApiService.Benchmarking;

/// <summary>Host system metrics sampled alongside throughput. Any value may be null when the
/// platform doesn't expose it. <see cref="OnAcPower"/> is true when plugged into AC (used to
/// invalidate the battery-drain figure if the machine was charging during a run).</summary>
public sealed record SystemSnapshot(double? CpuPercent, int? BatteryPercent, bool? OnAcPower);

/// <summary>
/// Reads system-wide CPU usage, battery charge % and AC-power state, per OS:
///   * Windows — GetSystemTimes + GetSystemPowerStatus.
///   * macOS   — host_statistics (Mach) for CPU, ioreg (AppleSmartBattery) for charge % / AC.
///   * Linux   — /proc/stat for CPU, /sys/class/power_supply for charge % / AC (best-effort).
/// CPU% is a delta since the previous call, so it needs two ticks to produce a value.
/// Not thread-safe — only the sampler calls <see cref="Read"/>, once a tick.
/// </summary>
public sealed class SystemMetrics
{
    // ---- shared CPU delta (cumulative busy/total tick counters -> % over the interval) ----
    private ulong _prevBusy, _prevTotal;
    private bool _havePrev;

    private double? CpuFromCumulative((ulong busy, ulong total)? sample)
    {
        if (sample is not { } s) return null;
        if (!_havePrev)
        {
            (_prevBusy, _prevTotal, _havePrev) = (s.busy, s.total, true);
            return null; // need two samples to compute a delta
        }
        ulong db = s.busy - _prevBusy, dt = s.total - _prevTotal;
        (_prevBusy, _prevTotal) = (s.busy, s.total);
        if (dt == 0) return null;
        return Math.Round(Math.Clamp((double)db / dt * 100.0, 0, 100), 1);
    }

    public SystemSnapshot Read()
    {
        if (OperatingSystem.IsWindows())
        {
            var (battery, onAc) = ReadWindowsBattery();
            return new SystemSnapshot(CpuFromCumulative(ReadWindowsCpuTicks()), battery, onAc);
        }
        if (OperatingSystem.IsMacOS())
        {
            var (battery, onAc) = ReadMacBattery();
            return new SystemSnapshot(CpuFromCumulative(ReadMacCpuTicks()), battery, onAc);
        }
        if (OperatingSystem.IsLinux())
        {
            var (battery, onAc) = ReadLinuxBattery();
            return new SystemSnapshot(CpuFromCumulative(ReadLinuxCpuTicks()), battery, onAc);
        }
        return new SystemSnapshot(null, null, null);
    }

    // ===================== Windows =====================

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    private static ulong ToUlong(FileTime f) => ((ulong)f.High << 32) | f.Low;

    [SupportedOSPlatform("windows")]
    private static (ulong busy, ulong total)? ReadWindowsCpuTicks()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;
        ulong i = ToUlong(idle), k = ToUlong(kernel), u = ToUlong(user);
        ulong total = k + u;     // kernel time already includes idle
        return (total - i, total);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;        // 0 = on battery, 1 = on AC, 255 = unknown
        public byte BatteryFlag;
        public byte BatteryLifePercent;  // 0-100, 255 = unknown
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [SupportedOSPlatform("windows")]
    private static (int? percent, bool? onAc) ReadWindowsBattery()
    {
        if (!GetSystemPowerStatus(out var s)) return (null, null);
        int? percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        bool? onAc = s.ACLineStatus == 255 ? null : s.ACLineStatus == 1;
        return (percent, onAc);
    }

    // ===================== macOS =====================

    private const int HOST_CPU_LOAD_INFO = 3;          // mach/host_info.h
    private const int HOST_CPU_LOAD_INFO_COUNT = 4;    // user, system, idle, nice

    [DllImport("libSystem.dylib")]
    private static extern uint mach_host_self();

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics(uint hostPort, int flavor, uint[] info, ref int infoCount);

    [SupportedOSPlatform("macos")]
    private static (ulong busy, ulong total)? ReadMacCpuTicks()
    {
        try
        {
            var info = new uint[HOST_CPU_LOAD_INFO_COUNT];
            int count = HOST_CPU_LOAD_INFO_COUNT;
            if (host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, info, ref count) != 0)
                return null;
            ulong user = info[0], system = info[1], idle = info[2], nice = info[3];
            ulong busy = user + system + nice;
            return (busy, busy + idle);
        }
        catch { return null; }
    }

    [SupportedOSPlatform("macos")]
    private static (int? percent, bool? onAc) ReadMacBattery()
    {
        var outp = RunCli("/usr/sbin/ioreg", "-rn AppleSmartBattery -w0");
        if (outp is null) return (null, null);

        int? cur = MatchInt(outp, "\"CurrentCapacity\"\\s*=\\s*(\\d+)");
        int? max = MatchInt(outp, "\"MaxCapacity\"\\s*=\\s*(\\d+)");
        // On Intel these are mAh; on Apple Silicon CurrentCapacity is already a %. The ratio
        // handles both (ARM: MaxCapacity=100, so cur*100/100 = cur).
        int? percent = (cur is int c && max is int m && m > 0) ? (int)Math.Round(c * 100.0 / m) : cur;
        bool? onAc = Regex.IsMatch(outp, "\"ExternalConnected\"\\s*=\\s*Yes");
        return (percent, onAc);
    }

    // ===================== Linux (best-effort) =====================

    private static (ulong busy, ulong total)? ReadLinuxCpuTicks()
    {
        try
        {
            // First line of /proc/stat: "cpu  user nice system idle iowait irq softirq steal ..."
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (line is null || !line.StartsWith("cpu ")) return null;
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ulong V(int i) => i < p.Length && ulong.TryParse(p[i], out var x) ? x : 0;
            ulong user = V(1), nice = V(2), system = V(3), idle = V(4),
                  iowait = V(5), irq = V(6), softirq = V(7), steal = V(8);
            ulong idleAll = idle + iowait;
            ulong busy = user + nice + system + irq + softirq + steal;
            return (busy, idleAll + busy);
        }
        catch { return null; }
    }

    private static (int? percent, bool? onAc) ReadLinuxBattery()
    {
        int? percent = ReadIntFile("/sys/class/power_supply/BAT0/capacity");
        int? ac = ReadIntFile("/sys/class/power_supply/AC/online")
               ?? ReadIntFile("/sys/class/power_supply/ACAD/online")
               ?? ReadIntFile("/sys/class/power_supply/AC0/online");
        return (percent, ac is int a ? a == 1 : null);
    }

    // ===================== helpers =====================

    private static string? RunCli(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            string outp = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return null; }
            return outp;
        }
        catch { return null; }
    }

    private static int? MatchInt(string s, string pattern)
        => Regex.Match(s, pattern) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var v) ? v : null;

    private static int? ReadIntFile(string path)
    {
        try { return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : null; }
        catch { return null; }
    }
}
