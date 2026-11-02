using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Faxina.Core;

namespace Faxina.Linux;

/// <summary>
/// Servidor HTTP local que serve a interface e a ponte. Escuta só em 127.0.0.1 e exige um token aleatório
/// em toda chamada de API, então nem outros usuários da máquina nem sites abertos no navegador conseguem usá-lo.
/// Respostas vão em POST /api/call; progresso e log chegam por Server-Sent Events em /api/events.
/// </summary>
public sealed class WebServer : IHost
{
    private readonly HttpListener _listener = new();
    private readonly List<Channel<string>> _clients = new();
    private readonly CancellationTokenSource _stop = new();
    private Bridge? _bridge;

    public int Port { get; }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public string Url => $"http://127.0.0.1:{Port}/?t={Token}";
    public event Action? ClientConnected;

    public WebServer(int preferredPort)
    {
        foreach (var port in new[] { preferredPort, FreePort() })
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Port = port;
                return;
            }
            catch (HttpListenerException) { }
        }
        throw new InvalidOperationException("Não consegui abrir uma porta local para a interface.");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public async Task RunAsync(Bridge bridge)
    {
        _bridge = bridge;
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync().WaitAsync(_stop.Token); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    public void Stop()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            // Proteção contra DNS rebinding: só aceita os nomes locais desta porta.
            var host = req.Headers["Host"] ?? "";
            if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}") { await Fail(res, 403); return; }

            var path = req.Url!.AbsolutePath;
            if (path.StartsWith("/api/", StringComparison.Ordinal))
            {
                var origin = req.Headers["Origin"];
                if (origin != null && origin != $"http://127.0.0.1:{Port}" && origin != $"http://localhost:{Port}") { await Fail(res, 403); return; }
                if (!ValidToken(req.Headers["X-Faxina-Token"] ?? req.QueryString["t"])) { await Fail(res, 401); return; }

                if (path == "/api/call" && req.HttpMethod == "POST") await CallAsync(req, res);
                else if (path == "/api/events") await EventsAsync(res);
                else await Fail(res, 404);
                return;
            }

            var file = path == "/" ? "index.html" : path.TrimStart('/');
            await using var stream = Assets.Open(file);
            if (stream == null) { await Fail(res, 404); return; }
            res.ContentType = Assets.ContentType(file);
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            await stream.CopyToAsync(res.OutputStream);
            res.Close();
        }
        catch
        {
            try { res.Abort(); } catch { }
        }
    }

    private bool ValidToken(string? given) =>
        given != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(given), Encoding.ASCII.GetBytes(Token));

    private async Task CallAsync(HttpListenerRequest req, HttpListenerResponse res)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var result = await _bridge!.InvokeAsync(body);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Bridge.Json);
        res.ContentType = "application/json; charset=utf-8";
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private async Task EventsAsync(HttpListenerResponse res)
    {
        res.ContentType = "text/event-stream; charset=utf-8";
        res.Headers["Cache-Control"] = "no-cache";
        res.SendChunked = true;
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(2000) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_clients) _clients.Add(channel);
        ClientConnected?.Invoke();
        try
        {
            await Write(res, ": conectado\n\n");
            while (!_stop.IsCancellationRequested)
            {
                using var ping = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                ping.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var msg = await channel.Reader.ReadAsync(ping.Token);
                    await Write(res, $"data: {msg}\n\n");
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    await Write(res, ": ping\n\n"); // mantém a conexão viva e detecta aba fechada
                }
            }
        }
        catch { /* aba fechada */ }
        finally
        {
            lock (_clients) _clients.Remove(channel);
            try { res.Close(); } catch { }
        }
    }

    private static async Task Write(HttpListenerResponse res, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await res.OutputStream.WriteAsync(bytes);
        await res.OutputStream.FlushAsync();
    }

    private static async Task Fail(HttpListenerResponse res, int status)
    {
        res.StatusCode = status;
        await res.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(status.ToString()));
        res.Close();
    }

    // ───── IHost ─────

    public void Emit(object message)
    {
        var json = JsonSerializer.Serialize(message, Bridge.Json);
        lock (_clients) foreach (var c in _clients) c.Writer.TryWrite(json);
    }

    /// <summary>Não há janela nativa para escolher pasta; a interface pede o caminho digitado.</summary>
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

    public void Exit() => Stop();
}
