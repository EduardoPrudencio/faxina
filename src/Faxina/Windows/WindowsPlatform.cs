using System.ComponentModel;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Faxina.Core;
using Microsoft.VisualBasic.FileIO;

namespace Faxina.Windows;

public sealed class WindowsPlatform : IPlatform
{
    public string Name => "windows";
    public bool IsAdmin => Elevation.IsAdmin;
    public string SystemRoot { get; } = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
    public IReadOnlyList<CleanupTarget> Cleanups => WindowsCleanups.All;

    public object GetSystemInfo() => WindowsSystem.Get();
    public List<string> DefaultProjectRoots() => WindowsCleanups.DefaultProjectRoots();
    public Task<List<InstalledApp>> ListAppsAsync(CancellationToken ct) => WindowsApps.ListAsync(ct);
    public Task<int> UninstallAsync(string id, Action<string> log, CancellationToken ct) => WindowsApps.UninstallAsync(id, log, ct);

    // Junções e symlinks já são pulados pela varredura; no Windows não há outros pontos de montagem a excluir.
    public IReadOnlySet<string> ScanExcludes(string root) => new HashSet<string>();

    public string? WhyProtected(string path)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.StartsWith(win, StringComparison.OrdinalIgnoreCase)
            ? "Por segurança, o app não apaga nada dentro da pasta do Windows por aqui. Use a aba Limpeza."
            : null;
    }

    public void MoveToTrash(string path)
    {
        if (File.Exists(path)) FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    public void Reveal(string path) =>
        Process.Start("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"");

    public bool RelaunchAsAdmin() => Elevation.RelaunchAsAdmin();

    public AccountInfo GetAccount() => WindowsAccount.Get();
    public Task ChangePasswordAsync(string current, string next, CancellationToken ct) => WindowsAccount.ChangeAsync(current, next);

    public void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}

public static class Elevation
{
    public static bool IsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Reabre o app elevado (UAC). Retorna false se o usuário cancelar.</summary>
    public static bool RelaunchAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception) { return false; }
    }
}

/// <summary>Dados que a varredura de pastas não enxerga: paginação, hibernação, pontos de restauração e Lixeira.</summary>
public static class WindowsSystem
{
    public static object Get()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d => new
            {
                name = d.Name,
                label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Disco local" : d.VolumeLabel,
                format = d.DriveFormat,
                total = d.TotalSize,
                free = d.AvailableFreeSpace,
            }).ToList();

        var sysDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var hidden = new List<HiddenItem>();
        try
        {
            foreach (var f in new DirectoryInfo(sysDrive).EnumerateFiles("*.sys", new System.IO.EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                switch (f.Name.ToLowerInvariant())
                {
                    case "pagefile.sys": hidden.Add(new("memory", "Memória virtual (pagefile.sys)", "Gerenciado pelo Windows; não apague", f.Length)); break;
                    case "hiberfil.sys": hidden.Add(new("moon", "Hibernação (hiberfil.sys)", "Pode ser apagado desativando a hibernação", f.Length)); break;
                    case "swapfile.sys": hidden.Add(new("memory", "Swap de apps (swapfile.sys)", "Gerenciado pelo Windows", f.Length)); break;
                }
            }
        }
        catch { }

        var shadow = GetShadowStorage(sysDrive);
        hidden.Add(shadow != null
            ? new("history", "Pontos de restauração", $"{shadow.Count} ponto(s) · limite de {Br.Size(shadow.Max)}", shadow.Used)
            : new("history", "Pontos de restauração", "Reabra como administrador para medir", null));
        var (rbBytes, rbItems) = Native.QueryRecycleBin();
        hidden.Add(new("trash", "Lixeira", $"{Br.Count(rbItems)} item(ns)", rbBytes));

        return new
        {
            platform = "windows",
            isAdmin = Elevation.IsAdmin,
            canRelaunch = true,
            canPickFolder = true,
            adminHint = (string?)null,
            machine = Environment.MachineName,
            user = Environment.UserName,
            os = Environment.OSVersion.VersionString,
            systemDrive = sysDrive,
            drives,
            hidden,
            shadow,
            rootFilesHint = "pagefile.sys, hiberfil.sys e swapfile.sys",
        };
    }

    /// <summary>Espaço usado pelos pontos de restauração (Win32_ShadowStorage). Exige administrador.</summary>
    public static ShadowInfo? GetShadowStorage(string drive)
    {
        if (!Elevation.IsAdmin) return null;
        try
        {
            var letter = drive.TrimEnd('\\');
            using var volumes = new ManagementObjectSearcher("SELECT DeviceID, DriveLetter FROM Win32_Volume");
            string? deviceId = volumes.Get().Cast<ManagementObject>()
                .FirstOrDefault(v => string.Equals(v["DriveLetter"] as string, letter, StringComparison.OrdinalIgnoreCase))?["DeviceID"] as string;

            using var searcher = new ManagementObjectSearcher("SELECT AllocatedSpace, MaxSpace, UsedSpace, Volume FROM Win32_ShadowStorage");
            long used = 0, allocated = 0, max = 0;
            foreach (ManagementObject s in searcher.Get())
            {
                var vol = s["Volume"] as string ?? "";
                if (deviceId != null && !vol.Contains(deviceId.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase)
                    && !vol.Contains(deviceId, StringComparison.OrdinalIgnoreCase)) continue;
                used += Convert.ToInt64(s["UsedSpace"]);
                allocated += Convert.ToInt64(s["AllocatedSpace"]);
                max += Convert.ToInt64(s["MaxSpace"]);
            }

            using var shadows = new ManagementObjectSearcher("SELECT ID FROM Win32_ShadowCopy");
            var count = shadows.Get().Count;
            return new ShadowInfo(used, allocated, max, count);
        }
        catch { return null; }
    }

    public sealed record ShadowInfo(long Used, long Allocated, long Max, int Count);
}

public static class Native
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    public static (long bytes, long items) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    public static bool EmptyRecycleBin()
    {
        const uint NoConfirmation = 0x1, NoProgressUi = 0x2, NoSound = 0x4;
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);
        return hr == 0 || hr == unchecked((int)0x8000FFFF); // E_UNEXPECTED = lixeira já vazia
    }
}
