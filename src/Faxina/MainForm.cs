using System.Text.Json;
using Faxina.Core;
using Faxina.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Faxina;

/// <summary>Janela única: hospeda a interface web (embutida no Faxina.Core) num WebView2 e liga a ponte JS ↔ C#.</summary>
public sealed class MainForm : Form, IHost
{
#if PORTABLE
    public const bool Portable = true;
#else
    public const bool Portable = false;
#endif

    private const string Origin = "https://faxina.local/";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(246, 247, 251) };
    private Bridge? _bridge;

    public MainForm()
    {
        Text = "Faxina" + (Portable ? " (portátil)" : "") + (Elevation.IsAdmin ? " — Administrador" : "");
        Width = 1360;
        Height = 880;
        MinimumSize = new Size(980, 640);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    /// <summary>
    /// Pasta de dados do WebView2. Na versão instalada fica em %LOCALAPPDATA%; na portátil, na pasta temporária
    /// do Windows, para não deixar nada permanente no PC onde o pendrive foi usado.
    /// </summary>
    private static string DataDir()
    {
        var sub = Elevation.IsAdmin ? "WebView2-admin" : "WebView2";
        return Portable
            ? Path.Combine(Path.GetTempPath(), "Faxina-portatil", sub)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Faxina", sub);
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, DataDir());
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;

            // A interface vem dos recursos embutidos no .exe: o app é um arquivo só.
            core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) => e.Response = ServeAsset(env, e.Request.Uri);

            _bridge = new Bridge(new WindowsPlatform(), this);
            core.WebMessageReceived += async (_, e) => Post(await _bridge.InvokeAsync(e.WebMessageAsJson));
            core.Navigate(Origin + "index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var open = MessageBox.Show(this,
                "Este computador não tem o Microsoft Edge WebView2, necessário para a interface do Faxina.\n\n" +
                "Deseja abrir a página de download da Microsoft? (instalação gratuita, cerca de 2 minutos)",
                "Faxina", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (open == DialogResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://go.microsoft.com/fwlink/p/?LinkId=2124703") { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível iniciar a interface (WebView2).\n\n" + ex.Message,
                "Faxina", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private static CoreWebView2WebResourceResponse ServeAsset(CoreWebView2Environment env, string uri)
    {
        var path = Uri.UnescapeDataString(new Uri(uri).AbsolutePath.TrimStart('/'));
        var stream = Assets.Open(path);
        if (stream == null) return env.CreateWebResourceResponse(null, 404, "Not Found", "");
        return env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {Assets.ContentType(path)}\r\nCache-Control: no-store");
    }

    // ───── IHost ─────

    public void Emit(object message) => Post(message);

    public Task<string?> PickFolderAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        BeginInvoke(() =>
        {
            using var dlg = new FolderBrowserDialog { Description = "Escolha a pasta para analisar", UseDescriptionForTitle = true };
            tcs.SetResult(dlg.ShowDialog(this) == DialogResult.OK ? dlg.SelectedPath : null);
        });
        return tcs.Task;
    }

    public void Exit() => BeginInvoke(() => Application.Exit());

    /// <summary>Envia JSON para a página; pode ser chamado de qualquer thread.</summary>
    private void Post(object message)
    {
        var json = JsonSerializer.Serialize(message, Bridge.Json);
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                if (_web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json);
            });
        }
        catch (InvalidOperationException) { /* janela fechando */ }
    }
}
