using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ThroughputBenchmark.ApiService.Benchmarking;

/// <summary>Host system metrics sampled alongside throughput. Any value may be null when the
/// platform doesn't expose it (e.g. CPU temp is usually unavailable on ARM laptops, and power
/// draw is only readable while on battery).</summary>
public sealed record SystemSnapshot(double? CpuPercent, double? CpuTempC, double? PowerWatts, int? BatteryPercent);

/// <summary>
/// Reads system-wide CPU usage (via GetSystemTimes), plus battery %, power draw and CPU temp
/// (via WMI on Windows). Not thread-safe — only the sampler calls <see cref="Read"/>, once a tick.
/// </summary>
public sealed class SystemMetrics
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    private static ulong ToUlong(FileTime f) => ((ulong)f.High << 32) | f.Low;

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _havePrev;

    public SystemSnapshot Read()
    {
        if (!OperatingSystem.IsWindows())
            return new SystemSnapshot(null, null, null, null);

        var (battery, power) = ReadBatteryAndPower();
        return new SystemSnapshot(ReadCpuPercent(), ReadCpuTempC(), power, battery);
    }

    /// <summary>System-wide CPU busy % over the interval since the previous call.</summary>
    private double? ReadCpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return null;

        ulong i = ToUlong(idle), k = ToUlong(kernel), u = ToUlong(user);
        if (!_havePrev)
        {
            (_prevIdle, _prevKernel, _prevUser, _havePrev) = (i, k, u, true);
            return null; // need two samples to compute a delta
        }

        ulong total = (k - _prevKernel) + (u - _prevUser); // kernel time already includes idle
        ulong idleDelta = i - _prevIdle;
        (_prevIdle, _prevKernel, _prevUser) = (i, k, u);

        if (total == 0) return null;
        double busy = (double)(total - idleDelta) / total * 100.0;
        return Math.Round(Math.Clamp(busy, 0, 100), 1);
    }

    [SupportedOSPlatform("windows")]
    private static double? ReadCpuTempC()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                // CurrentTemperature is in tenths of a Kelvin.
                double tenthsK = Convert.ToDouble(mo["CurrentTemperature"]);
                return Math.Round(tenthsK / 10.0 - 273.15, 1);
            }
        }
        catch { /* not exposed on this machine (common on ARM / without admin) */ }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static (int? BatteryPercent, double? PowerWatts) ReadBatteryAndPower()
    {
        int? percent = null;
        double? watts = null;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT EstimatedChargeRemaining FROM Win32_Battery");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                if (mo["EstimatedChargeRemaining"] is { } v) percent = Convert.ToInt32(v);
                break;
            }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\WMI", "SELECT DischargeRate, PowerOnline FROM BatteryStatus");
            foreach (ManagementBaseObject mo in searcher.Get())
            {
                bool onAc = mo["PowerOnline"] is bool b && b;
                // DischargeRate is mW and only meaningful while running on battery.
                if (!onAc && mo["DischargeRate"] is { } dr)
                {
                    double mw = Convert.ToDouble(dr);
                    if (mw > 0) watts = Math.Round(mw / 1000.0, 1);
                }
                break;
            }
        }
        catch { }

        return (percent, watts);
    }
}
