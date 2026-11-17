using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Faxina.Core;

namespace Faxina.Linux;

public sealed class LinuxRecovery : IRecovery
{
    // ───────────────────────── Lixeira ─────────────────────────
    // Padrão freedesktop: ~/.local/share/Trash e, em pendrives e outros discos, <disco>/.Trash-<uid>.
    // Cada item tem info/<nome>.trashinfo (de onde veio e quando) e files/<nome>.

    /// <param name="Top">Disco da lixeira; o caminho de origem no .trashinfo é relativo a ele. Null na lixeira pessoal.</param>
    private sealed record TrashDir(string Root, string? Top);

    private static List<TrashDir> TrashDirs()
    {
        var list = new List<TrashDir> { new(Path.Combine(LinuxUser.Home, ".local/share/Trash"), null) };
        var uid = Uid();
        foreach (var (_, v) in LinuxSystem.Volumes())
        {
            if (v.Name == "/" || uid == null) continue;
            foreach (var t in new[] { Path.Combine(v.Name, $".Trash-{uid}"), Path.Combine(v.Name, ".Trash", uid) })
                if (Directory.Exists(Path.Combine(t, "info"))) list.Add(new TrashDir(t, v.Name));
        }
        return list.Where(t => Directory.Exists(Path.Combine(t.Root, "info"))).ToList();
    }

    // uid de quem chamou o sudo (ou do próprio usuário), lido de /etc/passwd
    private static string? Uid() =>
        LinuxUser.ReadText("/etc/passwd").Split('\n').Select(l => l.Split(':'))
            .FirstOrDefault(f => f.Length > 2 && f[0] == LinuxUser.Name)?[2];

    public List<TrashItem> ListTrash() =>
        TrashDirs().SelectMany(t => Directory.EnumerateFiles(Path.Combine(t.Root, "info"), "*.trashinfo").Select(i => ReadInfo(t, i)))
            .OfType<TrashItem>().OrderByDescending(i => i.DeletedAt).ToList();

    private static TrashItem? ReadInfo(TrashDir t, string infoPath)
    {
        var text = LinuxUser.ReadText(infoPath);
        var raw = Regex.Match(text, @"^Path=(.+)$", RegexOptions.Multiline);
        if (!raw.Success) return null;
        var original = Uri.UnescapeDataString(raw.Groups[1].Value.Trim());
        if (!original.StartsWith('/')) original = Path.Combine(t.Top ?? "/", original);
        var data = DataPath(t, infoPath);
        var isDir = Directory.Exists(data);
        if (!isDir && !File.Exists(data)) return null;
        var date = Regex.Match(text, @"^DeletionDate=(.+)$", RegexOptions.Multiline);
        DateTime? when = date.Success && DateTime.TryParse(date.Groups[1].Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
        return new TrashItem(infoPath, Path.GetFileName(original), original, when, isDir ? Fs.FolderSize(data) : Fs.SafeLength(data), isDir);
    }

    private static string DataPath(TrashDir t, string infoPath) =>
        Path.Combine(t.Root, "files", Path.GetFileNameWithoutExtension(infoPath));

    public List<RestoredItem> RestoreFromTrash(IReadOnlyCollection<string> ids)
    {
        var dirs = TrashDirs();
        var result = new List<RestoredItem>();
        foreach (var id in ids)
        {
            // Só aceita .trashinfo das lixeiras do próprio usuário
            var t = dirs.FirstOrDefault(d => Path.GetDirectoryName(id) == Path.Combine(d.Root, "info"));
            if (t == null || !id.EndsWith(".trashinfo", StringComparison.Ordinal)) throw new InvalidOperationException("Item inválido da Lixeira.");
            var item = ReadInfo(t, id);
            if (item == null) { result.Add(new RestoredItem(Path.GetFileName(id), "", false, "não está mais na Lixeira")); continue; }
            try
            {
                var target = RecoveryKit.FreeName(item.OriginalPath, "restaurado");
                var parent = Path.GetDirectoryName(target)!;
                var created = !Directory.Exists(parent);
                Directory.CreateDirectory(parent);
                if (created) LinuxUser.GiveBack(parent);
                if (item.IsFolder) Directory.Move(DataPath(t, id), target);
                else File.Move(DataPath(t, id), target);
                File.Delete(id);
                result.Add(new RestoredItem(item.Name, target, target != item.OriginalPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Add(new RestoredItem(item.Name, item.OriginalPath, false, ex.Message));
            }
        }
        return result;
    }

    // ───────────────────────── versões anteriores ─────────────────────────

    public PreviousVersionsInfo PreviousVersions() => new(false, false, false,
        "No Linux, versões anteriores dependem de snapshots configurados à parte (Timeshift, Snapper, btrfs ou ZFS). " +
        "Se você usa um deles, restaure pela ferramenta dele.");

    public List<Snapshot> ListSnapshots(string path) => throw NotHere();
    public List<SnapshotEntry> BrowseSnapshot(string snapshotId, string path) => throw NotHere();
    public List<RestoredItem> RestoreFromSnapshot(string snapshotId, string path, IReadOnlyCollection<string> names) => throw NotHere();
    public void OpenFileHistory() => throw NotHere();
    private static InvalidOperationException NotHere() => new("Versões anteriores não estão disponíveis no Linux.");

    // ───────────────────────── recuperação profunda ─────────────────────────
    // PhotoRec (pacote testdisk) procura arquivos pelo conteúdo, então funciona em qualquer formato,
    // inclusive cartões e pendrives formatados. Os nomes originais se perdem (f12345.jpg).

    public DeepTool DeepTool()
    {
        var cmd = Proc.Which("apt-get") != null ? "sudo apt install testdisk"
            : Proc.Which("dnf") != null ? "sudo dnf install testdisk"
            : Proc.Which("pacman") != null ? "sudo pacman -S testdisk"
            : Proc.Which("zypper") != null ? "sudo zypper install photorec"
            : "instale o pacote testdisk pelo gerenciador de pacotes";
        return new DeepTool("PhotoRec", Proc.Which("photorec") != null, true, cmd, false);
    }

    public void OpenDeepToolInstall() => throw new InvalidOperationException("Instale pelo terminal com o comando mostrado.");

    public async Task RunDeepAsync(DeepRequest req, string output, Action<string> log, Action<int> percent, CancellationToken ct)
    {
        if (!LinuxUser.IsRoot) throw new InvalidOperationException("A recuperação profunda precisa de root (rode o Faxina com sudo).");
        var exe = Proc.Which("photorec") ?? throw new InvalidOperationException("Instale o PhotoRec (pacote testdisk).");
        var (device, source) = LinuxSystem.Volumes().FirstOrDefault(v => v.Volume.Name == req.Source);
        if (source == null || !device.StartsWith("/dev/")) throw new InvalidOperationException("Disco de origem não encontrado: " + req.Source);
        var dest = Path.GetFullPath(req.Destination);
        if (!Directory.Exists(dest)) throw new InvalidOperationException("Pasta de destino não encontrada: " + dest);
        if (DeviceOf(dest) == device)
            throw new InvalidOperationException("O destino precisa estar em outro disco. Gravar no mesmo disco pode apagar de vez o que você quer recuperar.");

        // Rápida: só o espaço livre (onde ficam os apagados). Completa: o disco inteiro, inclusive o que ainda existe.
        var cmd = new List<string>();
        if (source.Format.StartsWith("ext")) cmd.AddRange(["options", "mode_ext2"]);
        var types = req.Groups.SelectMany(g => FileGroups.PhotoRec[g]).Distinct().ToList();
        cmd.AddRange(["fileopt", "everything", types.Count > 0 ? "disable" : "enable"]);
        foreach (var t in types) cmd.AddRange([t, "enable"]);
        cmd.AddRange([req.Mode == "full" ? "wholespace" : "freespace", "search"]);

        Directory.CreateDirectory(output);
        var args = $"/log /logname {Proc.Quote(Path.Combine(output, "photorec.log"))} /d {Proc.Quote(Path.Combine(output, "recup_dir"))} /cmd {Proc.Quote(device)} {string.Join(",", cmd)}";
        log($"photorec {args}");

        var psi = new ProcessStartInfo(exe, args) { WorkingDirectory = output };
        psi.Environment["TERM"] = "xterm";       // a tela do PhotoRec usa ncurses mesmo no modo por comando
        psi.Environment["LC_ALL"] = "C";
        try
        {
            var code = await RecoveryKit.RunToolAsync(psi, log, percent, ct,
                // "Pass 1 - Reading sector 123456/7811072, 43 files found"
                parsePercent: l => Regex.Match(l, @"sector\s+(\d+)/(\d+)") is { Success: true } m && long.Parse(m.Groups[2].Value) > 0
                    ? (int)(long.Parse(m.Groups[1].Value) * 100 / long.Parse(m.Groups[2].Value)) : null,
                keep: l => Regex.IsMatch(l, @"Pass \d|files? found|Elapsed|recup_dir|error|fail|denied|No such|Unable|cannot", RegexOptions.IgnoreCase));
            if (code != 0) throw new InvalidOperationException($"O PhotoRec terminou com erro (código {code}). Os detalhes estão em Atividade e em photorec.log.");
        }
        finally { LinuxUser.GiveBack(output); }     // os arquivos são criados como root
    }

    // Dispositivo do sistema de arquivos que contém a pasta (o ponto de montagem mais longo que é prefixo dela)
    private static string? DeviceOf(string path) =>
        LinuxSystem.Mounts().Where(m => path == m.Point || path.StartsWith(m.Point.TrimEnd('/') + "/", StringComparison.Ordinal))
            .MaxBy(m => m.Point.Length)?.Device;
}
