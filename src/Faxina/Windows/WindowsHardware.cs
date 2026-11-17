using System.Management;
using System.Runtime.InteropServices;
using Faxina.Core;
using Microsoft.Win32;

namespace Faxina.Windows;

/// <summary>Processador, memória e discos físicos. Tudo vem do WMI e funciona sem administrador.</summary>
public static class WindowsHardware
{
    private const string Storage = @"root\Microsoft\Windows\Storage";

    public static HardwareInfo Get()
    {
        var volumes = WindowsSystem.Volumes();
        var disks = Disks(volumes);
        var mapped = disks.SelectMany(d => d.Volumes).ToHashSet();
        return new HardwareInfo(Computer(), Cpu(), new MemoryInfo(Memory(), Modules(out var slots), slots),
            disks, volumes.Where(v => !mapped.Contains(v)).ToList());
    }

    private static ComputerInfo Computer()
    {
        var cs = Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault();
        var os = Query("SELECT Caption, BuildNumber FROM Win32_OperatingSystem").FirstOrDefault();
        var caption = (os?["Caption"] as string ?? "Windows").Replace("Microsoft ", "").Trim();
        var display = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", null) as string;
        var build = os?["BuildNumber"] as string;
        return new ComputerInfo(Environment.MachineName, Hw.Clean(cs?["Manufacturer"] as string), Hw.Clean(cs?["Model"] as string),
            $"{caption}{(display != null ? " " + display : "")}{(build != null ? $" (build {build})" : "")}",
            Environment.TickCount64 / 1000);
    }

    private static CpuInfo Cpu()
    {
        var cpus = Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor").ToList();
        var cores = cpus.Sum(c => Int(c["NumberOfCores"]) ?? 0);
        var threads = cpus.Sum(c => Int(c["NumberOfLogicalProcessors"]) ?? 0);
        return new CpuInfo(Hw.CpuName(cpus.Select(c => c["Name"] as string).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))),
            cores > 0 ? cores : Environment.ProcessorCount, threads > 0 ? threads : Environment.ProcessorCount,
            cpus.Select(c => Int(c["MaxClockSpeed"])).Max(), null);
    }

    public static MemoryNow Memory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? new MemoryNow((long)m.ullTotalPhys, (long)m.ullAvailPhys) : new MemoryNow(0, 0);
    }

    private static List<MemoryModule> Modules(out int slots)
    {
        slots = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray").Sum(a => Int(a["MemoryDevices"]) ?? 0);
        return Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, DeviceLocator FROM Win32_PhysicalMemory")
            .Select(m => new MemoryModule(Convert.ToInt64(m["Capacity"] ?? 0L),
                // A velocidade configurada é a que a placa usa de fato; Speed é a máxima do pente.
                Int(m["ConfiguredClockSpeed"]) is > 0 and var c ? c : Int(m["Speed"]),
                MemoryType(Int(m["SMBIOSMemoryType"])), (m["DeviceLocator"] as string)?.Trim()))
            .Where(m => m.Size > 0).ToList();
    }

    private static string? MemoryType(int? smbios) => smbios switch
    {
        20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 34 => "DDR5",
        27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 35 => "LPDDR5",
        _ => null,
    };

    private static List<DiskInfo> Disks(List<Volume> volumes)
    {
        var media = new Dictionary<string, int>();
        foreach (var p in Query("SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk", Storage))
            media[p["DeviceId"] as string ?? ""] = Int(p["MediaType"]) ?? 0;

        var partitions = Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", Storage)
            .Select(p => (Disk: Int(p["DiskNumber"]) ?? -1, Letter: Letter(p["DriveLetter"])))
            .Where(p => p.Letter != null).ToList();

        return Query("SELECT Number, FriendlyName, Size, BusType, HealthStatus, IsBoot, IsSystem FROM MSFT_Disk", Storage)
            .Select(d =>
            {
                var number = Int(d["Number"]) ?? -1;
                var bus = Bus(Int(d["BusType"]));
                var kind = media.GetValueOrDefault(number.ToString());
                var vols = partitions.Where(p => p.Disk == number)
                    .Select(p => volumes.FirstOrDefault(v => v.Name.StartsWith(p.Letter + ":", StringComparison.OrdinalIgnoreCase)))
                    .OfType<Volume>().OrderBy(v => v.Name).ToList();
                var removable = bus is "usb" or "sd";
                return (number, disk: new DiskInfo(
                    (d["FriendlyName"] as string)?.Trim() ?? "Disco " + number,
                    Convert.ToInt64(d["Size"] ?? 0L),
                    kind switch { 4 => "ssd", 3 => "hdd", _ => bus == "nvme" ? "ssd" : null },
                    bus, removable,
                    Int(d["HealthStatus"]) switch { 0 => "ok", 1 => "warn", 2 => "bad", _ => null },
                    d["IsBoot"] is true || d["IsSystem"] is true,
                    vols));
            })
            .Where(x => x.disk.Size > 0)
            .OrderByDescending(x => x.disk.System).ThenBy(x => x.number)
            .Select(x => x.disk).ToList();
    }

    // Valores de MSFT_Disk.BusType
    private static string? Bus(int? type) => type switch
    {
        17 => "nvme", 11 or 3 => "sata", 7 => "usb", 12 or 13 => "sd", 1 => "scsi", 10 => "sas", 8 => "raid", 14 or 15 => "virtual",
        _ => null,
    };

    private static char? Letter(object? v)
    {
        try { var c = Convert.ToChar(v); return char.IsLetter(c) ? c : null; }
        catch { return null; }
    }

    private static int? Int(object? v)
    {
        try { return v == null ? null : Convert.ToInt32(v); } catch { return null; }
    }

    private static List<ManagementObject> Query(string wql, string scope = @"root\cimv2")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            return searcher.Get().Cast<ManagementObject>().ToList();
        }
        catch { return []; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
