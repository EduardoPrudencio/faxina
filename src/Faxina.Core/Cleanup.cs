using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Faxina.Core;

public sealed class CleanupItem
{
    public required string Path { get; init; }
    public string? Label { get; init; }
    public long Size { get; set; }
    public DateTime? LastActivity { get; init; }
    public bool Selected { get; set; } = true;
    public string? Note { get; init; }
}

public sealed class Measurement
{
    public long Bytes { get; set; }
    public bool Available { get; set; } = true;
    public bool Unknown { get; set; }          // o ganho só é conhecido depois de rodar
    public string? Note { get; set; }
    public List<CleanupItem>? Items { get; set; }

    public static Measurement NotAvailable(string note) => new() { Available = false, Note = note };
}

public sealed class RunContext
{
    public required Action<string> Log { get; init; }
    public required IReadOnlyList<CleanupItem> Items { get; init; }   // já filtrados pelo que o usuário marcou
    public CancellationToken Ct { get; init; }
}

public sealed class CleanupTarget
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public string Risk { get; init; } = "seguro";          // seguro | atencao
    public bool RequiresAdmin { get; init; }
    public bool Selectable { get; init; }                   // mostra a lista de itens com caixas de seleção
    public required Func<JsonElement?, CancellationToken, Task<Measurement>> Measure { get; init; }
    public required Func<RunContext, Task> Run { get; init; }
}

/// <summary>Peças usadas pelos catálogos de limpeza de cada sistema.</summary>
public static class CleanupKit
{
    public static Measurement FromPaths(CancellationToken ct, params string[] paths) =>
        FromLabeled(ct, paths.Select(p => (p, p)).ToList());

    public static Measurement FromLabeled(CancellationToken ct, List<(string path, string label)> paths, TimeSpan? olderThan = null)
    {
        var items = new List<CleanupItem>();
        foreach (var (path, label) in paths)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) continue;
            var size = Fs.FolderSize(path, ct, olderThan);
            if (size > 0) items.Add(new CleanupItem { Path = path, Label = label, Size = size });
        }
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        if (items.Count == 0) return Measurement.NotAvailable("Nada para limpar.");
        return new Measurement { Items = items, Bytes = items.Sum(i => i.Size) };
    }

    public static Measurement OnlyIfExists(CancellationToken ct, string path, string missing) =>
        Directory.Exists(path) ? FromPaths(ct, path) : Measurement.NotAvailable(missing);

    public static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : []; } catch { return []; }
    }

    public static Task DeletePaths(RunContext ctx, TimeSpan? olderThan = null, bool removeRoot = false) => Task.Run(() =>
    {
        foreach (var item in ctx.Items)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            ctx.Log($"Limpando {item.Path}…");
            var r = Fs.DeleteContents(item.Path, olderThan, removeRoot, ctx.Ct);
            ctx.Log($"  {Br.Count(r.Files)} arquivo(s), {Br.Size(r.Bytes)} removidos" +
                    (r.Failed > 0 ? $" · {Br.Count(r.Failed)} em uso/sem permissão foram mantidos" : ""));
        }
    });

    /// <summary>Lê tamanhos como "21.3GB", "512MB" (docker usa base 1000).</summary>
    public static long ParseHumanSize(string s, double kilo = 1000)
    {
        var m = Regex.Match(s, @"([\d.]+)\s*([kKMGTP]?)i?B?");
        if (!m.Success) return 0;
        var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var exp = "KMGTP".IndexOf(m.Groups[2].Value.ToUpperInvariant(), StringComparison.Ordinal) + 1;
        return (long)(v * Math.Pow(kilo, m.Groups[2].Value.Length == 0 ? 0 : exp));
    }

    // ───────────── node_modules ─────────────

    private static readonly HashSet<string> SkipWhenSearching = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "AppData", "$Recycle.Bin", "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        ".nuget", ".gradle", ".cache", ".local", ".npm", ".cargo", ".rustup", "snap", "dist", "bin", "obj",
    };
    private static readonly HashSet<string> IgnoreForActivity = new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "dist", "build", "bin", "obj", ".next", ".nuxt", "coverage", ".cache", ".turbo", "android", "ios", ".gradle", ".expo" };

    public static Measurement MeasureNodeModules(JsonElement? opts, List<string> defaultRoots, CancellationToken ct)
    {
        var roots = new List<string>();
        var idleDays = 90;
        if (opts is { ValueKind: JsonValueKind.Object } o)
        {
            if (o.TryGetProperty("roots", out var r) && r.ValueKind == JsonValueKind.Array)
                roots.AddRange(r.EnumerateArray().Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x)));
            if (o.TryGetProperty("idleDays", out var d) && d.TryGetInt32(out var days)) idleDays = Math.Max(1, days);
        }
        if (roots.Count == 0) roots = defaultRoots;
        if (roots.Count == 0) return Measurement.NotAvailable("Nenhuma pasta de projetos encontrada. Escolha uma pasta.");

        var found = new List<string>();
        var stack = new Stack<(string dir, int depth)>(roots.Select(r => (r, 0)));
        var seen = new HashSet<string>(Fs.PathComparer);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            if (!seen.Add(dir)) continue;
            foreach (var sub in SafeDirs(dir))
            {
                var name = Path.GetFileName(sub);
                try { if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; } catch { continue; }
                if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) { found.Add(sub); continue; }
                if (SkipWhenSearching.Contains(name) || depth >= 8) continue;
                stack.Push((sub, depth + 1));
            }
        }

        var cutoff = DateTime.Now.AddDays(-idleDays);
        var items = new List<CleanupItem>();
        foreach (var nm in found)
        {
            ct.ThrowIfCancellationRequested();
            var project = Path.GetDirectoryName(nm)!;
            var last = LastActivity(project, ct);
            var size = Fs.FolderSize(nm, ct);
            if (size == 0) continue;
            items.Add(new CleanupItem
            {
                Path = nm, Size = size, LastActivity = last, Selected = last < cutoff,
                Label = Path.GetRelativePath(roots.FirstOrDefault(r => nm.StartsWith(r, Fs.PathComparison)) ?? "", project),
            });
        }
        items.Sort((a, b) => b.Size.CompareTo(a.Size));
        var idle = items.Where(i => i.Selected).ToList();
        return new Measurement
        {
            Items = items, Bytes = idle.Sum(i => i.Size),
            Note = $"{items.Count} pasta(s) node_modules em {string.Join(", ", roots)} · {idle.Count} parada(s) há mais de {idleDays} dias.",
        };
    }

    /// <summary>Data do arquivo mais recente do projeto, ignorando dependências e saídas de build.</summary>
    private static DateTime LastActivity(string project, CancellationToken ct)
    {
        var newest = DateTime.MinValue;
        var visited = 0;
        var stack = new Stack<string>([project]);
        while (stack.Count > 0 && visited < 8000)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            try
            {
                foreach (var e in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    visited++;
                    if (e is DirectoryInfo d) { if (!IgnoreForActivity.Contains(d.Name)) stack.Push(d.FullName); }
                    else if (e.LastWriteTime > newest) newest = e.LastWriteTime;
                }
            }
            catch { }
        }
        return newest == DateTime.MinValue ? Directory.GetLastWriteTime(project) : newest;
    }
}

/// <summary>Onde cada ferramenta de desenvolvimento guarda o cache, em cada sistema.</summary>
public sealed record DevPaths
{
    public required string NpmCache { get; init; }
    public required string YarnCache { get; init; }
    public required string PnpmStore { get; init; }
    public required string[] NuGet { get; init; }
    public required string GradleHome { get; init; }
    public required string PipCache { get; init; }
    public required string CypressCache { get; init; }
    public required string EditorConfigRoot { get; init; }   // %APPDATA% ou ~/.config
    public required Func<List<string>> ProjectRoots { get; init; }
}

/// <summary>Limpezas de ferramentas de desenvolvimento, iguais nos dois sistemas.</summary>
public static class DevCleanups
{
    public static IEnumerable<CleanupTarget> Build(DevPaths p, string dockerHint)
    {
        yield return new()
        {
            Id = "editor-cache", Category = "Programas", Title = "Cache do VS Code e do Cursor",
            Description = "Caches, logs e extensões baixadas dos editores. Configurações e histórico não são tocados. Feche os editores antes.",
            Selectable = true,
            Measure = (_, ct) => Task.Run(() =>
            {
                var paths = new List<(string, string)>();
                foreach (var app in new[] { "Code", "Cursor", "Code - Insiders" })
                foreach (var sub in new[] { "Cache", "CachedData", "CachedExtensionVSIXs", "Code Cache", "GPUCache", "logs", Path.Combine("Service Worker", "CacheStorage"), "CachedProfilesData" })
                    paths.Add((Path.Combine(p.EditorConfigRoot, app, sub), $"{app} · {sub}"));
                return CleanupKit.FromLabeled(ct, paths);
            }),
            Run = ctx => CleanupKit.DeletePaths(ctx),
        };

        yield return new()
        {
            Id = "docker-prune", Category = "Docker, WSL e VMs", Title = "Docker: cache de build e imagens sem uso",
            Description = "docker builder prune + docker image prune. Volumes (bancos de dados) e containers não são tocados.",
            Measure = async (_, ct) =>
            {
                if (Proc.Which("docker") == null) return Measurement.NotAvailable("Docker não está instalado.");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(25));
                try
                {
                    var (code, output) = await Proc.CaptureAsync("docker", "system df --format \"{{json .}}\"", null, cts.Token);
                    if (code != 0) return new Measurement { Unknown = true, Note = dockerHint };
                    long total = 0;
                    var parts = new List<string>();
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!line.TrimStart().StartsWith('{')) continue;
                        using var doc = JsonDocument.Parse(line);
                        var type = doc.RootElement.GetProperty("Type").GetString();
                        if (type is not ("Build Cache" or "Images")) continue;
                        var bytes = CleanupKit.ParseHumanSize(doc.RootElement.GetProperty("Reclaimable").GetString() ?? "0B");
                        total += bytes;
                        parts.Add($"{(type == "Images" ? "imagens" : "cache de build")}: {Br.Size(bytes)}");
                    }
                    return new Measurement { Bytes = total, Note = string.Join(" · ", parts) };
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new Measurement { Unknown = true, Note = "O Docker não respondeu. Ele está rodando?" };
                }
            },
            Run = async ctx =>
            {
                await Proc.RunAsync("docker", "builder prune -a -f", ctx.Log, ctx.Ct);
                await Proc.RunAsync("docker", "image prune -a -f", ctx.Log, ctx.Ct);
                if (OperatingSystem.IsWindows())
                    ctx.Log("Dica: rode também \"Compactar discos virtuais\" para o Windows recuperar esse espaço.");
            },
        };

        yield return new()
        {
            Id = "node-modules", Category = "Desenvolvimento", Title = "node_modules de projetos parados",
            Description = "Dependências de projetos sem alteração há mais de 90 dias. Para voltar a usar, é só rodar npm install.",
            Selectable = true,
            Measure = (opts, ct) => Task.Run(() => CleanupKit.MeasureNodeModules(opts, p.ProjectRoots(), ct)),
            Run = ctx => CleanupKit.DeletePaths(ctx, removeRoot: true),
        };
        yield return new()
        {
            Id = "npm-cache", Category = "Desenvolvimento", Title = "Cache do npm",
            Description = "Pacotes baixados pelo npm. São baixados de novo quando precisar.",
            Measure = (_, ct) => Task.Run(() => CleanupKit.OnlyIfExists(ct, p.NpmCache, "Cache vazio.")),
            Run = async ctx =>
            {
                if (Proc.Which("npm") != null) await Proc.ShellAsync("npm cache clean --force", ctx.Log, ctx.Ct);
                else await CleanupKit.DeletePaths(ctx);
            },
        };
        yield return new()
        {
            Id = "yarn-cache", Category = "Desenvolvimento", Title = "Cache do Yarn",
            Description = "Pacotes baixados pelo Yarn.",
            Measure = (_, ct) => Task.Run(() => CleanupKit.OnlyIfExists(ct, p.YarnCache, "Cache vazio.")),
            Run = async ctx =>
            {
                if (Proc.Which("yarn") != null) await Proc.ShellAsync("yarn cache clean", ctx.Log, ctx.Ct);
                await CleanupKit.DeletePaths(ctx);
            },
        };
        yield return new()
        {
            Id = "pnpm-store", Category = "Desenvolvimento", Title = "Store do pnpm",
            Description = "Remove do store do pnpm os pacotes que nenhum projeto usa mais.",
            Measure = (_, ct) => Task.Run(() => CleanupKit.OnlyIfExists(ct, p.PnpmStore, "pnpm não encontrado.")),
            Run = ctx => Proc.Which("pnpm") != null ? Proc.ShellAsync("pnpm store prune", ctx.Log, ctx.Ct) : CleanupKit.DeletePaths(ctx),
        };
        yield return new()
        {
            Id = "nuget-cache", Category = "Desenvolvimento", Title = "Cache do NuGet (.NET)",
            Description = "Pacotes NuGet baixados. O próximo dotnet restore baixa de novo o que for preciso.",
            Measure = (_, ct) => Task.Run(() => CleanupKit.FromPaths(ct, p.NuGet)),
            Run = async ctx =>
            {
                if (Proc.Which("dotnet") != null) await Proc.RunAsync("dotnet", "nuget locals all --clear", ctx.Log, ctx.Ct);
                else await CleanupKit.DeletePaths(ctx);
            },
        };
        yield return new()
        {
            Id = "gradle-cache", Category = "Desenvolvimento", Title = "Cache do Gradle (Android/Java)",
            Description = "Dependências e distribuições baixadas pelo Gradle. O próximo build baixa de novo (pode demorar).",
            Selectable = true,
            Measure = (_, ct) => Task.Run(() => CleanupKit.FromLabeled(ct,
            [
                (Path.Combine(p.GradleHome, "caches"), "Dependências (caches)"),
                (Path.Combine(p.GradleHome, "wrapper", "dists"), "Distribuições do Gradle (wrapper)"),
                (Path.Combine(p.GradleHome, "daemon"), "Logs do daemon"),
            ])),
            Run = ctx => CleanupKit.DeletePaths(ctx, removeRoot: true),
        };
        yield return new()
        {
            Id = "pip-cache", Category = "Desenvolvimento", Title = "Cache do pip (Python)",
            Description = "Pacotes Python baixados.",
            Measure = (_, ct) => Task.Run(() => CleanupKit.OnlyIfExists(ct, p.PipCache, "Cache vazio.")),
            Run = ctx => CleanupKit.DeletePaths(ctx),
        };
        yield return new()
        {
            Id = "cypress-old", Category = "Desenvolvimento", Title = "Versões antigas do Cypress",
            Description = "Binários do Cypress de versões anteriores. A versão mais nova é mantida.",
            Selectable = true,
            Measure = (_, ct) => Task.Run(() =>
            {
                var versions = CleanupKit.SafeDirs(p.CypressCache)
                    .Select(d => (dir: d, ver: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                    .Where(x => x.ver != null).OrderByDescending(x => x.ver).ToList();
                if (versions.Count <= 1) return Measurement.NotAvailable("Só há uma versão instalada.");
                return CleanupKit.FromLabeled(ct, versions.Skip(1).Select(v => (v.dir, $"Cypress {v.ver}")).ToList());
            }),
            Run = ctx => CleanupKit.DeletePaths(ctx, removeRoot: true),
        };
    }
}
