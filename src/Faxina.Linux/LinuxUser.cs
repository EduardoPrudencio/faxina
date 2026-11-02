using System.Runtime.InteropServices;
using Faxina.Core;

namespace Faxina.Linux;

/// <summary>
/// Quem está usando o Faxina. Rodando com sudo, o processo é root, mas os caches e a Lixeira que interessam
/// são os do usuário que chamou o sudo (SUDO_USER), não os do root.
/// </summary>
public static class LinuxUser
{
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    public static bool IsRoot { get; } = GetEuid() == 0;

    public static string? SudoUser { get; } =
        IsRoot && Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } u && u != "root" ? u : null;

    public static string Name => SudoUser ?? Environment.UserName;

    public static string Home { get; } = ResolveHome();

    public static bool IsWsl { get; } = ReadText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);

    private static string ResolveHome()
    {
        if (SudoUser != null)
        {
            foreach (var line in ReadText("/etc/passwd").Split('\n'))
            {
                var f = line.Split(':');
                if (f.Length >= 6 && f[0] == SudoUser) return f[5];
            }
        }
        return Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>Devolve ao usuário real a posse de algo que o root criou na pasta dele.</summary>
    public static void GiveBack(string path)
    {
        if (SudoUser == null) return;
        try { Proc.RunAsync("chown", $"-R {SudoUser}: {Proc.Quote(path)}", null).Wait(TimeSpan.FromSeconds(30)); } catch { }
    }

    /// <summary>Abre algo na sessão gráfica do usuário real (nunca como root).</summary>
    public static void Open(string target)
    {
        if (IsWsl && Proc.Which("explorer.exe") != null)
        {
            // No WSL, abre no Windows: navegador padrão ou Explorer.
            var arg = target.StartsWith("http", StringComparison.Ordinal) ? target : WindowsPath(target);
            Start("explorer.exe", Proc.Quote(arg));
            return;
        }
        foreach (var tool in new[] { "xdg-open", "gio", "sensible-browser" })
        {
            if (Proc.Which(tool) == null) continue;
            var args = (tool == "gio" ? "open " : "") + Proc.Quote(target);
            if (SudoUser != null) Start("sudo", $"-u {SudoUser} -- {tool} {args}");
            else Start(tool, args);
            return;
        }
    }

    private static string WindowsPath(string linuxPath)
    {
        try
        {
            var (_, output) = Proc.CaptureAsync("wslpath", "-w " + Proc.Quote(linuxPath)).GetAwaiter().GetResult();
            return output.Trim();
        }
        catch { return linuxPath; }
    }

    private static void Start(string file, string args)
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            p?.StandardOutput.ReadToEndAsync();
            p?.StandardError.ReadToEndAsync();
        }
        catch { }
    }

    public static string ReadText(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }
}
