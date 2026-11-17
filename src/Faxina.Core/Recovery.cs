using System.Diagnostics;
using System.Text;

namespace Faxina.Core;

/// <summary>
/// Recuperar arquivos apagados, em dois níveis: o que ainda está guardado (Lixeira, versões anteriores) e
/// a recuperação profunda, que procura restos de arquivos no disco com uma ferramenta própria para isso
/// (Windows File Recovery da Microsoft no Windows, PhotoRec no Linux).
/// </summary>
public interface IRecovery
{
    List<TrashItem> ListTrash();

    /// <summary>Devolve cada item para a pasta de onde saiu; se já existir algo com o mesmo nome, ganha um sufixo.</summary>
    List<RestoredItem> RestoreFromTrash(IReadOnlyCollection<string> ids);

    PreviousVersionsInfo PreviousVersions();
    List<Snapshot> ListSnapshots(string path);
    List<SnapshotEntry> BrowseSnapshot(string snapshotId, string path);
    List<RestoredItem> RestoreFromSnapshot(string snapshotId, string path, IReadOnlyCollection<string> names);
    void OpenFileHistory();

    DeepTool DeepTool();
    void OpenDeepToolInstall();

    /// <summary>
    /// Roda a recuperação profunda gravando em <paramref name="output"/> (pasta nova dentro do destino).
    /// Recusa com <see cref="InvalidOperationException"/> se o destino estiver no mesmo volume da origem.
    /// </summary>
    Task RunDeepAsync(DeepRequest request, string output, Action<string> log, Action<int> percent, CancellationToken ct);
}

/// <param name="Id">Identifica o item na Lixeira (caminho do arquivo de informação dele).</param>
public sealed record TrashItem(string Id, string Name, string OriginalPath, DateTime? DeletedAt, long Size, bool IsFolder);

/// <param name="Error">Por que este item não voltou (os outros seguem sendo restaurados).</param>
public sealed record RestoredItem(string Name, string Path, bool Renamed, string? Error = null);

/// <param name="FileHistory">O Histórico de Arquivos do Windows está configurado.</param>
public sealed record PreviousVersionsInfo(bool Supported, bool RequiresAdmin, bool FileHistory, string? Note);

public sealed record Snapshot(string Id, DateTime Created);

/// <param name="Status">deleted (não existe mais) | changed (diferente do atual) | same | folder</param>
public sealed record SnapshotEntry(string Name, bool IsFolder, long Size, DateTime Modified, string Status);

/// <param name="InstallCommand">Comando para instalar pelo terminal (Linux).</param>
/// <param name="CanOpenInstall">Há uma página de instalação que o app abre (Microsoft Store).</param>
public sealed record DeepTool(string Name, bool Installed, bool RequiresAdmin, string? InstallCommand, bool CanOpenInstall);

/// <param name="Source">Volume de onde os arquivos foram apagados (C:\, E:\, /, /media/pendrive).</param>
/// <param name="Mode">quick | full</param>
/// <param name="Groups">Tipos de arquivo (chaves de <see cref="FileGroups"/>); vazio = todos.</param>
/// <param name="NameFilter">Parte do nome ou da pasta (só no Windows).</param>
public sealed record DeepRequest(string Source, string Destination, string Mode, List<string> Groups, string? NameFilter);

public static class FileGroups
{
    /// <summary>Extensões de cada grupo (Windows File Recovery filtra por nome).</summary>
    public static readonly Dictionary<string, string[]> Extensions = new()
    {
        ["photos"] = ["jpg", "jpeg", "png", "gif", "bmp", "heic", "tif", "tiff", "webp", "cr2", "nef", "arw", "dng"],
        ["videos"] = ["mp4", "mov", "avi", "mkv", "wmv", "3gp", "m4v"],
        ["audio"] = ["mp3", "wav", "m4a", "flac", "ogg", "wma"],
        ["documents"] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "txt", "rtf", "csv"],
        ["archives"] = ["zip", "rar", "7z"],
    };

    /// <summary>
    /// Formatos do PhotoRec, que reconhece arquivos pelo conteúdo: "zip" pega também docx/xlsx/odt,
    /// "mov" pega mp4/3gp, "riff" pega avi e wav, "doc" pega os Office antigos e "tif" vários RAW de câmera.
    /// </summary>
    public static readonly Dictionary<string, string[]> PhotoRec = new()
    {
        ["photos"] = ["jpg", "png", "gif", "bmp", "tif"],
        ["videos"] = ["mov", "mkv", "riff"],
        ["audio"] = ["mp3", "riff", "flac", "ogg"],
        ["documents"] = ["pdf", "doc", "zip", "txt"],
        ["archives"] = ["zip", "rar", "7z"],
    };

    public static List<string> Validate(IEnumerable<string>? groups) =>
        (groups ?? []).Where(Extensions.ContainsKey).Distinct().ToList();
}

public static class RecoveryKit
{
    /// <summary>"foto.jpg" → "foto (restaurado).jpg", "foto (restaurado 2).jpg"… até achar um nome livre.</summary>
    public static string FreeName(string path, string tag)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var isDir = Directory.Exists(path);
        var stem = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({tag}{(i > 1 ? " " + i : "")}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    public static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Quantos arquivos e quantos bytes já foram recuperados (lido enquanto a ferramenta roda).</summary>
    public static (int files, long bytes) Count(string dir)
    {
        int n = 0; long b = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (f.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                n++; b += f.Length;
            }
        }
        catch { }
        return (n, b);
    }

    /// <summary>
    /// Roda uma ferramenta de linha de comando que redesenha o progresso na mesma linha (com \r):
    /// cada atualização vira uma linha, porcentagens repetidas são descartadas e a última porcentagem
    /// vista vai para <paramref name="percent"/>. <paramref name="parsePercent"/> troca a leitura padrão ("45%")
    /// e <paramref name="keep"/> descarta linhas que só redesenham a tela.
    /// </summary>
    public static async Task<int> RunToolAsync(ProcessStartInfo psi, Action<string> log, Action<int> percent, CancellationToken ct,
        Func<string, int?>? parsePercent = null, Func<string, bool>? keep = null)
    {
        parsePercent ??= l => System.Text.RegularExpressions.Regex.Match(l, @"(\d{1,3})(?:[.,]\d+)?\s?%") is { Success: true } m
            && int.TryParse(m.Groups[1].Value, out var pc) && pc <= 100 ? pc : null;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        using var p = new Process { StartInfo = psi };
        p.Start();
        p.StandardInput.Close();

        var lastPercent = -1;
        string? lastLine = null;
        var gate = new object();
        void Line(string raw)
        {
            lock (gate) LineLocked(raw);
        }
        void LineLocked(string raw)
        {
            // Tira códigos de cor e de posicionamento do terminal
            var line = System.Text.RegularExpressions.Regex.Replace(raw, @"\x1B\[[0-9;?]*[A-Za-z]|\x1B[()][A-Z0-9]", "").Trim();
            if (line.Length == 0 || line == lastLine || (keep != null && !keep(line))) return;
            if (parsePercent(line) is int pc)
            {
                if (pc == lastPercent) return;
                lastPercent = pc;
                percent(pc);
            }
            lastLine = line;
            log(line);
        }

        async Task Pump(StreamReader reader)
        {
            var sb = new StringBuilder();
            var buf = new char[4096];
            int n;
            while ((n = await reader.ReadAsync(buf, CancellationToken.None)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] is '\r' or '\n') { if (sb.Length > 0) { Line(sb.ToString()); sb.Clear(); } }
                    else sb.Append(buf[i]);
                }
            }
            if (sb.Length > 0) Line(sb.ToString());
        }

        var pumps = Task.WhenAll(Pump(p.StandardOutput), Pump(p.StandardError));
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        await pumps;
        return p.ExitCode;
    }
}
