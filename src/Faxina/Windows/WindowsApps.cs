using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Faxina.Core;
using Microsoft.Win32;

namespace Faxina.Windows;

/// <summary>Lista programas instalados (registro + apps da Store) e desinstala.</summary>
public static class WindowsApps
{
    private sealed record UninstallInfo(string Name, string? Command, string? QuietCommand, string? PackageFullName);

    private static readonly Dictionary<string, UninstallInfo> _last = new();

    public static async Task<List<InstalledApp>> ListAsync(CancellationToken ct)
    {
        var found = new List<(InstalledApp app, UninstallInfo how)>();
        found.AddRange(ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry64));
        found.AddRange(ReadRegistry(RegistryHive.LocalMachine, RegistryView.Registry32));
        found.AddRange(ReadRegistry(RegistryHive.CurrentUser, RegistryView.Default));
        found.AddRange(await ReadStoreAppsAsync(ct));

        // O mesmo programa aparece nas visões 32/64 bits; fica o de maior tamanho.
        var unique = found
            .GroupBy(x => (x.app.Name.Trim().ToLowerInvariant(), x.app.Version ?? ""))
            .Select(g => g.OrderByDescending(x => x.app.Size).First())
            .ToList();

        foreach (var (app, _) in unique) Advise(app);
        lock (_last)
        {
            _last.Clear();
            foreach (var (app, how) in unique) _last[app.Id] = how;
        }
        return unique.Select(x => x.app).OrderByDescending(a => a.Size).ToList();
    }

    private static IEnumerable<(InstalledApp, UninstallInfo)> ReadRegistry(RegistryHive hive, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
        using var root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (root == null) yield break;
        foreach (var keyName in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(keyName);
            if (k == null) continue;
            var name = k.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
            if (k.GetValue("ParentKeyName") != null || k.GetValue("ReleaseType") is string rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase)) continue;
            var uninstall = k.GetValue("UninstallString") as string;
            if (string.IsNullOrWhiteSpace(uninstall)) continue;

            long size = k.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;
            DateTime? date = null;
            if (k.GetValue("InstallDate") is string d && DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt)) date = dt;

            var app = new InstalledApp
            {
                Id = $"{hive}-{view}-{keyName}",
                Name = name.Trim(),
                Publisher = (k.GetValue("Publisher") as string)?.Trim(),
                Version = k.GetValue("DisplayVersion") as string,
                InstallDate = date,
                Size = size,
                Location = k.GetValue("InstallLocation") as string,
            };
            yield return (app, new UninstallInfo(app.Name, uninstall, k.GetValue("QuietUninstallString") as string, null));
        }
    }

    private static async Task<List<(InstalledApp, UninstallInfo)>> ReadStoreAppsAsync(CancellationToken ct)
    {
        var list = new List<(InstalledApp, UninstallInfo)>();
        try
        {
            const string script = "Get-AppxPackage | Where-Object { -not $_.IsFramework -and $_.SignatureKind -ne 'System' -and -not $_.NonRemovable } | " +
                                  "Select-Object Name, PackageFullName, Publisher, Version, InstallLocation | ConvertTo-Json -Compress";
            var (_, output) = await Proc.CaptureAsync("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + Proc.Quote("[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script), null, ct);
            output = output.Trim();
            if (output.Length == 0) return list;
            using var doc = JsonDocument.Parse(output.StartsWith('[') ? output : "[" + output + "]");
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var name = e.GetProperty("Name").GetString() ?? "";
                var fullName = e.GetProperty("PackageFullName").GetString();
                var location = e.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                var publisher = e.TryGetProperty("Publisher", out var p) ? p.GetString() : null;
                var cn = publisher != null ? Regex.Match(publisher, "CN=([^,]+)").Groups[1].Value : null;
                var app = new InstalledApp
                {
                    Id = "store-" + fullName,
                    Name = Prettify(name),
                    Publisher = string.IsNullOrWhiteSpace(cn) ? publisher : cn.Trim('"'),
                    Version = e.TryGetProperty("Version", out var v) ? v.GetString() : null,
                    Location = location,
                    Size = location != null ? Fs.FolderSize(location, ct) : 0,
                    Source = "store",
                };
                list.Add((app, new UninstallInfo(app.Name, null, null, fullName)));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return list;
    }

    // "DellInc.DellSupportAssistforPCs" → "Dell SupportAssist for PCs"
    private static string Prettify(string packageName)
    {
        var n = Regex.Replace(packageName, @"^[A-Za-z0-9]+\.", "");
        n = Regex.Replace(n, "(?<=[a-z])(?=[A-Z])", " ");
        return n.Replace("for P Cs", "for PCs").Trim();
    }

    /// <summary>Recomendações para programas comuns de fábrica e bloatware.</summary>
    private static readonly (string pattern, string verdict, string advice)[] Rules =
    [
        (@"Dell SupportAssist Remediation|SupportAssist OS Recovery", "remover", "Componente de recuperação do SupportAssist. Pode remover junto com o SupportAssist."),
        (@"Dell SupportAssist", "opcional", "Diagnóstico e chamados de garantia da Dell. Ocupa bastante espaço e roda em segundo plano; pode remover e reinstalar se precisar de suporte."),
        (@"Dell Optimizer", "remover", "Ajustes automáticos de áudio, bateria e desempenho. Não é necessário para o PC funcionar."),
        (@"Dell Pair", "remover", "Só serve para parear periféricos Dell (mouse, fone)."),
        (@"MyDell|Dell Customer Connect|Dell Digital Delivery|Dell Connected Service Delivery|Dell Core Services|Dell Update for Windows Universal", "remover", "Portal MyDell e serviços de telemetria/entrega de apps da Dell. Pode remover."),
        (@"Dell Command \| Update|Dell Update$", "manter", "Atualiza drivers e BIOS da Dell. Recomendo manter."),
        (@"Windows Software Development Kit", "opcional", "SDK do Windows para compilar apps nativos. Se houver várias versões, as mais antigas geralmente podem sair; mantenha a que seus projetos usam."),
        (@"McAfee|WebAdvisor", "remover", "Antivírus de teste que vem de fábrica. Ao remover, o Microsoft Defender assume automaticamente. Use a ferramenta MCPR da McAfee se sobrar algo."),
        (@"Realtek|Intel\(R\)|Intel®|NVIDIA (Graphics|Driver|HD Audio|PhysX)|Waves MaxxAudio|Synaptics|Microsoft Visual C\+\+|\.NET|Windows SDK|WebView2", "manter", "Driver ou componente do sistema. Recomendo manter."),
        (@"Candy Crush|Booking|Spotify Music|Disney|TikTok|Instagram|Facebook|Amazon|Netflix|LinkedIn|Microsoft Solitaire|Xbox|Clipchamp|Microsoft News|Bing Weather|Get Help|Microsoft Tips|Feedback Hub", "opcional", "App pré-instalado. Pode remover se não usar."),
        (@"ExpressVPN|Dropbox promotion|WildTangent|CyberLink|Norton|Avast|AVG", "remover", "Software de fábrica/teste geralmente dispensável."),
    ];

    private static void Advise(InstalledApp app)
    {
        var text = app.Name + " " + app.Publisher;
        foreach (var (pattern, verdict, advice) in Rules)
        {
            if (!Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase)) continue;
            app.Verdict = verdict;
            app.Advice = advice;
            return;
        }
    }

    public static async Task<int> UninstallAsync(string id, Action<string> log, CancellationToken ct)
    {
        UninstallInfo? how;
        lock (_last) _last.TryGetValue(id, out how);
        if (how == null) throw new InvalidOperationException("Programa não encontrado. Atualize a lista.");

        if (how.PackageFullName != null)
        {
            log($"Removendo o app {how.Name}…");
            return await Proc.PowerShellAsync($"Remove-AppxPackage -Package '{how.PackageFullName}'", log, ct);
        }

        var cmd = how.QuietCommand ?? how.Command!;
        // msiexec /I{GUID} abre o modo "modificar"; /X desinstala com uma barra de progresso simples.
        var msi = Regex.Match(cmd, @"msiexec(\.exe)?\s+/[IX]\s*(\{[0-9A-F\-]+\})", RegexOptions.IgnoreCase);
        string file, args;
        if (msi.Success)
        {
            file = "msiexec.exe";
            args = $"/x {msi.Groups[2].Value} /passive /norestart";
        }
        else
        {
            (file, args) = SplitCommand(cmd);
        }

        log($"Abrindo o desinstalador de {how.Name}…");
        log($"> {file} {args}");
        // UseShellExecute = true deixa o Windows pedir elevação (UAC) se o desinstalador precisar.
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        if (p == null) return -1;
        await p.WaitForExitAsync(ct);
        log($"Desinstalador terminou (código {p.ExitCode}).");
        return p.ExitCode;
    }

    private static (string file, string args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        var exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (cmd[..(exe + 4)], cmd[(exe + 4)..].Trim());
        var space = cmd.IndexOf(' ');
        return space > 0 ? (cmd[..space], cmd[(space + 1)..]) : (cmd, "");
    }
}
