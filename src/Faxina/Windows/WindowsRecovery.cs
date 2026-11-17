using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using System.Text;
using Faxina.Core;

namespace Faxina.Windows;

public sealed class WindowsRecovery : IRecovery
{
    // ───────────────────────── Lixeira ─────────────────────────
    // Cada disco tem $Recycle.Bin\<SID do usuário>. Para cada item há um par: $I… (de onde veio, quando, tamanho)
    // e $R… (o próprio arquivo ou pasta, com o mesmo sufixo). Restaurar = mover o $R de volta e apagar o $I.

    private static List<string> BinDirs()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid == null) return [];
        return WindowsSystem.Volumes().Select(v => Path.Combine(v.Name, "$Recycle.Bin", sid)).Where(Directory.Exists).ToList();
    }

    public List<TrashItem> ListTrash()
    {
        var hidden = new System.IO.EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        return BinDirs()
            .SelectMany(dir => Directory.EnumerateFiles(dir, "$I*", hidden))
            .Select(ReadInfo).OfType<TrashItem>()
            .OrderByDescending(i => i.DeletedAt).ToList();
    }

    private static TrashItem? ReadInfo(string infoPath)
    {
        try
        {
            var b = File.ReadAllBytes(infoPath);
            if (b.Length < 28) return null;
            var version = BitConverter.ToInt64(b, 0);
            var size = BitConverter.ToInt64(b, 8);
            var when = DateTime.FromFileTime(BitConverter.ToInt64(b, 16));
            // Versão 2 (Windows 10+): tamanho do caminho e depois o caminho; versão 1: caminho fixo de 260 caracteres
            var original = version == 2
                ? Encoding.Unicode.GetString(b, 28, Math.Min(BitConverter.ToInt32(b, 24) * 2, b.Length - 28))
                : Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
            original = original.Split('\0')[0];
            var data = DataPath(infoPath);
            if (original.Length == 0 || (!File.Exists(data) && !Directory.Exists(data))) return null;
            return new TrashItem(infoPath, Path.GetFileName(original), original, when, size, Directory.Exists(data));
        }
        catch { return null; }
    }

    private static string DataPath(string infoPath) =>
        Path.Combine(Path.GetDirectoryName(infoPath)!, "$R" + Path.GetFileName(infoPath)[2..]);

    public List<RestoredItem> RestoreFromTrash(IReadOnlyCollection<string> ids)
    {
        var bins = BinDirs();
        var result = new List<RestoredItem>();
        foreach (var id in ids)
        {
            // Só aceita arquivos $I das Lixeiras do próprio usuário
            var dir = Path.GetDirectoryName(id);
            if (dir == null || !Path.GetFileName(id).StartsWith("$I", StringComparison.Ordinal)
                || !bins.Any(b => string.Equals(b, dir, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Item inválido da Lixeira.");
            var item = ReadInfo(id);
            if (item == null) { result.Add(new RestoredItem(Path.GetFileName(id), "", false, "não está mais na Lixeira")); continue; }
            try
            {
                var target = RecoveryKit.FreeName(item.OriginalPath, "restaurado");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (item.IsFolder) Directory.Move(DataPath(id), target);
                else File.Move(DataPath(id), target);
                File.Delete(id);
                result.Add(new RestoredItem(item.Name, target, !string.Equals(target, item.OriginalPath, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Add(new RestoredItem(item.Name, item.OriginalPath, false, ex.Message));
            }
        }
        return result;
    }

    // ───────────────────────── versões anteriores ─────────────────────────
    // Os pontos de restauração são cópias de sombra (VSS) do volume inteiro, inclusive dos arquivos do usuário.
    // Ler dentro delas exige administrador.

    private static string FileHistoryConfig =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\FileHistory\Configuration");

    public PreviousVersionsInfo PreviousVersions() => new(true, true,
        Directory.Exists(FileHistoryConfig) && Directory.EnumerateFiles(FileHistoryConfig, "Config*.xml").Any(), null);

    private sealed record Shadow(string Id, string Device, DateTime Created);

    private static List<Shadow> Shadows(string root, string? id = null)
    {
        if (!Elevation.IsAdmin) throw new InvalidOperationException("Reabra o Faxina como administrador para ver as versões anteriores.");
        var letter = root.TrimEnd('\\');
        string? volume;
        using (var volumes = new ManagementObjectSearcher("SELECT DeviceID, DriveLetter FROM Win32_Volume"))
            volume = volumes.Get().Cast<ManagementObject>()
                .FirstOrDefault(v => string.Equals(v["DriveLetter"] as string, letter, StringComparison.OrdinalIgnoreCase))?["DeviceID"] as string;
        if (volume == null) return [];

        using var searcher = new ManagementObjectSearcher("SELECT ID, DeviceObject, VolumeName, InstallDate FROM Win32_ShadowCopy");
        return searcher.Get().Cast<ManagementObject>()
            .Where(s => string.Equals(s["VolumeName"] as string, volume, StringComparison.OrdinalIgnoreCase))
            .Where(s => id == null || string.Equals(s["ID"] as string, id, StringComparison.OrdinalIgnoreCase))
            .Select(s => new Shadow((string)s["ID"], (string)s["DeviceObject"], ManagementDateTimeConverter.ToDateTime((string)s["InstallDate"])))
            .OrderByDescending(s => s.Created).ToList();
    }

    // C:\Users\Ana → \\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy3\Users\Ana
    private static string InShadow(Shadow s, string path) =>
        s.Device.TrimEnd('\\') + "\\" + path[Path.GetPathRoot(path)!.Length..];

    private static string Folder(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new InvalidOperationException("Pasta não encontrada: " + path);
        return path;
    }

    public List<Snapshot> ListSnapshots(string path)
    {
        path = Folder(path);
        return Shadows(Path.GetPathRoot(path)!).Where(s => Directory.Exists(InShadow(s, path) + "\\"))
            .Select(s => new Snapshot(s.Id, s.Created)).ToList();
    }

    private static Shadow Find(string id, string path)
    {
        if (!Guid.TryParse(id, out _)) throw new InvalidOperationException("Versão inválida.");
        return Shadows(Path.GetPathRoot(path)!, id).FirstOrDefault() ?? throw new InvalidOperationException("Esta versão não existe mais.");
    }

    public List<SnapshotEntry> BrowseSnapshot(string snapshotId, string path)
    {
        path = Folder(path);
        var shadow = Find(snapshotId, path);
        var dir = new DirectoryInfo(InShadow(shadow, path) + "\\");
        var list = new List<SnapshotEntry>();
        foreach (var e in dir.EnumerateFileSystemInfos("*", new System.IO.EnumerationOptions { IgnoreInaccessible = true }))
        {
            var live = Path.Combine(path, e.Name);
            if (e is DirectoryInfo)
                list.Add(new SnapshotEntry(e.Name, true, 0, e.LastWriteTime, Directory.Exists(live) ? "folder" : "deleted"));
            else
            {
                var f = (FileInfo)e;
                var now = new FileInfo(live);
                var status = !now.Exists ? "deleted"
                    : now.Length != f.Length || Math.Abs((now.LastWriteTimeUtc - f.LastWriteTimeUtc).TotalSeconds) > 2 ? "changed" : "same";
                list.Add(new SnapshotEntry(e.Name, false, f.Length, f.LastWriteTime, status));
            }
        }
        return list.OrderByDescending(e => e.IsFolder).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public List<RestoredItem> RestoreFromSnapshot(string snapshotId, string path, IReadOnlyCollection<string> names)
    {
        path = Folder(path);
        var shadow = Find(snapshotId, path);
        var tag = $"versão de {shadow.Created:dd-MM-yyyy HH\\hmm}";
        var result = new List<RestoredItem>();
        foreach (var name in names)
        {
            if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['\\', '/', ':']) >= 0)
                throw new InvalidOperationException("Nome inválido.");
            var from = Path.Combine(InShadow(shadow, path), name);
            var to = Path.Combine(path, name);
            try
            {
                var target = RecoveryKit.FreeName(to, tag);
                if (Directory.Exists(from)) RecoveryKit.CopyTree(from, target);
                else File.Copy(from, target);
                result.Add(new RestoredItem(name, target, target != to));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Add(new RestoredItem(name, to, false, ex.Message));
            }
        }
        return result;
    }

    // Abre a tela "Restaurar arquivos pessoais" do Histórico de Arquivos
    public void OpenFileHistory() => Process.Start(new ProcessStartInfo("FileHistory.exe") { UseShellExecute = true });

    // ───────────────────────── recuperação profunda ─────────────────────────
    // Windows File Recovery (winfr), gratuito da Microsoft, distribuído pela Microsoft Store.

    private const string StoreUrl = "ms-windows-store://pdp/?ProductId=9N26S50LN705";

    public DeepTool DeepTool() => new("Windows File Recovery", Proc.Which("winfr") != null, true, null, true);

    public void OpenDeepToolInstall() => Process.Start(new ProcessStartInfo(StoreUrl) { UseShellExecute = true });

    public async Task RunDeepAsync(DeepRequest req, string output, Action<string> log, Action<int> percent, CancellationToken ct)
    {
        if (!Elevation.IsAdmin) throw new InvalidOperationException("A recuperação profunda precisa de administrador.");
        var exe = Proc.Which("winfr") ?? throw new InvalidOperationException("Instale o Windows File Recovery pela Microsoft Store.");
        var source = WindowsSystem.Volumes().FirstOrDefault(v => string.Equals(v.Name, req.Source, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException("Disco de origem não encontrado: " + req.Source);
        var dest = Path.GetFullPath(req.Destination);
        if (!Directory.Exists(dest)) throw new InvalidOperationException("Pasta de destino não encontrada: " + dest);
        if (string.Equals(Path.GetPathRoot(dest), source.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O destino precisa estar em outro disco. Gravar no mesmo disco pode apagar de vez o que você quer recuperar.");

        // O modo normal só funciona em NTFS; pendrives e cartões (FAT, exFAT) vão sempre pelo completo.
        var full = req.Mode == "full" || !source.Format.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        if (full && req.Mode != "full") log($"{source.Name} é {source.Format}: usando o modo completo, o único que funciona nesse formato.");

        var filter = Sanitize(req.NameFilter);
        var exts = req.Groups.SelectMany(g => FileGroups.Extensions[g]).Distinct().ToList();
        var patterns = exts.Count > 0
            ? exts.Select(e => filter != null ? $"*{filter}*.{e}" : $"*.{e}")
            : filter != null ? [$"*{filter}*"] : [];

        Directory.CreateDirectory(output);
        var args = new StringBuilder($"{source.Name.TrimEnd('\\')} \"{output}\" /{(full ? "extensive" : "regular")} /a");
        foreach (var p in patterns) args.Append($" /n \"{p}\"");
        log($"winfr {args}");

        var psi = new ProcessStartInfo(exe, args.ToString()) { StandardOutputEncoding = Proc.Oem, StandardErrorEncoding = Proc.Oem };
        var code = await RecoveryKit.RunToolAsync(psi, log, percent, ct);
        if (code != 0) throw new InvalidOperationException($"O Windows File Recovery terminou com erro (código {code}). Os detalhes estão em Atividade.");
    }

    // Nome ou pasta digitado: sem aspas nem barras de opção, que mudariam o comando
    private static string? Sanitize(string? s)
    {
        s = s?.Replace("\"", "").Replace("*", "").Trim().TrimStart('/');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
