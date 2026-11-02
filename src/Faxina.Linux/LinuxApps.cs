using System.Text.RegularExpressions;
using Faxina.Core;

namespace Faxina.Linux;

/// <summary>Programas instalados via apt/dpkg, rpm, Snap e Flatpak, e como removê-los.</summary>
public static class LinuxApps
{
    private sealed record Removal(string Name, string Command);

    private static readonly Dictionary<string, Removal> _last = new();
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9.+_:\-]*$");

    public static async Task<List<InstalledApp>> ListAsync(CancellationToken ct)
    {
        var found = new List<(InstalledApp app, Removal how)>();
        if (Proc.Which("dpkg-query") != null) found.AddRange(await ReadDpkgAsync(ct));
        else if (Proc.Which("rpm") != null) found.AddRange(await ReadRpmAsync(ct));
        if (Proc.Which("snap") != null) found.AddRange(await ReadSnapsAsync(ct));
        if (Proc.Which("flatpak") != null) found.AddRange(await ReadFlatpaksAsync(ct));

        foreach (var (app, _) in found) Advise(app);
        lock (_last)
        {
            _last.Clear();
            foreach (var (app, how) in found) _last[app.Id] = how;
        }
        return found.Select(x => x.app).OrderByDescending(a => a.Size).ToList();
    }

    /// <summary>
    /// Pacotes .deb. Mostra só os instalados "manualmente" (apt-mark showmanual): as centenas de bibliotecas
    /// puxadas como dependência poluiriam a lista e são removidas pelo "autoremove" na Limpeza.
    /// </summary>
    private static async Task<List<(InstalledApp, Removal)>> ReadDpkgAsync(CancellationToken ct)
    {
        var manual = new HashSet<string>(StringComparer.Ordinal);
        if (Proc.Which("apt-mark") != null)
        {
            var (_, m) = await Proc.CaptureAsync("apt-mark", "showmanual", null, ct);
            foreach (var l in m.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) manual.Add(l);
        }

        var (_, output) = await Proc.CaptureAsync("dpkg-query",
            "-W --showformat=${Package}\\t${Version}\\t${Installed-Size}\\t${Maintainer}\\t${db:Status-Abbrev}\\t${Priority}\\n", null, ct);
        var list = new List<(InstalledApp, Removal)>();
        foreach (var line in output.Split('\n'))
        {
            var f = line.Split('\t');
            if (f.Length < 6 || !f[4].StartsWith("ii", StringComparison.Ordinal)) continue;
            var pkg = f[0].Split(':')[0];
            if (manual.Count > 0 ? !manual.Contains(f[0]) && !manual.Contains(pkg) : !long.TryParse(f[2], out var s) || s < 10_000) continue;
            if (f[5] is "required" or "important") continue;   // base do sistema: não é "programa"
            long.TryParse(f[2], out var kb);
            list.Add((new InstalledApp
            {
                Id = "apt:" + f[0], Name = pkg, Version = f[1], Size = kb * 1024,
                Publisher = Regex.Replace(f[3], "<.*?>", "").Trim(), Source = "apt", RequiresAdmin = true,
            }, new Removal(pkg, $"DEBIAN_FRONTEND=noninteractive apt-get remove -y {f[0]}")));
        }
        return list;
    }

    private static async Task<List<(InstalledApp, Removal)>> ReadRpmAsync(CancellationToken ct)
    {
        var (_, output) = await Proc.CaptureAsync("rpm", "-qa --qf %{NAME}\\t%{VERSION}-%{RELEASE}\\t%{SIZE}\\t%{VENDOR}\\n", null, ct);
        var remover = Proc.Which("dnf") != null ? "dnf" : Proc.Which("zypper") != null ? "zypper" : "yum";
        var list = new List<(InstalledApp, Removal)>();
        foreach (var line in output.Split('\n'))
        {
            var f = line.Split('\t');
            if (f.Length < 4 || !long.TryParse(f[2], out var bytes) || bytes < 10L << 20) continue;
            list.Add((new InstalledApp
            {
                Id = "rpm:" + f[0], Name = f[0], Version = f[1], Size = bytes,
                Publisher = f[3] == "(none)" ? null : f[3], Source = "rpm", RequiresAdmin = true,
            }, new Removal(f[0], remover == "zypper" ? $"zypper --non-interactive remove {f[0]}" : $"{remover} remove -y {f[0]}")));
        }
        return list;
    }

    private static async Task<List<(InstalledApp, Removal)>> ReadSnapsAsync(CancellationToken ct)
    {
        var (code, output) = await Proc.CaptureAsync("snap", "list", null, ct);
        var list = new List<(InstalledApp, Removal)>();
        if (code != 0) return list;
        foreach (var line in output.Split('\n').Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 5) continue;
            var notes = f.Length > 5 ? f[5] : "";
            if (notes.Contains("base") || notes.Contains("core") || notes.Contains("snapd")) continue; // runtimes do próprio snap
            list.Add((new InstalledApp
            {
                Id = "snap:" + f[0], Name = f[0], Version = f[1], Publisher = f[4].TrimEnd('✓', '*'),
                Size = Fs.SafeLength($"/var/lib/snapd/snaps/{f[0]}_{f[2]}.snap"), Source = "snap", RequiresAdmin = true,
            }, new Removal(f[0], $"snap remove {f[0]}")));
        }
        return list;
    }

    private static async Task<List<(InstalledApp, Removal)>> ReadFlatpaksAsync(CancellationToken ct)
    {
        var (code, output) = await Proc.CaptureAsync("flatpak", "list --app --columns=application,name,version,size,installation", null, ct);
        var list = new List<(InstalledApp, Removal)>();
        if (code != 0) return list;
        foreach (var line in output.Split('\n'))
        {
            var f = line.Split('\t');
            if (f.Length < 5) continue;
            var system = f[4].Trim() == "system";
            list.Add((new InstalledApp
            {
                Id = "flatpak:" + f[0], Name = f[1], Version = f[2], Size = CleanupKit.ParseHumanSize(f[3].Replace(',', '.')),
                Publisher = f[0], Source = "flatpak", RequiresAdmin = system,
            }, new Removal(f[1], $"flatpak uninstall -y --noninteractive {(system ? "--system" : "--user")} {f[0]}")));
        }
        return list;
    }

    private static readonly (string pattern, string verdict, string advice)[] Rules =
    [
        (@"^(ubuntu-|linux-|systemd|apt$|dpkg|sudo|openssh|network-manager|grub|bash$|coreutils|snapd$|flatpak$|xorg|gnome-shell|plasma-)",
            "manter", "Parte do sistema. Recomendo manter."),
        (@"^(thunderbird|libreoffice|rhythmbox|totem|cheese|aisleriot|gnome-mahjongg|gnome-mines|gnome-sudoku|transmission)",
            "opcional", "App que vem com a distribuição. Pode remover se não usar."),
    ];

    private static void Advise(InstalledApp app)
    {
        foreach (var (pattern, verdict, advice) in Rules)
        {
            if (!Regex.IsMatch(app.Name, pattern, RegexOptions.IgnoreCase)) continue;
            app.Verdict = verdict;
            app.Advice = advice;
            return;
        }
    }

    public static async Task<int> UninstallAsync(string id, Action<string> log, CancellationToken ct)
    {
        Removal? how;
        lock (_last) _last.TryGetValue(id, out how);
        if (how == null) throw new InvalidOperationException("Programa não encontrado. Atualize a lista.");
        if (!SafeName.IsMatch(how.Name.Replace(' ', '-')) && !how.Command.StartsWith("flatpak", StringComparison.Ordinal))
            throw new InvalidOperationException("Nome de pacote inesperado.");

        log($"Removendo {how.Name}…");
        log("> " + how.Command);
        var code = await Proc.ShellAsync(how.Command, log, ct);
        log(code == 0 ? $"{how.Name} removido." : $"O comando terminou com código {code}.");
        return code;
    }
}
