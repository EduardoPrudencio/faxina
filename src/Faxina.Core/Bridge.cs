using System.Text.Json;
using System.Text.Json.Serialization;

namespace Faxina.Core;

/// <summary>
/// Ponte JS ↔ C#. A página envia {id, method, params}; respondemos {id, ok, result | error}.
/// Eventos assíncronos (progresso, log) vão pelo host como {event, data}.
/// </summary>
public sealed class Bridge
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IPlatform _platform;
    private readonly IHost _host;
    private DiskScanner? _scanner;
    private CancellationTokenSource? _scanCts;
    private readonly Dictionary<string, Measurement> _measured = new();
    private readonly SemaphoreSlim _measureGate = new(3);
    private readonly SemaphoreSlim _runGate = new(1);

    public Bridge(IPlatform platform, IHost host)
    {
        _platform = platform;
        _host = host;
    }

    private void Emit(string evt, object data) => _host.Emit(new { @event = evt, data });

    /// <summary>Processa uma chamada da página e devolve a resposta (já pronta para serializar).</summary>
    public async Task<object> InvokeAsync(string raw)
    {
        string? id = null;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetString();
            var method = root.GetProperty("method").GetString()!;
            var p = root.TryGetProperty("params", out var pe) ? pe.Clone() : default;
            var result = await DispatchAsync(method, p);
            return new { id, ok = true, result };
        }
        catch (Exception ex)
        {
            return new { id, ok = false, error = ex is OperationCanceledException ? "Cancelado." : ex.Message };
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement p)
    {
        switch (method)
        {
            case "system.info":
                return await Task.Run(_platform.GetSystemInfo);
            case "system.hardware":
                return await Task.Run(_platform.GetHardware);
            case "system.memory":
                return _platform.GetMemory();

            case "app.relaunchAdmin":
                if (!_platform.RelaunchAsAdmin()) return false;
                _host.Exit();
                return true;

            // ───── varredura ─────
            case "scan.start":
                return StartScan(Str(p, "path") ?? _platform.SystemRoot);
            case "scan.cancel":
                _scanCts?.Cancel();
                return true;
            case "scan.node":
            {
                var node = _scanner?.Find(Str(p, "path") ?? "") ?? throw new InvalidOperationException("Pasta não encontrada na análise.");
                return NodeDto(node, Int(p, "limit") ?? 80);
            }
            case "scan.summary":
                return Summary();

            // ───── arquivos ─────
            case "dialog.pickFolder":
                return await _host.PickFolderAsync();
            case "shell.reveal":
                _platform.Reveal(Str(p, "path")!);
                return true;
            case "fs.delete":
                return await Task.Run(() => DeletePath(Str(p, "path")!, Bool(p, "permanent")));

            // ───── limpeza ─────
            case "cleanup.list":
                return new
                {
                    targets = _platform.Cleanups.Select(t => new
                    {
                        t.Id, t.Title, t.Description, t.Category, t.Risk, t.RequiresAdmin, t.Selectable,
                    }),
                    projectRoots = _platform.DefaultProjectRoots(),
                };
            case "cleanup.measure":
                return await MeasureAsync(Str(p, "id")!, p.TryGetProperty("options", out var o) ? o : null);
            case "cleanup.run":
                return await RunCleanupAsync(Str(p, "id")!, p);

            // ───── programas ─────
            case "apps.list":
                return await _platform.ListAppsAsync(CancellationToken.None);
            case "apps.uninstall":
            {
                var code = await _platform.UninstallAsync(Str(p, "id")!, line => Emit("log", new { source = "apps", line }), CancellationToken.None);
                return new { code };
            }

            // ───── conta ─────
            case "account.info":
                return _platform.GetAccount();
            case "account.openManage":
            {
                // Só abre o endereço que a própria plataforma indicou (a página não escolhe a URL).
                var url = _platform.GetAccount().ManageUrl ?? throw new InvalidOperationException("Nada para abrir.");
                _platform.OpenUrl(url);
                return true;
            }
            case "account.changePassword":
                return await ChangePasswordAsync(Str(p, "current") ?? "", Str(p, "next") ?? "");

            // ───── recuperar ─────
            case "recover.trash.list":
                return await Task.Run(_platform.Recovery.ListTrash);
            case "recover.trash.restore":
                return await Task.Run(() => LogRestored(_platform.Recovery.RestoreFromTrash(StrList(p, "ids")), "Lixeira"));
            case "recover.versions.info":
                return _platform.Recovery.PreviousVersions();
            case "recover.versions.list":
                return await Task.Run(() => _platform.Recovery.ListSnapshots(Str(p, "path") ?? throw new InvalidOperationException("Escolha uma pasta.")));
            case "recover.versions.browse":
                return await Task.Run(() => _platform.Recovery.BrowseSnapshot(Str(p, "id")!, Str(p, "path")!));
            case "recover.versions.restore":
                return await Task.Run(() => LogRestored(
                    _platform.Recovery.RestoreFromSnapshot(Str(p, "id")!, Str(p, "path")!, StrList(p, "names")), "versão anterior"));
            case "recover.versions.fileHistory":
                _platform.Recovery.OpenFileHistory();
                return true;
            case "recover.deep.tool":
                return _platform.Recovery.DeepTool();
            case "recover.deep.install":
                _platform.Recovery.OpenDeepToolInstall();
                return true;
            case "recover.deep.start":
                return StartDeep(new DeepRequest(Str(p, "source") ?? "", Str(p, "destination") ?? "",
                    Str(p, "mode") == "full" ? "full" : "quick", FileGroups.Validate(StrList(p, "groups")), Str(p, "nameFilter")));
            case "recover.deep.cancel":
                _deepCts?.Cancel();
                return true;

            default:
                throw new InvalidOperationException("Método desconhecido: " + method);
        }
    }

    // ───────────────────────── recuperar ─────────────────────────

    private CancellationTokenSource? _deepCts;

    private List<RestoredItem> LogRestored(List<RestoredItem> items, string from)
    {
        foreach (var i in items)
            Emit("log", new
            {
                source = "recover",
                line = i.Error != null ? $"Erro ao restaurar {i.Name}: {i.Error}"
                    : $"↺ {i.Name} voltou da {from} para {i.Path}{(i.Renamed ? " (com outro nome, já existia um igual)" : "")}",
            });
        return items;
    }

    private object StartDeep(DeepRequest req)
    {
        if (_deepCts != null) throw new InvalidOperationException("Já há uma recuperação em andamento.");
        if (req.NameFilter is { Length: > 200 }) throw new InvalidOperationException("Filtro de nome muito longo.");
        var cts = _deepCts = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        void Log(string line) => Emit("log", new { source = "recover", line });

        var output = Path.Combine(req.Destination, $"Faxina recuperados {DateTime.Now:yyyy-MM-dd HH-mm}");
        _ = Task.Run(async () =>
        {
            var lastPercent = (int?)null;
            // Enquanto a ferramenta roda, conta o que já apareceu na pasta de destino
            using var timer = new Timer(_ =>
            {
                var (files, bytes) = RecoveryKit.Count(output);
                Emit("recover.progress", new { files, bytes, percent = lastPercent, elapsed = (DateTime.UtcNow - started).TotalSeconds, folder = output });
            }, null, 1000, 2000);
            try
            {
                Log($"▶ Recuperação profunda de {req.Source} para {output}");
                await _platform.Recovery.RunDeepAsync(req, output, Log, pc => lastPercent = pc, cts.Token);
                timer.Dispose();
                var (files, bytes) = RecoveryKit.Count(output);
                Log($"✔ Concluído: {files} arquivo(s), {Br.Size(bytes)}, em {output}");
                Emit("recover.done", new { files, bytes, folder = output });
            }
            catch (OperationCanceledException)
            {
                Log("Recuperação cancelada. O que já tinha sido encontrado ficou na pasta de destino.");
                Emit("recover.cancelled", new { });
            }
            catch (Exception ex)
            {
                Log("Erro: " + ex.Message);
                Emit("recover.error", new { message = ex.Message });
            }
            finally { _deepCts = null; }
        });
        return true;
    }

    // ───────────────────────── varredura ─────────────────────────

    private object StartScan(string path)
    {
        if (!Directory.Exists(path)) throw new InvalidOperationException("Pasta não encontrada: " + path);
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        var scanner = new DiskScanner(cts.Token, _platform.ScanExcludes(path));
        _scanner = scanner;

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            var ticking = Task.Run(async () =>
            {
                while (await timer.WaitForNextTickAsync() && !cts.IsCancellationRequested && scanner.FinishedAt == null)
                    Emit("scan.progress", ProgressDto(scanner));
            });
            try
            {
                await Task.Run(() => scanner.Scan(path));
                timer.Dispose();
                Emit("scan.done", Summary()!);
            }
            catch (OperationCanceledException)
            {
                Emit("scan.cancelled", new { path });
            }
            catch (Exception ex)
            {
                Emit("scan.error", new { message = ex.Message });
            }
        });
        return new { path };
    }

    private static object ProgressDto(DiskScanner s) => new
    {
        files = s.FilesScanned,
        bytes = s.BytesScanned,
        current = s.CurrentPath,
        elapsed = (DateTime.Now - s.StartedAt).TotalSeconds,
    };

    private object? Summary()
    {
        var s = _scanner;
        if (s?.Root == null) return null;

        var byCategory = new Dictionary<string, long>();
        var byExt = s.BytesByExt.ToArray();
        foreach (var (ext, bytes) in byExt)
        {
            var cat = DiskScanner.Categories.TryGetValue(ext, out var c) ? c : "Outros";
            byCategory[cat] = byCategory.GetValueOrDefault(cat) + bytes;
        }

        return new
        {
            root = NodeDto(s.Root, 80),
            files = s.FilesScanned,
            bytes = s.Root.Size,
            cloudBytes = s.CloudBytes,
            inaccessible = s.Inaccessible,
            seconds = ((s.FinishedAt ?? DateTime.Now) - s.StartedAt).TotalSeconds,
            categories = byCategory.OrderByDescending(x => x.Value).Select(x => new { name = x.Key, bytes = x.Value }),
            extensions = byExt.OrderByDescending(x => x.Value).Take(25)
                .Select(x => new { ext = string.IsNullOrEmpty(x.Key) ? "(sem extensão)" : x.Key.ToLowerInvariant(), bytes = x.Value }),
            topFiles = s.TopFiles().Select(f => new
            {
                path = f.Path, name = Path.GetFileName(f.Path), size = f.Size, modified = f.Modified,
                category = DiskScanner.Categories.GetValueOrDefault(Path.GetExtension(f.Path), "Outros"),
            }),
            heavy = s.HeavyFolders(),
        };
    }

    private static object NodeDto(DirNode n, int limit)
    {
        var kids = n.Children.Take(limit).Select(c => new
        {
            name = c.Name, path = c.Path, size = c.Size, files = c.FileCount, dirs = c.DirCount,
            modified = c.Modified, isLink = c.IsLink, isMount = c.IsMount, hasChildren = c.Children.Count > 0,
            preview = c.Children.Take(8).Select(g => new { name = g.Name, size = g.Size }),
        }).ToList();
        var rest = n.Children.Skip(limit).ToList();
        var crumbs = new List<object>();
        for (var x = n; x != null; x = x.Parent) crumbs.Insert(0, new { name = x.Name, path = x.Path });
        return new
        {
            name = n.Name, path = n.Path, size = n.Size, filesSize = n.FilesSize, files = n.FileCount, dirs = n.DirCount,
            children = kids,
            others = rest.Count > 0 ? new { count = rest.Count, size = rest.Sum(r => r.Size) } : null,
            breadcrumb = crumbs,
        };
    }

    private object DeletePath(string path, bool permanent)
    {
        if (string.IsNullOrEmpty(path) || string.Equals(Fs.TrimSep(path), Fs.TrimSep(Path.GetPathRoot(path) ?? ""), Fs.PathComparison))
            throw new InvalidOperationException("Não é permitido apagar a raiz do disco.");
        if (_platform.WhyProtected(path) is { } why) throw new InvalidOperationException(why);

        long bytes, files;
        if (File.Exists(path))
        {
            bytes = Fs.SafeLength(path); files = 1;
            if (permanent) File.Delete(path);
            else _platform.MoveToTrash(path);
        }
        else if (Directory.Exists(path))
        {
            var node = _scanner?.Find(path);
            bytes = node?.Size ?? Fs.FolderSize(path); files = node?.FileCount ?? 0;
            if (permanent)
            {
                var r = Fs.DeleteContents(path, removeRoot: true);
                if (Directory.Exists(path))
                    return new { ok = false, freed = r.Bytes, message = $"{Br.Count(r.Failed)} arquivo(s) em uso ou sem permissão não foram apagados." };
            }
            else _platform.MoveToTrash(path);
        }
        else throw new InvalidOperationException("Item não existe mais.");

        _scanner?.Forget(path, bytes, files);
        return new { ok = true, freed = bytes };
    }

    // ───────────────────────── limpeza ─────────────────────────

    private CleanupTarget Target(string id) =>
        _platform.Cleanups.FirstOrDefault(t => t.Id == id) ?? throw new InvalidOperationException("Limpeza desconhecida.");

    private async Task<object> MeasureAsync(string id, JsonElement? options)
    {
        var target = Target(id);
        await _measureGate.WaitAsync();
        try
        {
            var m = await target.Measure(options, CancellationToken.None);
            lock (_measured) _measured[id] = m;
            return m;
        }
        finally { _measureGate.Release(); }
    }

    private async Task<object> RunCleanupAsync(string id, JsonElement p)
    {
        var target = Target(id);
        if (target.RequiresAdmin && !_platform.IsAdmin)
            throw new InvalidOperationException(_platform.Name == "linux" ? "Esta limpeza precisa de root (rode o Faxina com sudo)." : "Esta limpeza precisa de administrador.");

        Measurement? m;
        lock (_measured) _measured.TryGetValue(id, out m);
        m ??= await target.Measure(null, CancellationToken.None);

        // Só executa sobre itens que a própria medição encontrou: a página não consegue mandar apagar um caminho qualquer.
        var items = m.Items ?? [];
        if (p.TryGetProperty("selected", out var sel) && sel.ValueKind == JsonValueKind.Array)
        {
            var chosen = new HashSet<string>(sel.EnumerateArray().Select(x => x.GetString()!), Fs.PathComparer);
            items = items.Where(i => chosen.Contains(i.Path)).ToList();
        }
        else if (target.Selectable)
        {
            items = items.Where(i => i.Selected).ToList();
        }

        await _runGate.WaitAsync();
        try
        {
            var freeBefore = new DriveInfo(_platform.SystemRoot).AvailableFreeSpace;
            void Log(string line) => Emit("log", new { source = id, line });
            Log($"▶ {target.Title}");
            await target.Run(new RunContext { Log = Log, Items = items, Ct = CancellationToken.None });
            var free = new DriveInfo(_platform.SystemRoot).AvailableFreeSpace;
            var freed = Math.Max(0, free - freeBefore);
            Log($"✔ Concluído. Espaço livre aumentou {Br.Size(freed)}.");
            lock (_measured) _measured.Remove(id);
            return new { freed, free };
        }
        finally { _runGate.Release(); }
    }

    // ───────────────────────── senha ─────────────────────────

    private readonly SemaphoreSlim _passwordGate = new(1);
    private int _wrongAttempts;
    private DateTime _lockedUntil;

    /// <summary>
    /// Valida, aplica um bloqueio crescente após senhas atuais erradas (para a página não virar um jeito de
    /// adivinhar a senha) e delega ao sistema. As senhas não são registradas em lugar nenhum.
    /// </summary>
    private async Task<object> ChangePasswordAsync(string current, string next)
    {
        var account = _platform.GetAccount();
        if (!account.CanChange) throw new InvalidOperationException(account.Reason ?? "Não é possível trocar a senha por aqui.");
        if (current.Length == 0) throw new InvalidOperationException("Digite a senha atual.");
        if (next.Length < account.MinLength) throw new InvalidOperationException($"A nova senha precisa ter pelo menos {account.MinLength} caracteres.");
        if (next.Length > 256) throw new InvalidOperationException("A nova senha é longa demais.");
        if (next.Any(char.IsControl)) throw new InvalidOperationException("A nova senha não pode ter quebras de linha nem caracteres de controle.");
        if (next == current) throw new InvalidOperationException("A nova senha precisa ser diferente da atual.");

        await _passwordGate.WaitAsync();
        try
        {
            var wait = _lockedUntil - DateTime.Now;
            if (wait > TimeSpan.Zero)
                throw new InvalidOperationException($"Muitas tentativas com a senha atual errada. Aguarde {Math.Ceiling(wait.TotalSeconds)} s.");
            try
            {
                await _platform.ChangePasswordAsync(current, next, CancellationToken.None);
                _wrongAttempts = 0;
                return new { ok = true };
            }
            catch (PasswordException ex)
            {
                if (ex.WrongCurrent && ++_wrongAttempts >= 3)
                    _lockedUntil = DateTime.Now.AddSeconds(30 * (_wrongAttempts - 2));
                throw new InvalidOperationException(ex.Message);
            }
        }
        finally { _passwordGate.Release(); }
    }

    // ───────────────────────── util ─────────────────────────

    private static string? Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    private static bool Bool(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<string> StrList(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];
}
