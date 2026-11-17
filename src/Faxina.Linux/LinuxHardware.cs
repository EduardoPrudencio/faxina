using System.Globalization;
using System.Text.RegularExpressions;
using Faxina.Core;

namespace Faxina.Linux;

/// <summary>Processador, memória e discos lidos de /proc e /sys, sem precisar de root nem de programas extras.</summary>
public static class LinuxHardware
{
    public static HardwareInfo Get()
    {
        var volumes = LinuxSystem.Volumes();
        var disks = Disks(volumes);
        var mapped = disks.SelectMany(d => d.Volumes).ToHashSet();
        return new HardwareInfo(Computer(), Cpu(), new MemoryInfo(Memory(), [], 0),
            disks, volumes.Select(v => v.Volume).Where(v => !mapped.Contains(v)).ToList());
    }

    private static ComputerInfo Computer()
    {
        var maker = Hw.Clean(Read("/sys/class/dmi/id/sys_vendor"));
        var model = Hw.Clean(Read("/sys/class/dmi/id/product_name"));
        model ??= Hw.Clean(Read("/proc/device-tree/model").TrimEnd('\0'));     // Raspberry Pi e outras placas ARM
        var up = Read("/proc/uptime").Split(' ')[0];
        return new ComputerInfo(Environment.MachineName, maker, model, LinuxSystem.OsName(),
            double.TryParse(up, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? (long)s : 0);
    }

    private static CpuInfo Cpu()
    {
        var text = Read("/proc/cpuinfo");
        string? Field(string key) => Regex.Match(text, $@"^{key}\s*:\s*(.+)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;

        var threads = Regex.Matches(text, @"^processor\s*:", RegexOptions.Multiline).Count;
        // Núcleos físicos: pares (soquete, núcleo) distintos; sem essa informação (VMs, ARM), conta as threads.
        string? physical = null;
        var pairs = new HashSet<string>();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("physical id")) physical = line.Split(':')[1].Trim();
            else if (line.StartsWith("core id")) pairs.Add(physical + "/" + line.Split(':')[1].Trim());
        }
        var cores = pairs.Count > 0 ? pairs.Count : threads;

        int? maxMhz = long.TryParse(Read("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq"), out var khz) ? (int)(khz / 1000) : null;
        return new CpuInfo(Hw.CpuName(Field("model name") ?? Field("Hardware")),
            cores > 0 ? cores : Environment.ProcessorCount, threads > 0 ? threads : Environment.ProcessorCount, null, maxMhz);
    }

    public static MemoryNow Memory()
    {
        var info = new Dictionary<string, long>();
        foreach (var line in Read("/proc/meminfo").Split('\n'))
        {
            var m = Regex.Match(line, @"^(\w+):\s+(\d+) kB");
            if (m.Success) info[m.Groups[1].Value] = long.Parse(m.Groups[2].Value) * 1024;
        }
        long Get(string k) => info.GetValueOrDefault(k);
        return new MemoryNow(Get("MemTotal"), info.ContainsKey("MemAvailable") ? Get("MemAvailable") : Get("MemFree") + Get("Cached"),
            Get("SwapTotal"), Get("SwapFree"));
    }

    private static List<DiskInfo> Disks(List<(string Device, Volume Volume)> volumes)
    {
        // Cada volume vai para o primeiro disco de onde vem (LVM ou btrfs em vários discos ficam no primeiro).
        var byDisk = volumes.Select(v => (Disk: DisksOf(v.Device).FirstOrDefault(), v.Volume))
            .Where(v => v.Disk != null).ToLookup(v => v.Disk!, v => v.Volume);

        var list = new List<DiskInfo>();
        if (!Directory.Exists("/sys/block")) return list;
        foreach (var dir in Directory.EnumerateDirectories("/sys/block").Order())
        {
            var name = Path.GetFileName(dir);
            string[] virtualDevices = ["loop", "ram", "zram", "dm-", "md", "sr", "nbd", "fd"];
            if (virtualDevices.Any(name.StartsWith)) continue;
            if (!long.TryParse(Read(Path.Combine(dir, "size")), out var sectors) || sectors == 0) continue;

            var vols = byDisk[name].ToList();
            // No WSL os outros discos virtuais são internos (sistema do WSL, swap) e não interessam
            if (LinuxUser.IsWsl && vols.Count == 0) continue;

            var real = Resolve(dir);
            var model = Model(dir, name);
            var bus = name.StartsWith("nvme") ? "nvme"
                : real.Contains("/usb") ? "usb"
                : name.StartsWith("mmcblk") ? "sd"
                : IsVirtual(real, name, Read(Path.Combine(dir, "device/vendor")) + " " + model) ? "virtual"
                : real.Contains("/ata") ? "sata"
                : null;
            var removable = bus is "usb" or "sd" || Read(Path.Combine(dir, "removable")) == "1";
            var rotational = Read(Path.Combine(dir, "queue/rotational")) == "1";
            // Pendrives e cartões costumam se dizer "rotacionais"; para eles o tipo fica em branco.
            var media = bus == "nvme" ? "ssd" : bus is "usb" or "sd" or "virtual" ? null : rotational ? "hdd" : "ssd";

            list.Add(new DiskInfo(model, sectors * 512, media, bus, removable, null,
                vols.Any(v => v.Name == "/"), vols));
        }
        return list.OrderByDescending(d => d.System).ToList();
    }

    // virtio (KVM/QEMU), Xen, Hyper-V (WSL e Azure), VMware e VirtualBox
    private static bool IsVirtual(string real, string name, string vendorModel) =>
        real.Contains("/virtio") || real.Contains("MSFT1000") || real.Contains("VMBUS", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("vd") || name.StartsWith("xvd")
        || Regex.IsMatch(vendorModel, @"\b(Msft|QEMU|VMware|VBOX|Virtual)\b", RegexOptions.IgnoreCase);

    private static string Model(string dir, string name)
    {
        var vendor = Read(Path.Combine(dir, "device/vendor"));
        var model = Read(Path.Combine(dir, "device/model"));
        if (model == "") model = Read(Path.Combine(dir, "device/name"));    // cartões SD
        // "ATA" e códigos hexadecimais não dizem nada ao usuário
        if (vendor == "ATA" || vendor.StartsWith("0x") || model.Contains(vendor, StringComparison.OrdinalIgnoreCase)) vendor = "";
        var full = $"{vendor} {model}".Trim();
        return full == "" ? name : full;
    }

    /// <summary>De /dev/sda1, /dev/nvme0n1p2 ou /dev/mapper/vg-root até o(s) disco(s) físico(s): sda, nvme0n1…</summary>
    private static List<string> DisksOf(string device)
    {
        var found = new List<string>();
        if (!device.StartsWith("/dev/")) return found;
        string name;
        try { name = Path.GetFileName(new FileInfo(device).ResolveLinkTarget(true)?.FullName ?? device); }
        catch { return found; }
        Walk(name, found, 0);
        return found;
    }

    private static void Walk(string name, List<string> found, int depth)
    {
        var cls = "/sys/class/block/" + name;
        if (depth > 6 || !Directory.Exists(cls)) return;
        var slaves = Path.Combine(cls, "slaves");
        var under = Directory.Exists(slaves) ? Directory.GetFileSystemEntries(slaves) : [];
        if (under.Length > 0)
        {
            foreach (var s in under.Order()) Walk(Path.GetFileName(s), found, depth + 1);   // LVM, criptografia, RAID
            return;
        }
        // Partição: a pasta dela fica dentro da pasta do disco
        var disk = File.Exists(Path.Combine(cls, "partition")) ? Path.GetFileName(Path.GetDirectoryName(Resolve(cls))) : name;
        if (disk != null && !found.Contains(disk)) found.Add(disk);
    }

    public static bool IsRemovable(string device)
    {
        var disk = DisksOf(device).FirstOrDefault();
        if (disk == null) return false;
        var dir = "/sys/block/" + disk;
        return Read(Path.Combine(dir, "removable")) == "1" || Resolve(dir).Contains("/usb") || disk.StartsWith("mmcblk");
    }

    private static string Resolve(string path)
    {
        try { return new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? path; }
        catch { return path; }
    }

    private static string Read(string path) => LinuxUser.ReadText(path).Trim();
}
