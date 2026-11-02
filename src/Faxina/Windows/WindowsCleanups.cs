using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Faxina.Core;
using static Faxina.Core.CleanupKit;

namespace Faxina.Windows;

/// <summary>Catálogo de limpezas do Windows: as específicas do sistema mais as de desenvolvimento (comuns).</summary>
public static class WindowsCleanups
{
    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string SysDrive => Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    private static readonly long GB = 1L << 30;

    public static readonly List<CleanupTarget> All = [.. Build(), .. DevCleanups.Build(DevPaths, "Abra o Docker Desktop para medir.")];

    public static List<string> DefaultProjectRoots() => new[]
        {
            Path.Combine(SysDrive, "Projetos"), Path.Combine(SysDrive, "Projects"), Path.Combine(SysDrive, "dev"),
            Path.Combine(Home, "source"), Path.Combine(Home, "Projects"), Path.Combine(Home, "repos"),
            Path.Combine(Home, @"Documents\GitHub"), Path.Combine(Home, "Desktop"),
        }.Where(Directory.Exists).ToList();

    private static DevPaths DevPaths => new()
    {
        NpmCache = Path.Combine(Local, @"npm-cache\_cacache"),
        YarnCache = Path.Combine(Local, @"Yarn\Cache"),
        PnpmStore = Path.Combine(Local, @"pnpm\store"),
        NuGet = [Path.Combine(Home, @".nuget\packages"), Path.Combine(Local, @"NuGet\v3-cache"), Path.Combine(Local, @"NuGet\plugins-cache"), Path.Combine(Path.GetTempPath(), "NuGetScratch")],
        GradleHome = Path.Combine(Home, ".gradle"),
        PipCache = Path.Combine(Local, @"pip\cache"),
        CypressCache = Path.Combine(Local, @"Cypress\Cache"),
        EditorConfigRoot = Roaming,
        ProjectRoots = DefaultProjectRoots,
    };

    private static List<CleanupTarget> Build() => new()
    {
        // ───────────── Windows ─────────────
        new()
        {
            Id = "temp-user", Category = "Windows", Title = "Arquivos temporários do usuário",
            Description = "Pasta Temp do seu usuário. Só apaga arquivos com mais de 24 h; os que estiverem em uso são ignorados.",
            Measure = (_, ct) => Task.Run(() => FromLabeled(ct, [(Path.GetTempPath(), "Temp do usuário")], TimeSpan.FromHours(24))),
            Run = ctx => DeletePaths(ctx, TimeSpan.FromHours(24)),
        },
        new()
        {
            Id = "temp-windows", Category = "Windows", Title = "Arquivos temporários do Windows",
            Description = "C:\\Windows\\Temp. Só arquivos com mais de 24 h.", RequiresAdmin = true,
            Measure = (_, ct) => Task.Run(() => Elevation.IsAdmin
                ? FromLabeled(ct, [(Path.Combine(Win, "Temp"), "Windows\\Temp")], TimeSpan.FromHours(24))
                : new Measurement { Unknown = true, Note = "Reabra como administrador para medir." }),
            Run = ctx => DeletePaths(ctx, TimeSpan.FromHours(24)),
        },
        new()
        {
            Id = "recycle", Category = "Windows", Title = "Lixeira", Risk = "atencao",
            Description = "Esvazia a Lixeira de todos os discos. Os arquivos são apagados de vez.",
            Measure = (_, _) =>
            {
                var (bytes, items) = Native.QueryRecycleBin();
                return Task.FromResult(new Measurement { Bytes = bytes, Note = $"{items} item(ns)" });
            },
            Run = ctx =>
            {
                ctx.Log(Native.EmptyRecycleBin() ? "Lixeira esvaziada." : "Não foi possível esvaziar a Lixeira.");
                return Task.CompletedTask;
            },
        },
        new()
        {
            Id = "crash-dumps", Category = "Windows", Title = "Despejos de memória e relatórios de erro",
            Description = "Arquivos .dmp gerados quando programas ou o Windows travam, e a fila do Relatório de Erros.",
            Selectable = true,
            Measure = (_, ct) => Task.Run(() => FromPaths(ct,
                Path.Combine(Local, "CrashDumps"),
                Path.Combine(Local, @"Microsoft\Windows\WER"),
                Path.Combine(Win, "Minidump"),
                Path.Combine(Win, "MEMORY.DMP"),
                @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive",
                @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue")),
            Run = ctx => DeletePaths(ctx),
        },
        new()
        {
            Id = "wu-cache", Category = "Windows", Title = "Downloads do Windows Update",
            Description = "Pacotes de atualização já baixados (SoftwareDistribution\\Download). O Windows baixa de novo se precisar.",
            RequiresAdmin = true,
            Measure = (_, ct) => Task.Run(() => FromPaths(ct, Path.Combine(Win, @"SoftwareDistribution\Download"))),
            Run = async ctx =>
            {
                await Proc.RunAsync("net.exe", "stop wuauserv", ctx.Log, ctx.Ct, Proc.Oem);
                await DeletePaths(ctx);
                await Proc.RunAsync("net.exe", "start wuauserv", ctx.Log, ctx.Ct, Proc.Oem);
            },
        },
        new()
        {
            Id = "delivery-opt", Category = "Windows", Title = "Cache de Otimização de Entrega",
            Description = "Cópias de atualizações que o Windows compartilha com outros PCs.", RequiresAdmin = true,
            Measure = (_, ct) => Task.Run(() => Elevation.IsAdmin
                ? FromPaths(ct, Path.Combine(Win, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"))
                : new Measurement { Unknown = true, Note = "Reabra como administrador para medir." }),
            Run = ctx => Proc.PowerShellAsync("Delete-DeliveryOptimizationCache -Force", ctx.Log, ctx.Ct),
        },
        new()
        {
            Id = "dism", Category = "Windows", Title = "Limpeza de componentes do Windows (WinSxS)",
            Description = "Remove versões antigas de componentes substituídas por atualizações (DISM). Pode levar 10–30 min.",
            RequiresAdmin = true,
            Measure = (_, _) => Task.FromResult(new Measurement { Unknown = true, Note = "O ganho só aparece depois; costuma liberar de 1 a 5 GB." }),
            Run = ctx => Proc.RunAsync("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", ctx.Log, ctx.Ct, Proc.Oem),
        },
        new()
        {
            Id = "windows-old", Category = "Windows", Title = "Instalação anterior do Windows", Risk = "atencao",
            Description = "Windows.old e pastas de upgrade. Depois disso não dá mais para voltar à versão anterior do Windows.",
            RequiresAdmin = true, Selectable = true,
            Measure = (_, ct) => Task.Run(() =>
            {
                var m = FromPaths(ct, Path.Combine(SysDrive, "Windows.old"), Path.Combine(SysDrive, "$Windows.~BT"),
                    Path.Combine(SysDrive, "$Windows.~WS"), Path.Combine(SysDrive, "ESD"));
                return m.Available ? m : Measurement.NotAvailable("Não há instalação anterior neste PC.");
            }),
            Run = async ctx =>
            {
                foreach (var item in ctx.Items) await TakeOwnership(ctx, item.Path);
                await DeletePaths(ctx, removeRoot: true);
            },
        },
        new()
        {
            Id = "hibernation", Category = "Windows", Title = "Desativar hibernação", Risk = "atencao",
            Description = "Apaga o hiberfil.sys. O PC deixa de hibernar e a Inicialização Rápida é desligada (o modo Suspender continua funcionando).",
            RequiresAdmin = true,
            Measure = (_, _) =>
            {
                var size = Fs.SafeLength(Path.Combine(SysDrive, "hiberfil.sys"));
                return Task.FromResult(size > 0 ? new Measurement { Bytes = size } : Measurement.NotAvailable("A hibernação já está desativada."));
            },
            Run = ctx => Proc.RunAsync("powercfg.exe", "/hibernate off", ctx.Log, ctx.Ct, Proc.Oem),
        },
        new()
        {
            Id = "shadow", Category = "Windows", Title = "Limitar pontos de restauração a 10 GB", Risk = "atencao",
            Description = "Reduz o espaço reservado às cópias de sombra. O Windows apaga os pontos mais antigos e mantém os mais recentes.",
            RequiresAdmin = true,
            Measure = (_, _) => Task.Run(() =>
            {
                if (!Elevation.IsAdmin) return new Measurement { Unknown = true, Note = "Reabra como administrador para medir." };
                var s = WindowsSystem.GetShadowStorage(SysDrive);
                if (s == null) return Measurement.NotAvailable("Não foi possível ler os pontos de restauração.");
                return new Measurement
                {
                    Bytes = Math.Max(0, s.Used - 10 * GB),
                    Note = $"{s.Count} ponto(s), usando {Br.Size(s.Used)} de um limite de {Br.Size(s.Max)}.",
                };
            }),
            Run = async ctx =>
            {
                var s = WindowsSystem.GetShadowStorage(SysDrive);
                var target = Math.Min(10 * GB, s?.Max ?? 10 * GB) / (1L << 20);
                var d = SysDrive.TrimEnd('\\');
                await Proc.RunAsync("vssadmin.exe", $"resize shadowstorage /for={d} /on={d} /maxsize={target}MB", ctx.Log, ctx.Ct, Proc.Oem);
            },
        },

        // ───────────── Programas ─────────────
        new()
        {
            Id = "mcafee-logs", Category = "Programas", Title = "Logs do McAfee",
            Description = "Registros de atividade do McAfee (wps\\log), que podem crescer sem limite.", RequiresAdmin = true,
            Measure = (_, ct) => Task.Run(() => OnlyIfExists(ct, @"C:\ProgramData\McAfee\wps\log", "McAfee não encontrado.")),
            Run = ctx => DeletePaths(ctx),
        },
        new()
        {
            Id = "dell-leftovers", Category = "Programas", Title = "Pacotes de drivers extraídos (Dell)", Risk = "atencao",
            Description = "C:\\Drivers e C:\\dell guardam instaladores de drivers já instalados. Se precisar de novo, dá para baixar no site da Dell.",
            RequiresAdmin = true, Selectable = true,
            Measure = (_, ct) => Task.Run(() =>
            {
                var m = FromPaths(ct, Path.Combine(SysDrive, "Drivers"), Path.Combine(SysDrive, "dell"));
                return m.Available ? m : Measurement.NotAvailable("Nada encontrado.");
            }),
            Run = ctx => DeletePaths(ctx, removeRoot: true),
        },
        new()
        {
            Id = "dell-snapshots", Category = "Programas", Title = "Snapshots do Dell SupportAssist", Risk = "atencao",
            Description = "Cópias do Windows que o SupportAssist Remediation guarda para o \"Reparo do sistema\". Depois de apagar, desinstale o Remediation ou desligue o Reparo do sistema, senão ele cria novas.",
            RequiresAdmin = true,
            Measure = async (_, ct) =>
            {
                if (!Directory.Exists(DellSnapshots)) return Measurement.NotAvailable("Nenhum snapshot da Dell encontrado.");
                if (!Elevation.IsAdmin) return new Measurement { Unknown = true, Note = "A pasta é protegida. Reabra como administrador para medir." };
                var bytes = await RobocopySizeAsync(DellSnapshots, ct);
                return bytes > 0 ? new Measurement { Bytes = bytes, Items = [new CleanupItem { Path = DellSnapshots, Size = bytes }] }
                                 : Measurement.NotAvailable("Nenhum snapshot da Dell encontrado.");
            },
            Run = async ctx =>
            {
                ctx.Log("Parando o serviço do SupportAssist Remediation…");
                await Proc.PowerShellAsync("Get-Service | Where-Object { $_.DisplayName -like '*SupportAssist*Remediation*' } | Stop-Service -Force -ErrorAction SilentlyContinue", ctx.Log, ctx.Ct);
                await TakeOwnership(ctx, DellSnapshots);
                await DeletePaths(ctx);
            },
        },
        new()
        {
            Id = "vs-packages", Category = "Programas", Title = "Cache do instalador do Visual Studio",
            Description = "Pacotes que o Visual Studio Installer guarda para reparar ou modificar a instalação. Se precisar, ele baixa de novo. Desligue \"Manter cache de download\" no instalador para não voltar.",
            RequiresAdmin = true, Selectable = true,
            Measure = (_, ct) => Task.Run(() =>
            {
                var root = @"C:\ProgramData\Microsoft\VisualStudio\Packages";
                if (!Directory.Exists(root)) return Measurement.NotAvailable("Visual Studio não encontrado.");
                // Pastas que começam com "_" (_Instances, _bootstrapper…) guardam o estado da instalação: nunca entram.
                var dirs = SafeDirs(root).Where(d => !Path.GetFileName(d).StartsWith('_')).ToList();
                var m = FromLabeled(ct, dirs.Select(d => (d, Path.GetFileName(d))).ToList());
                if (m.Items is { Count: > 20 })
                {
                    // Centenas de pacotes: mostra só os maiores na lista, mas todos continuam marcados.
                    m.Note = $"{m.Items.Count} pacotes em cache.";
                }
                return m;
            }),
            Run = ctx => DeletePaths(ctx, removeRoot: true),
        },
        new()
        {
            Id = "browser-cache", Category = "Programas", Title = "Cache dos navegadores",
            Description = "Cache de páginas do Chrome, Edge e Brave. Senhas, favoritos e logins continuam. Feche os navegadores antes.",
            Selectable = true,
            Measure = (_, ct) => Task.Run(() =>
            {
                var paths = new List<(string, string)>();
                foreach (var (browser, dir) in new[] { ("Chrome", @"Google\Chrome\User Data"), ("Edge", @"Microsoft\Edge\User Data"), ("Brave", @"BraveSoftware\Brave-Browser\User Data") })
                {
                    var root = Path.Combine(Local, dir);
                    if (!Directory.Exists(root)) continue;
                    foreach (var profile in SafeDirs(root).Where(p => File.Exists(Path.Combine(p, "Preferences"))))
                    foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage" })
                        paths.Add((Path.Combine(profile, sub), $"{browser} ({Path.GetFileName(profile)}) · {sub}"));
                }
                return FromLabeled(ct, paths);
            }),
            Run = ctx => DeletePaths(ctx),
        },
        new()
        {
            Id = "old-installers", Category = "Programas", Title = "Instaladores antigos em Downloads", Risk = "atencao",
            Description = "Arquivos .exe, .msi, .iso e .zip com mais de 30 dias na pasta Downloads. Nada vem marcado: escolha o que apagar.",
            Selectable = true,
            Measure = (_, ct) => Task.Run(() =>
            {
                var dl = Path.Combine(Home, "Downloads");
                var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".exe", ".msi", ".iso", ".zip", ".rar", ".7z", ".msix" };
                var items = new List<CleanupItem>();
                if (Directory.Exists(dl))
                {
                    foreach (var f in new DirectoryInfo(dl).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 2 }))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!exts.Contains(f.Extension) || f.LastWriteTime > DateTime.Now.AddDays(-30)) continue;
                        items.Add(new CleanupItem { Path = f.FullName, Label = f.Name, Size = f.Length, LastActivity = f.LastWriteTime, Selected = false });
                    }
                }
                items.Sort((a, b) => b.Size.CompareTo(a.Size));
                return new Measurement { Items = items, Bytes = 0, Available = items.Count > 0, Note = items.Count == 0 ? "Nenhum instalador antigo." : null };
            }),
            Run = ctx => DeletePaths(ctx),
        },

        // ───────────── Docker, WSL e máquinas virtuais ─────────────
        new()
        {
            Id = "vhdx-compact", Category = "Docker, WSL e VMs", Title = "Compactar discos virtuais (Docker/WSL)",
            Description = "Os arquivos .vhdx crescem e nunca encolhem sozinhos. Fecha o Docker, desliga o WSL e compacta cada disco. Nada dentro deles é apagado.",
            RequiresAdmin = true, Selectable = true,
            Measure = (_, ct) => Task.Run(() =>
            {
                var items = FindVhdx().Select(p => new CleanupItem { Path = p, Label = DescribeVhdx(p), Size = Fs.SafeLength(p) }).ToList();
                if (items.Count == 0) return Measurement.NotAvailable("Nenhum disco do Docker/WSL encontrado.");
                return new Measurement
                {
                    Items = items, Unknown = true,
                    Note = $"{items.Count} disco(s) somando {Br.Size(items.Sum(i => i.Size))}. O ganho depende do espaço livre dentro deles.",
                };
            }),
            Run = CompactVhdx,
        },
        new()
        {
            Id = "hyperv-orphans", Category = "Docker, WSL e VMs", Title = "Discos de máquinas virtuais sem VM", Risk = "atencao",
            Description = "Discos .vhdx do Hyper-V (Criação Rápida, ambientes MSIX, Ubuntu…) que nenhuma VM registrada usa. Apagar remove o conteúdo dessas máquinas para sempre.",
            RequiresAdmin = true, Selectable = true,
            Measure = (_, ct) => MeasureHyperVOrphans(ct),
            Run = ctx => DeletePaths(ctx),
        },
    };
    // ───────────── pastas protegidas ─────────────

    private const string DellSnapshots = @"C:\ProgramData\Dell\SARemediation\SystemRepair\Snapshots";

    /// <summary>
    /// Tamanho de uma pasta que nem o administrador consegue listar normalmente. O robocopy /L só simula a cópia
    /// e usa o privilégio de backup, então enxerga o conteúdo sem alterar nada.
    /// </summary>
    private static async Task<long> RobocopySizeAsync(string path, CancellationToken ct)
    {
        var dummy = Path.Combine(Path.GetTempPath(), "faxina-robocopy-dummy");
        var (_, output) = await Proc.CaptureAsync("robocopy.exe",
            $"\"{path}\" \"{dummy}\" /L /S /B /NJH /BYTES /NFL /NDL /NC /XJ /R:0 /W:0", Proc.Oem, ct);
        var m = Regex.Match(output, @"^\s*Bytes\s*:\s*(\d+)", RegexOptions.Multiline);
        return m.Success ? long.Parse(m.Groups[1].Value) : 0;
    }

    private static async Task TakeOwnership(RunContext ctx, string path)
    {
        var yes = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? "S" : "Y";
        ctx.Log($"Assumindo a posse de {path}…");
        await Proc.RunAsync("takeown.exe", $"/F \"{path}\" /R /A /D {yes}", null, ctx.Ct, Proc.Oem);
        await Proc.RunAsync("icacls.exe", $"\"{path}\" /grant *S-1-5-32-544:F /T /C /Q", null, ctx.Ct, Proc.Oem);
    }

    // ───────────── Hyper-V ─────────────

    private static async Task<Measurement> MeasureHyperVOrphans(CancellationToken ct)
    {
        var folders = new[]
        {
            @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks",
            @"C:\Users\Public\Documents\Hyper-V\Virtual hard disks",
        }.Where(Directory.Exists).ToList();
        if (folders.Count == 0) return Measurement.NotAvailable("Nenhum disco de máquina virtual encontrado.");
        if (!Elevation.IsAdmin) return new Measurement { Unknown = true, Note = "A pasta do Hyper-V é protegida. Reabra como administrador para medir." };

        var disks = folders.SelectMany(f =>
        {
            try { return new DirectoryInfo(f).EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true }); }
            catch { return []; }
        }).Where(f => f.Extension.ToLowerInvariant() is ".vhdx" or ".vhd" or ".avhdx" or ".avhd").ToList();
        if (disks.Count == 0) return Measurement.NotAvailable("Nenhum disco de máquina virtual encontrado.");

        // Discos em uso pelas VMs registradas, incluindo os "pais" dos discos de diferenciação (checkpoints).
        const string script =
            "if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) { 'NOHYPERV'; exit } " +
            "Get-VM | Get-VMHardDiskDrive | ForEach-Object { $p = $_.Path; while ($p) { $p; try { $p = (Get-VHD -Path $p -ErrorAction Stop).ParentPath } catch { $p = $null } } }; 'OK'";
        var (_, output) = await Proc.CaptureAsync("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + Proc.Quote(script), Proc.Oem, ct);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verified = lines.Contains("OK");
        var used = new HashSet<string>(lines.Where(l => l.Contains('\\')), StringComparer.OrdinalIgnoreCase);

        var orphans = disks.Where(d => !used.Contains(d.FullName)).OrderByDescending(d => d.Length).ToList();
        var inUse = disks.Count - orphans.Count;
        if (orphans.Count == 0) return Measurement.NotAvailable($"Todos os {disks.Count} disco(s) estão em uso por VMs.");

        var items = orphans.Select(d => new CleanupItem
        {
            Path = d.FullName, Label = Path.GetFileNameWithoutExtension(d.Name), Size = d.Length, LastActivity = d.LastWriteTime,
            // Sem conseguir consultar o Hyper-V, não dá para garantir que estão órfãos: nada vem marcado.
            Selected = verified,
        }).ToList();
        return new Measurement
        {
            Items = items,
            Bytes = items.Where(i => i.Selected).Sum(i => i.Size),
            Note = verified
                ? $"{orphans.Count} disco(s) que nenhuma VM usa" + (inUse > 0 ? $"; {inUse} em uso por VMs não aparecem aqui." : ".")
                : "Não consegui consultar o Hyper-V. Confira no Gerenciador do Hyper-V antes de marcar algum disco.",
        };
    }

    // ───────────── VHDX ─────────────

    private static List<string> FindVhdx()
    {
        var list = new List<string>();
        void Scan(string root, int depth)
        {
            if (!Directory.Exists(root)) return;
            try
            {
                list.AddRange(new DirectoryInfo(root).EnumerateFiles("*.vhdx", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = depth })
                    .Select(f => f.FullName));
            }
            catch { }
        }
        Scan(Path.Combine(Local, @"Docker\wsl"), 3);
        Scan(Path.Combine(Local, "wsl"), 2);
        foreach (var pkg in SafeDirs(Path.Combine(Local, "Packages")))
        {
            var ext4 = Path.Combine(pkg, @"LocalState\ext4.vhdx");
            if (File.Exists(ext4)) list.Add(ext4);
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string DescribeVhdx(string path)
    {
        if (path.Contains(@"\Docker\", StringComparison.OrdinalIgnoreCase))
            return path.Contains("docker_data", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\data\", StringComparison.OrdinalIgnoreCase)
                ? "Docker · imagens, containers e volumes" : "Docker · sistema";
        var m = Regex.Match(path, @"Packages\\([^\\_]+)");
        return m.Success ? "WSL · " + m.Groups[1].Value.Replace("CanonicalGroupLimited.", "") : "WSL · " + Path.GetFileName(Path.GetDirectoryName(path));
    }

    private static async Task CompactVhdx(RunContext ctx)
    {
        var dockerExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Docker\Docker\Docker Desktop.exe");
        var dockerProcs = Process.GetProcesses()
            .Where(p => p.ProcessName.Equals("Docker Desktop", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("com.docker", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var dockerWasRunning = dockerProcs.Any(p => p.ProcessName.Equals("Docker Desktop", StringComparison.OrdinalIgnoreCase));
        if (dockerProcs.Count > 0)
        {
            ctx.Log("Fechando o Docker Desktop…");
            foreach (var p in dockerProcs) { try { p.Kill(true); } catch { } }
        }

        ctx.Log("Desligando o WSL (wsl --shutdown)…");
        await Proc.RunAsync("wsl.exe", "--shutdown", ctx.Log, ctx.Ct, Encoding.Unicode);
        await Task.Delay(TimeSpan.FromSeconds(5), ctx.Ct);

        foreach (var item in ctx.Items)
        {
            var before = Fs.SafeLength(item.Path);
            ctx.Log($"Compactando {item.Label ?? item.Path} ({Br.Size(before)})…");
            var script = $"if (Get-Command Optimize-VHD -ErrorAction SilentlyContinue) {{ Optimize-VHD -Path '{item.Path.Replace("'", "''")}' -Mode Full; exit $LASTEXITCODE }} else {{ exit 99 }}";
            var code = await Proc.PowerShellAsync(script, ctx.Log, ctx.Ct);
            if (code == 99)
            {
                ctx.Log("  Optimize-VHD indisponível; usando diskpart.");
                var scriptFile = Path.Combine(Path.GetTempPath(), $"faxina-compact-{Guid.NewGuid():N}.txt");
                await File.WriteAllTextAsync(scriptFile,
                    $"select vdisk file=\"{item.Path}\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\nexit\r\n", ctx.Ct);
                try { await Proc.RunAsync("diskpart.exe", $"/s \"{scriptFile}\"", ctx.Log, ctx.Ct, Proc.Oem); }
                finally { try { File.Delete(scriptFile); } catch { } }
            }
            var after = Fs.SafeLength(item.Path);
            ctx.Log($"  {Br.Size(before)} → {Br.Size(after)} (liberou {Br.Size(Math.Max(0, before - after))})");
        }

        if (dockerWasRunning && File.Exists(dockerExe))
        {
            ctx.Log("Reabrindo o Docker Desktop…");
            // Via explorer.exe para o Docker abrir com o usuário normal, e não elevado como este app.
            try { Process.Start("explorer.exe", $"\"{dockerExe}\""); } catch { }
        }
    }
}
