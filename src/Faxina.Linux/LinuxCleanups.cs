using System.Text.RegularExpressions;
using Faxina.Core;
using static Faxina.Core.CleanupKit;

namespace Faxina.Linux;

/// <summary>Catálogo de limpezas do Linux: pacotes, logs, snaps, caches do usuário e as de desenvolvimento (comuns).</summary>
public static class LinuxCleanups
{
    private static string Home => LinuxUser.Home;
    private static string Cache => Path.Combine(Home, ".cache");
    private const long MB = 1L << 20;

    public static readonly List<CleanupTarget> All =
        [.. Build(), .. DevCleanups.Build(DevPaths, "O Docker não está rodando ou seu usuário não está no grupo docker.")];

    public static List<string> DefaultProjectRoots() =>
        new[] { "projects", "Projects", "Projetos", "projetos", "dev", "src", "code", "repos", "workspace", "git", "source" }
            .Select(d => Path.Combine(Home, d)).Where(Directory.Exists).ToList();

    private static DevPaths DevPaths => new()
    {
        NpmCache = Path.Combine(Home, ".npm/_cacache"),
        YarnCache = Path.Combine(Cache, "yarn"),
        PnpmStore = Path.Combine(Home, ".local/share/pnpm/store"),
        NuGet = [Path.Combine(Home, ".nuget/packages"), Path.Combine(Home, ".local/share/NuGet/v3-cache"),
                 Path.Combine(Home, ".local/share/NuGet/http-cache"), Path.Combine(Home, ".local/share/NuGet/plugins-cache"), "/tmp/NuGetScratch"],
        GradleHome = Path.Combine(Home, ".gradle"),
        PipCache = Path.Combine(Cache, "pip"),
        CypressCache = Path.Combine(Cache, "Cypress"),
        EditorConfigRoot = Path.Combine(Home, ".config"),
        ProjectRoots = DefaultProjectRoots,
    };

    // Pastas de ~/.cache que já têm um card próprio (não contar duas vezes).
    private static readonly HashSet<string> CoveredCaches = ["yarn", "pip", "Cypress", "pnpm"];

    private static List<CleanupTarget> Build() => new()
    {
        // ───────────── Sistema ─────────────
        new()
        {
            Id = "apt-cache", Category = "Sistema", Title = "Pacotes .deb baixados (apt)",
            Description = "Instaladores que o apt guarda depois de instalar ou atualizar. Não afeta nada instalado.",
            RequiresAdmin = true,
            Measure = (_, ct) => Task.Run(() => Proc.Which("apt-get") == null
                ? Measurement.NotAvailable("Este sistema não usa apt.")
                : FromLabeled(ct, [("/var/cache/apt/archives", "/var/cache/apt/archives")])),
            Run = ctx => Proc.RunAsync("apt-get", "clean", ctx.Log, ctx.Ct),
        },
        new()
        {
            Id = "apt-autoremove", Category = "Sistema", Title = "Pacotes órfãos e kernels antigos (apt)", Risk = "atencao",
            Description = "apt autoremove: dependências que nada mais usa, incluindo kernels antigos. O kernel em uso e o anterior são mantidos.",
            RequiresAdmin = true,
            Measure = (_, ct) => MeasureAutoremove(ct),
            Run = ctx => Proc.ShellAsync("DEBIAN_FRONTEND=noninteractive apt-get autoremove --purge -y", ctx.Log, ctx.Ct),
        },
        new()
        {
            Id = "dnf-cache", Category = "Sistema", Title = "Cache do dnf",
            Description = "Pacotes e metadados baixados pelo dnf (Fedora, RHEL…).",
            RequiresAdmin = true,
            Measure = (_, ct) => Task.Run(() => Proc.Which("dnf") == null
                ? Measurement.NotAvailable("Este sistema não usa dnf.")
                : FromLabeled(ct, [("/var/cache/dnf", "/var/cache/dnf")])),
            Run = ctx => Proc.RunAsync("dnf", "clean all", ctx.Log, ctx.Ct),
        },
        new()
        {
            Id = "pacman-cache", Category = "Sistema", Title = "Cache do pacman",
            Description = "Pacotes antigos guardados pelo pacman (Arch, Manjaro…). Mantém a versão instalada de cada um.",
            RequiresAdmin = true,
            Measure = (_, ct) => Task.Run(() => Proc.Which("pacman") == null
                ? Measurement.NotAvailable("Este sistema não usa pacman.")
                : FromLabeled(ct, [("/var/cache/pacman/pkg", "/var/cache/pacman/pkg")])),
            Run = ctx => Proc.Which("paccache") != null
                ? Proc.RunAsync("paccache", "-rk1", ctx.Log, ctx.Ct)
                : Proc.RunAsync("pacman", "-Sc --noconfirm", ctx.Log, ctx.Ct),
        },
        new()
        {
            Id = "journal", Category = "Sistema", Title = "Logs do sistema (journald)",
            Description = "Reduz os logs do systemd para os 200 MB mais recentes. Os antigos raramente são úteis.",
            RequiresAdmin = true,
            Measure = (_, _) => Task.Run(() =>
            {
                if (Proc.Which("journalctl") == null) return Measurement.NotAvailable("Este sistema não usa journald.");
                var used = LinuxSystem.JournalUsage();
                if (used == 0) return new Measurement { Unknown = true, Note = "Sem acesso aos logs do sistema. Rode com sudo para medir." };
                return new Measurement { Bytes = Math.Max(0, used - 200 * MB), Note = $"Os logs ocupam {Br.Size(used)} hoje." };
            }),
            Run = ctx => Proc.RunAsync("journalctl", "--vacuum-size=200M", ctx.Log, ctx.Ct),
        },
        new()
        {
            Id = "old-logs", Category = "Sistema", Title = "Logs antigos já rotacionados",
            Description = "Arquivos de /var/log que já foram arquivados (.gz, .1, .old). Os logs atuais não são tocados.",
            RequiresAdmin = true,
            Measure = (_, ct) => Task.Run(() =>
            {
                var items = new List<CleanupItem>();
                try
                {
                    foreach (var f in new DirectoryInfo("/var/log").EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (Regex.IsMatch(f.Name, @"(\.gz|\.xz|\.bz2|\.zst|\.old|\.\d+|\.\d+\.log)$"))
                            items.Add(new CleanupItem { Path = f.FullName, Label = f.FullName, Size = f.Length });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                if (items.Count == 0) return Measurement.NotAvailable("Nenhum log antigo encontrado.");
                items.Sort((a, b) => b.Size.CompareTo(a.Size));
                return new Measurement { Items = items, Bytes = items.Sum(i => i.Size), Note = $"{items.Count} arquivo(s)." };
            }),
            Run = ctx => DeletePaths(ctx),
        },
        new()
        {
            Id = "snap-old", Category = "Sistema", Title = "Versões antigas de Snaps",
            Description = "O snapd guarda revisões anteriores de cada app (por padrão, duas). Remove só as desativadas.",
            RequiresAdmin = true, Selectable = true,
            Measure = (_, ct) => MeasureOldSnaps(ct),
            Run = async ctx =>
            {
                foreach (var item in ctx.Items)
                {
                    var m = Regex.Match(Path.GetFileNameWithoutExtension(item.Path), @"^(.+)_(\w+)$");
                    if (!m.Success) continue;
                    await Proc.RunAsync("snap", $"remove {m.Groups[1].Value} --revision={m.Groups[2].Value}", ctx.Log, ctx.Ct);
                }
                ctx.Log("Dica: \"sudo snap set system refresh.retain=2\" limita quantas versões o snapd guarda.");
            },
        },
        new()
        {
            Id = "flatpak-unused", Category = "Sistema", Title = "Runtimes Flatpak sem uso",
            Description = "Bibliotecas Flatpak que nenhum app instalado usa mais.",
            Measure = (_, _) => Task.FromResult(Proc.Which("flatpak") == null
                ? Measurement.NotAvailable("Flatpak não está instalado.")
                : new Measurement { Unknown = true, Note = "O Flatpak só informa o ganho ao remover." }),
            Run = ctx => Proc.RunAsync("flatpak", "uninstall --unused -y --noninteractive", ctx.Log, ctx.Ct),
        },
        new()
        {
            Id = "coredumps", Category = "Sistema", Title = "Despejos de memória (core dumps)",
            Description = "Arquivos gerados quando programas travam (systemd-coredump e apport).",
            RequiresAdmin = true,
            Measure = (_, ct) => Task.Run(() => FromPaths(ct, "/var/lib/systemd/coredump", "/var/crash")),
            Run = ctx => DeletePaths(ctx),
        },
        new()
        {
            Id = "tmp", Category = "Sistema", Title = "Arquivos temporários antigos",
            Description = "Arquivos em /tmp e /var/tmp com mais de 7 dias. Sem root, apaga só os seus.",
            Measure = (_, ct) => Task.Run(() => FromLabeled(ct, [("/tmp", "/tmp"), ("/var/tmp", "/var/tmp")], TimeSpan.FromDays(7))),
            Run = ctx => DeletePaths(ctx, TimeSpan.FromDays(7)),
        },

        // ───────────── Programas ─────────────
        new()
        {
            Id = "user-cache", Category = "Programas", Title = "Cache dos aplicativos (~/.cache)",
            Description = "Cache de navegadores, miniaturas de imagens e outros apps. Tudo aqui é recriado quando preciso. Feche os apps antes.",
            Selectable = true,
            Measure = (_, ct) => Task.Run(() => FromLabeled(ct,
                SafeDirs(Cache).Where(d => !CoveredCaches.Contains(Path.GetFileName(d)))
                    .Select(d => (d, "~/.cache/" + Path.GetFileName(d))).ToList())),
            Run = ctx => DeletePaths(ctx),
        },
        new()
        {
            Id = "trash", Category = "Programas", Title = "Lixeira", Risk = "atencao",
            Description = "Esvazia a Lixeira do seu usuário. Os arquivos são apagados de vez.",
            Measure = (_, ct) => Task.Run(() =>
            {
                var trash = Path.Combine(Home, ".local/share/Trash");
                return FromLabeled(ct, [(Path.Combine(trash, "files"), "Arquivos"), (Path.Combine(trash, "info"), "Informações"), (Path.Combine(trash, "expunged"), "Pendentes")]);
            }),
            Run = ctx => DeletePaths(ctx),
        },
        new()
        {
            Id = "old-downloads", Category = "Programas", Title = "Instaladores antigos em Downloads", Risk = "atencao",
            Description = "Arquivos .deb, .rpm, .AppImage, .iso e compactados com mais de 30 dias em Downloads. Nada vem marcado: escolha o que apagar.",
            Selectable = true,
            Measure = (_, ct) => Task.Run(() =>
            {
                var items = new List<CleanupItem>();
                var dl = Path.Combine(Home, "Downloads");
                if (Directory.Exists(dl))
                {
                    foreach (var f in new DirectoryInfo(dl).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 }))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!Regex.IsMatch(f.Name, @"\.(deb|rpm|appimage|iso|zip|tar\.gz|tgz|tar\.xz|7z|run)$", RegexOptions.IgnoreCase)) continue;
                        if (f.LastWriteTime > DateTime.Now.AddDays(-30)) continue;
                        items.Add(new CleanupItem { Path = f.FullName, Label = f.Name, Size = f.Length, LastActivity = f.LastWriteTime, Selected = false });
                    }
                }
                items.Sort((a, b) => b.Size.CompareTo(a.Size));
                return items.Count == 0 ? Measurement.NotAvailable("Nenhum instalador antigo.") : new Measurement { Items = items };
            }),
            Run = ctx => DeletePaths(ctx),
        },
    };

    private static async Task<Measurement> MeasureAutoremove(CancellationToken ct)
    {
        if (Proc.Which("apt-get") == null) return Measurement.NotAvailable("Este sistema não usa apt.");
        var (_, output) = await Proc.CaptureAsync("apt-get", "-s autoremove --purge", null, ct);
        var pkgs = Regex.Matches(output, @"^(?:Remv|Purg) (\S+)", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct().ToList();
        if (pkgs.Count == 0) return Measurement.NotAvailable("Nenhum pacote órfão.");

        long bytes = 0;
        var (_, sizes) = await Proc.CaptureAsync("dpkg-query", "-W --showformat=${Installed-Size}\\n " +string.Join(' ', pkgs), null, ct);
        foreach (var line in sizes.Split('\n')) if (long.TryParse(line.Trim(), out var kb)) bytes += kb * 1024;
        var kernels = pkgs.Count(p => p.StartsWith("linux-", StringComparison.Ordinal));
        return new Measurement
        {
            Bytes = bytes,
            Note = $"{pkgs.Count} pacote(s)" + (kernels > 0 ? $", {kernels} deles de kernels antigos" : "") + ": " +
                   string.Join(", ", pkgs.Take(6)) + (pkgs.Count > 6 ? "…" : ""),
        };
    }

    private static async Task<Measurement> MeasureOldSnaps(CancellationToken ct)
    {
        if (Proc.Which("snap") == null) return Measurement.NotAvailable("Snap não está instalado.");
        var (code, output) = await Proc.CaptureAsync("snap", "list --all", null, ct);
        if (code != 0) return Measurement.NotAvailable("O snapd não está rodando neste sistema.");

        var items = new List<CleanupItem>();
        foreach (var line in output.Split('\n').Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 4 || !line.Contains("disabled", StringComparison.Ordinal)) continue;
            var file = $"/var/lib/snapd/snaps/{f[0]}_{f[2]}.snap";
            items.Add(new CleanupItem { Path = file, Label = $"{f[0]} (revisão {f[2]}, versão {f[1]})", Size = Fs.SafeLength(file) });
        }
        if (items.Count == 0) return Measurement.NotAvailable("Nenhuma versão antiga guardada.");
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        return new Measurement { Items = items, Bytes = items.Sum(i => i.Size) };
    }
}
