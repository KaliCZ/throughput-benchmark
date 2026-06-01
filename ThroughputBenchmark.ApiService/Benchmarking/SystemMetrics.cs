using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace ThroughputBenchmark.ApiService.Benchmarking;

/// <summary>Host system metrics sampled alongside throughput. Any value may be null when the
/// platform doesn't expose it.</summary>
public sealed record SystemSnapshot(double? CpuPercent, int? BatteryPercent);

/// <summary>
/// Reads system-wide CPU usage and battery charge %, per OS:
///   * Windows — GetSystemTimes + WMI (Win32_Battery).
///   * macOS   — host_statistics (Mach) for CPU, ioreg (AppleSmartBattery) for charge %.
///   * Linux   — /proc/stat for CPU, /sys/class/power_supply for charge % (best-effort).
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
            return new SystemSnapshot(CpuFromCumulative(ReadWindowsCpuTicks()), ReadWindowsBattery());
        if (OperatingSystem.IsMacOS())
            return new SystemSnapshot(CpuFromCumulative(ReadMacCpuTicks()), ReadMacBattery());
        if (OperatingSystem.IsLinux())
            return new SystemSnapshot(CpuFromCumulative(ReadLinuxCpuTicks()), ReadLinuxBattery());
        return new SystemSnapshot(null, null);
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

    [SupportedOSPlatform("windows")]
    private static int? ReadWindowsBattery()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT EstimatedChargeRemaining FROM Win32_Battery");
            foreach (ManagementBaseObject mo in searcher.Get())
                if (mo["EstimatedChargeRemaining"] is { } v) return Convert.ToInt32(v);
        }
        catch { }
        return null;
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
    private static int? ReadMacBattery()
    {
        var outp = RunCli("/usr/sbin/ioreg", "-rn AppleSmartBattery -w0");
        if (outp is null) return null;

        int? cur = MatchInt(outp, "\"CurrentCapacity\"\\s*=\\s*(\\d+)");
        int? max = MatchInt(outp, "\"MaxCapacity\"\\s*=\\s*(\\d+)");
        // On Intel these are mAh; on Apple Silicon CurrentCapacity is already a %. The ratio
        // handles both (ARM: MaxCapacity=100, so cur*100/100 = cur).
        return (cur is int c && max is int m && m > 0) ? (int)Math.Round(c * 100.0 / m) : cur;
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

    private static int? ReadLinuxBattery()
        => ReadIntFile("/sys/class/power_supply/BAT0/capacity");

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
