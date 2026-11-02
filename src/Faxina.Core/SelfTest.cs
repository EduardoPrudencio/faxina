using System.Text.Json;

namespace Faxina.Core;

/// <summary>
/// Diagnóstico sem interface: mede tudo (sem apagar nada) e grava um JSON.
/// Uso: Faxina --selftest &lt;pasta para varrer&gt; &lt;arquivo de saída&gt;
/// </summary>
public static class SelfTest
{
    public static async Task RunAsync(IPlatform platform, string scanPath, string outFile)
    {
        var report = new Dictionary<string, object?>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        report["system"] = platform.GetSystemInfo();

        var scanner = new DiskScanner(CancellationToken.None, platform.ScanExcludes(scanPath));
        var root = scanner.Scan(scanPath);
        report["scan"] = new
        {
            seconds = sw.Elapsed.TotalSeconds, root.Size, root.FileCount, root.DirCount, scanner.CloudBytes, scanner.Inaccessible,
            top = root.Children.Take(12).Select(c => new { c.Name, c.Size, c.IsMount, c.IsLink }),
            topFiles = scanner.TopFiles().Take(5),
            heavy = scanner.HeavyFolders(),
        };

        var measures = new Dictionary<string, object>();
        foreach (var t in platform.Cleanups)
        {
            try
            {
                var m = await t.Measure(null, CancellationToken.None);
                measures[t.Id] = new { m.Bytes, m.Available, m.Unknown, m.Note, items = m.Items?.Count };
            }
            catch (Exception ex) { measures[t.Id] = new { error = ex.Message }; }
        }
        report["cleanup"] = measures;

        var apps = await platform.ListAppsAsync(CancellationToken.None);
        report["apps"] = new
        {
            count = apps.Count,
            bySource = apps.GroupBy(a => a.Source).ToDictionary(g => g.Key, g => g.Count()),
            biggest = apps.Take(8).Select(a => new { a.Name, a.Source, a.Size, a.Verdict }),
            flagged = apps.Where(a => a.Verdict != "").Select(a => new { a.Name, a.Verdict, a.Size }),
        };
        report["totalSeconds"] = sw.Elapsed.TotalSeconds;

        await File.WriteAllTextAsync(outFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
    }
}
