using System.Text.RegularExpressions;
using Faxina.Core;

namespace Faxina.Linux;

public sealed class LinuxPlatform : IPlatform
{
    public string Name => "linux";
    public bool IsAdmin => LinuxUser.IsRoot;
    public string SystemRoot => "/";
    public IReadOnlyList<CleanupTarget> Cleanups => LinuxCleanups.All;

    public object GetSystemInfo() => LinuxSystem.Get();
    public HardwareInfo GetHardware() => LinuxHardware.Get();
    public MemoryNow GetMemory() => LinuxHardware.Memory();
    public IRecovery Recovery { get; } = new LinuxRecovery();
    public List<string> DefaultProjectRoots() => LinuxCleanups.DefaultProjectRoots();
    public Task<List<InstalledApp>> ListAppsAsync(CancellationToken ct) => LinuxApps.ListAsync(ct);
    public Task<int> UninstallAsync(string id, Action<string> log, CancellationToken ct) => LinuxApps.UninstallAsync(id, log, ct);

    /// <summary>
    /// Como o "ncdu -x": não atravessa outros sistemas de arquivos montados dentro da pasta analisada
    /// (/proc, /sys, /run, o C: do Windows em /mnt/c no WSL, snaps em /snap, outro disco em /home…).
    /// </summary>
    public IReadOnlySet<string> ScanExcludes(string root)
    {
        var rootNorm = root == "/" ? "/" : Fs.TrimSep(root);
        return LinuxSystem.Mounts().Select(m => m.Point).Where(p => p != rootNorm).ToHashSet(StringComparer.Ordinal);
    }

    private static readonly string[] SystemDirs =
        ["/bin", "/sbin", "/lib", "/lib32", "/lib64", "/libx32", "/usr", "/etc", "/boot", "/proc", "/sys", "/dev", "/run",
         "/var/lib/dpkg", "/var/lib/apt", "/var/lib/rpm", "/var/lib/snapd", "/snap"];

    public string? WhyProtected(string path)
    {
        var p = Fs.TrimSep(path);
        if (SystemDirs.Any(d => p == d || p.StartsWith(d + "/", StringComparison.Ordinal)))
            return "Por segurança, o Faxina não apaga arquivos do sistema por aqui. Use a aba Limpeza.";
        if (p == Fs.TrimSep(LinuxUser.Home) || p == "/home")
            return "Não é permitido apagar a pasta pessoal inteira.";
        return null;
    }

    /// <summary>Lixeira do padrão freedesktop (~/.local/share/Trash), a mesma do Nautilus, Dolphin e Thunar.</summary>
    public void MoveToTrash(string path)
    {
        var trash = Path.Combine(LinuxUser.Home, ".local/share/Trash");
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        var created = !Directory.Exists(trash);
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);
        if (created) LinuxUser.GiveBack(trash);

        var name = Path.GetFileName(Fs.TrimSep(path));
        var target = name;
        for (var i = 2; File.Exists(Path.Combine(files, target)) || Directory.Exists(Path.Combine(files, target)) ||
                        File.Exists(Path.Combine(info, target + ".trashinfo")); i++)
            target = $"{name}.{i}";

        var infoFile = Path.Combine(info, target + ".trashinfo");
        var encoded = string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        File.WriteAllText(infoFile, $"[Trash Info]\nPath={encoded}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        try
        {
            if (Directory.Exists(path)) Directory.Move(path, Path.Combine(files, target));
            else File.Move(path, Path.Combine(files, target));
        }
        catch (IOException)
        {
            File.Delete(infoFile);
            throw new InvalidOperationException("Este item está em outro disco e não pode ir para a Lixeira. Use \"Excluir permanentemente\".");
        }
        LinuxUser.GiveBack(infoFile);
    }

    public void Reveal(string path) => LinuxUser.Open(File.Exists(path) ? Path.GetDirectoryName(path)! : path);

    // Um navegador não consegue se reabrir como root; a interface mostra como rodar com sudo.
    public bool RelaunchAsAdmin() => false;

    public void OpenUrl(string url) => LinuxUser.Open(url);
    public AccountInfo GetAccount() => LinuxAccount.Get();
    public Task ChangePasswordAsync(string current, string next, CancellationToken ct) => LinuxAccount.ChangeAsync(current, next, ct);
}

public static class LinuxSystem
{
    public sealed record Mount(string Device, string Point, string Type);

    /// <summary>Sistemas de arquivos que guardam dados em disco de verdade (o resto é virtual: proc, tmpfs, overlay…).</summary>
    private static readonly HashSet<string> DiskTypes =
        ["ext2", "ext3", "ext4", "btrfs", "xfs", "zfs", "f2fs", "jfs", "reiserfs", "bcachefs", "ntfs", "ntfs3", "fuseblk", "vfat", "exfat"];

    public static List<Mount> Mounts()
    {
        var list = new List<Mount>();
        foreach (var line in LinuxUser.ReadText("/proc/self/mounts").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(' ');
            if (f.Length < 3) continue;
            list.Add(new Mount(Unescape(f[0]), Unescape(f[1]), f[2]));
        }
        return list;
    }

    // /proc/mounts escreve espaços e afins como \040
    private static string Unescape(string s) => Regex.Replace(s, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    /// <summary>Volumes com dados de verdade, junto com o dispositivo de onde vêm (/dev/sda1, /dev/mapper/…).</summary>
    public static List<(string Device, Volume Volume)> Volumes() => Mounts()
        .Where(m => DiskTypes.Contains(m.Type) && !m.Point.StartsWith("/snap/", StringComparison.Ordinal)
                    && !m.Point.StartsWith("/mnt/wsl", StringComparison.Ordinal) && !m.Point.StartsWith("/usr/lib/wsl", StringComparison.Ordinal))
        .GroupBy(m => m.Device).Select(g => g.OrderBy(m => m.Point.Length).First())   // bind mounts do mesmo disco
        .Select(m =>
        {
            try
            {
                var d = new DriveInfo(m.Point);
                if (d.TotalSize < 512L << 20) return default;    // /boot/efi e afins
                return (m.Device, new Volume(m.Point, m.Point == "/" ? "Sistema" : Path.GetFileName(m.Point), m.Type,
                    d.TotalSize, d.AvailableFreeSpace,
                    // ext4 reserva ~5% para o root: não está "usado", mas também não está livre para o usuário.
                    Reserved: Math.Max(0, d.TotalFreeSpace - d.AvailableFreeSpace),
                    Removable: LinuxHardware.IsRemovable(m.Device)));
            }
            catch { return default; }
        })
        .Where(x => x.Item2 != null).OrderBy(x => x.Item2.Name == "/" ? "" : x.Item2.Name).ToList();

    public static object Get()
    {
        var drives = Volumes().Select(x => x.Volume).ToList();

        var hidden = new List<HiddenItem>();
        foreach (var line in LinuxUser.ReadText("/proc/swaps").Split('\n').Skip(1))
        {
            var f = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 3 || !long.TryParse(f[2], out var kb)) continue;
            hidden.Add(f[1] == "file"
                ? new("memory", $"Arquivo de swap ({f[0]})", "Memória virtual; o tamanho é definido na instalação", kb * 1024)
                : new("memory", "Partição de swap", "Fica fora do sistema de arquivos; não ocupa o /", kb * 1024, Faded: true));
        }

        var journal = JournalUsage();
        hidden.Add(new("history", "Logs do sistema (journald)",
            journal > 0 ? "Crescem com o tempo; podem ser reduzidos na Limpeza" : "Sem acesso aos logs do sistema (rode com sudo)", journal > 0 ? journal : null));

        var snaps = "/var/lib/snapd/snaps";
        if (Directory.Exists(snaps))
        {
            var files = SafeFiles(snaps, "*.snap");
            hidden.Add(new("box", "Pacotes Snap", $"{files.Count} revisão(ões) guardada(s); as antigas podem sair", files.Sum(f => f.Length)));
        }
        if (Directory.Exists("/var/lib/flatpak"))
            hidden.Add(new("box", "Flatpak (sistema)", "Apps e runtimes Flatpak", Fs.FolderSize("/var/lib/flatpak")));
        if (LinuxUser.IsRoot && Directory.Exists("/var/lib/docker"))
            hidden.Add(new("box", "Docker (/var/lib/docker)", "Imagens, containers e volumes", Fs.FolderSize("/var/lib/docker")));
        var trash = Path.Combine(LinuxUser.Home, ".local/share/Trash");
        hidden.Add(new("trash", "Lixeira", trash.Replace(LinuxUser.Home, "~"), Directory.Exists(trash) ? Fs.FolderSize(trash) : 0));

        var exe = Environment.ProcessPath ?? "./faxina";
        return new
        {
            platform = "linux",
            isAdmin = LinuxUser.IsRoot,
            canRelaunch = false,
            canPickFolder = false,
            adminHint = $"Feche o Faxina (Ctrl+C no terminal) e rode de novo com sudo: sudo {exe}",
            machine = Environment.MachineName,
            user = LinuxUser.Name,
            os = OsName(),
            isWsl = LinuxUser.IsWsl,
            systemDrive = "/",
            drives,
            hidden,
            shadow = (object?)null,
            hiddenTitle = "Onde o Linux costuma acumular",
            rootFilesHint = "arquivos soltos na raiz, como /swapfile",
        };
    }

    public static long JournalUsage()
    {
        if (Proc.Which("journalctl") == null) return 0;
        try
        {
            var (_, output) = Proc.CaptureAsync("journalctl", "--disk-usage").GetAwaiter().GetResult();
            var m = Regex.Match(output, @"take up ([\d.]+[KMGT]?)");
            return m.Success ? CleanupKit.ParseHumanSize(m.Groups[1].Value, 1024) : 0;
        }
        catch { return 0; }
    }

    public static string OsName()
    {
        var m = Regex.Match(LinuxUser.ReadText("/etc/os-release"), "^PRETTY_NAME=\"?([^\"\n]+)", RegexOptions.Multiline);
        return (m.Success ? m.Groups[1].Value : "Linux") + (LinuxUser.IsWsl ? " (WSL)" : "");
    }

    public static List<FileInfo> SafeFiles(string dir, string pattern)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles(pattern, new EnumerationOptions { IgnoreInaccessible = true }).ToList(); }
        catch { return []; }
    }
}
