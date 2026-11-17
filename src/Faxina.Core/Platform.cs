using System.Reflection;
using System.Text.RegularExpressions;

namespace Faxina.Core;

/// <summary>Tudo que muda entre Windows e Linux. A ponte (Bridge) e a interface só falam com isto.</summary>
public interface IPlatform
{
    string Name { get; }                 // "windows" | "linux"
    bool IsAdmin { get; }
    string SystemRoot { get; }           // "C:\" ou "/"

    /// <summary>Discos, espaço escondido e textos que a interface mostra (serializado direto para o JS).</summary>
    object GetSystemInfo();

    /// <summary>Computador, processador, memória e discos físicos com os volumes de cada um (tela Computador).</summary>
    HardwareInfo GetHardware();

    /// <summary>Uso da memória agora; a tela Computador pergunta de novo a cada poucos segundos.</summary>
    MemoryNow GetMemory();

    IRecovery Recovery { get; }

    IReadOnlyList<CleanupTarget> Cleanups { get; }
    List<string> DefaultProjectRoots();

    Task<List<InstalledApp>> ListAppsAsync(CancellationToken ct);
    Task<int> UninstallAsync(string id, Action<string> log, CancellationToken ct);

    /// <summary>Pastas que a varredura não deve atravessar (outros sistemas de arquivos montados, /proc…).</summary>
    IReadOnlySet<string> ScanExcludes(string root);

    /// <summary>Motivo para recusar apagar o caminho pela tela de exploração, ou null se puder.</summary>
    string? WhyProtected(string path);

    void MoveToTrash(string path);
    void Reveal(string path);
    void OpenUrl(string url);
    bool RelaunchAsAdmin();

    /// <summary>Conta do usuário atual e se dá para trocar a senha por aqui.</summary>
    AccountInfo GetAccount();

    /// <summary>
    /// Troca a senha do usuário atual, conferindo a atual. Lança <see cref="PasswordException"/> com uma mensagem
    /// pronta para a tela (nunca contendo as senhas).
    /// </summary>
    Task ChangePasswordAsync(string current, string next, CancellationToken ct);
}

/// <param name="Kind">local | microsoft | domain | linux</param>
/// <param name="ManageUrl">Onde trocar a senha quando não dá por aqui (ex.: conta Microsoft).</param>
public sealed record AccountInfo(string User, string Kind, bool CanChange, string? Reason, string? ManageUrl, int MinLength);

public sealed class PasswordException(string message, bool wrongCurrent = false) : Exception(message)
{
    /// <summary>A senha atual estava errada (conta para o bloqueio temporário).</summary>
    public bool WrongCurrent { get; } = wrongCurrent;
}

/// <summary>Quem hospeda a interface: a janela WebView2 no Windows, o servidor HTTP local no Linux.</summary>
public interface IHost
{
    void Emit(object message);
    Task<string?> PickFolderAsync();
    void Exit();
}

public sealed class InstalledApp
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public DateTime? InstallDate { get; init; }
    public long Size { get; set; }
    public string Source { get; init; } = "win32";      // win32 | store | apt | snap | flatpak
    public string? Location { get; init; }
    public string Verdict { get; set; } = "";           // remover | opcional | manter | ""
    public string? Advice { get; set; }
    public bool RequiresAdmin { get; init; }            // desinstalar exige estar como root/admin
}

/// <summary>Um volume com sistema de arquivos (C:\, /, /media/pendrive). Reserved: o que o ext4 guarda para o root.</summary>
public sealed record Volume(string Name, string Label, string Format, long Total, long Free, long Reserved = 0, bool Removable = false);

/// <param name="Media">ssd | hdd | null (pendrive, cartão, disco virtual ou desconhecido)</param>
/// <param name="Bus">nvme | sata | usb | sd | scsi | sas | raid | virtual | null</param>
/// <param name="Health">ok | warn | bad | null (sem informação)</param>
/// <param name="System">É o disco onde o sistema está instalado.</param>
public sealed record DiskInfo(string Model, long Size, string? Media, string? Bus, bool Removable, string? Health, bool System, List<Volume> Volumes);

public sealed record MemoryModule(long Size, int? Speed, string? Type, string? Slot);

/// <summary>Swap: só no Linux; no Windows o pagefile.sys já aparece na visão geral.</summary>
public sealed record MemoryNow(long Total, long Available, long SwapTotal = 0, long SwapFree = 0);

/// <param name="BaseMhz">Clock base (Windows informa este).</param>
/// <param name="MaxMhz">Clock máximo (Linux informa este, quando o kernel expõe).</param>
public sealed record CpuInfo(string? Name, int Cores, int Threads, int? BaseMhz, int? MaxMhz);

/// <param name="Slots">Encaixes de memória na placa; 0 quando o sistema não informa.</param>
public sealed record MemoryInfo(MemoryNow Now, List<MemoryModule> Modules, int Slots);

public sealed record ComputerInfo(string Machine, string? Maker, string? Model, string Os, long UptimeSeconds);

/// <param name="Others">Volumes que não deu para ligar a um disco físico (ZFS, unidades virtuais…).</param>
public sealed record HardwareInfo(ComputerInfo Computer, CpuInfo Cpu, MemoryInfo Memory, List<DiskInfo> Disks, List<Volume> Others);

public static class Hw
{
    /// <summary>Tira os textos de exemplo que fabricantes deixam no firmware ("To Be Filled By O.E.M.").</summary>
    public static string? Clean(string? s)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        string[] junk = ["O.E.M.", "System manufacturer", "System Product Name", "Default string", "Not Applicable", "None"];
        return junk.Any(j => s.Contains(j, StringComparison.OrdinalIgnoreCase)) ? null : s;
    }

    /// <summary>"Intel(R) Core(TM) i7-8550U CPU @ 1.80GHz" → "Intel Core i7-8550U CPU @ 1.80GHz".</summary>
    public static string? CpuName(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : Regex.Replace(s.Replace("(R)", "").Replace("(TM)", "").Replace("(tm)", ""), @"\s+", " ").Trim();
}

/// <summary>Linha do quadro "Espaço escondido" da visão geral.</summary>
public sealed record HiddenItem(string Icon, string Title, string Sub, long? Bytes, bool Faded = false);

/// <summary>Arquivos da interface embutidos no Faxina.Core.dll.</summary>
public static class Assets
{
    public static Stream? Open(string path) =>
        typeof(Assets).Assembly.GetManifestResourceStream("wwwroot/" + path.TrimStart('/'));

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

    public static string Version =>
        typeof(Assets).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0";
}
