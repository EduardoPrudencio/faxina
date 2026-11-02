using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Faxina.Core;

public static class Proc
{
    /// <summary>Code page do console no Windows (850 em pt-BR); UTF-8 no Linux.</summary>
    public static readonly Encoding Oem = OperatingSystem.IsWindows()
        ? Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage)
        : Encoding.UTF8;

    /// <summary>Executa um processo sem janela, repassando cada linha de saída para <paramref name="log"/>.</summary>
    public static async Task<int> RunAsync(string file, string args, Action<string>? log, CancellationToken ct = default,
        Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };
        // Comandos do Linux em inglês e sem cores: a saída é lida por regex.
        if (!OperatingSystem.IsWindows()) { psi.Environment["LC_ALL"] = "C"; psi.Environment["NO_COLOR"] = "1"; }
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke(e.Data.TrimEnd()); };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Invoke(e.Data.TrimEnd()); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        return p.ExitCode;
    }

    /// <summary>Executa e devolve toda a saída como texto.</summary>
    public static async Task<(int code, string output)> CaptureAsync(string file, string args, Encoding? encoding = null,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        var code = await RunAsync(file, args, l => { lock (sb) sb.AppendLine(l); }, ct, encoding);
        return (code, sb.ToString());
    }

    /// <summary>
    /// Executa passando <paramref name="input"/> pela entrada padrão (para segredos: nunca vão na linha de comando,
    /// que outros processos conseguem ler). A saída é devolvida, não registrada.
    /// </summary>
    public static async Task<(int code, string output)> RunWithInputAsync(string file, string args, string input, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (!OperatingSystem.IsWindows()) psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.StandardInput.WriteAsync(input);
        p.StandardInput.Close();
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, (await stdout + await stderr).Trim());
    }

    /// <summary>Procura um executável no PATH (no Windows inclui .cmd, como npm.cmd).</summary>
    public static string? Which(string name)
    {
        string[] exts = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ""] : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                try
                {
                    var full = Path.Combine(dir.Trim(), name + ext);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>Roda uma linha de comando pelo shell do sistema (cmd.exe ou /bin/sh).</summary>
    public static Task<int> ShellAsync(string command, Action<string>? log, CancellationToken ct = default) =>
        OperatingSystem.IsWindows()
            ? RunAsync("cmd.exe", "/d /c " + command, log, ct)
            : RunAsync("/bin/sh", "-c " + Quote(command), log, ct);

    public static Task<int> PowerShellAsync(string script, Action<string>? log, CancellationToken ct = default) =>
        RunAsync("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + Quote(script), log, ct);

    public static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}

public static class Fs
{
    private static readonly EnumerationOptions Recursive = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static long FolderSize(string path, CancellationToken ct = default, TimeSpan? olderThan = null)
    {
        if (File.Exists(path)) return SafeLength(path);
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        var cutoff = olderThan is { } o ? DateTime.Now - o : DateTime.MaxValue;
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", Recursive))
            {
                ct.ThrowIfCancellationRequested();
                try { if (f.LastWriteTime <= cutoff) total += f.Length; } catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return total;
    }

    public static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; } catch { return 0; }
    }

    public record DeleteStats(long Bytes, int Files, int Failed);

    /// <summary>
    /// Apaga arquivos dentro de <paramref name="path"/> (opcionalmente só os mais antigos que <paramref name="olderThan"/>),
    /// ignorando os que estiverem em uso, e remove pastas que ficarem vazias.
    /// </summary>
    public static DeleteStats DeleteContents(string path, TimeSpan? olderThan = null, bool removeRoot = false,
        CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            var len = SafeLength(path);
            return TryDeleteFile(path) ? new(len, 1, 0) : new(0, 0, 1);
        }
        if (!Directory.Exists(path)) return new(0, 0, 0);

        long bytes = 0; int files = 0, failed = 0;
        var cutoff = olderThan is { } o ? DateTime.Now - o : DateTime.MaxValue;
        var root = new DirectoryInfo(path);
        try
        {
            foreach (var f in root.EnumerateFiles("*", Recursive))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (f.LastWriteTime > cutoff) continue;
                    var len = f.Length;
                    if (TryDeleteFile(f.FullName)) { bytes += len; files++; } else failed++;
                }
                catch { failed++; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // Remove pastas vazias, das mais fundas para as mais rasas.
        try
        {
            var dirs = root.EnumerateDirectories("*", Recursive).Select(d => d.FullName)
                .OrderByDescending(d => d.Length).ToList();
            foreach (var d in dirs)
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
            // Links (junções, symlinks) dentro da pasta: remove só o link, nunca o destino.
            foreach (var link in root.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
                         .Where(e => e.LinkTarget != null).Select(e => e.FullName).ToList())
            {
                try { if (Directory.Exists(link)) Directory.Delete(link); else File.Delete(link); } catch { }
            }
            if (removeRoot && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
        }
        catch { }
        return new(bytes, files, failed);
    }

    private static bool TryDeleteFile(string file)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                    File.SetAttributes(file, FileAttributes.Normal);
            }
            File.Delete(file);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Compara caminhos do jeito do sistema (Windows ignora maiúsculas; Linux não).</summary>
    public static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Remove a barra final, preservando a raiz ("C:\" vira "C:", "/" vira "").</summary>
    public static string TrimSep(string p) => p.TrimEnd('\\', '/');
}

/// <summary>Formatação em pt-BR sem depender dos dados de cultura do sistema (o build Linux usa globalização invariante).</summary>
public static class Br
{
    public static readonly NumberFormatInfo Numbers = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = "." };

    public static string Size(long bytes)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes; var i = 0;
        while (Math.Abs(v) >= 1000 && i < u.Length - 1) { v /= 1024; i++; }
        return v.ToString(i == 0 ? "0" : v < 10 ? "0.0" : "0", Numbers) + " " + u[i];
    }

    public static string Count(long n) => n.ToString("N0", Numbers);
}
