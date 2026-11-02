using Faxina.Core;
using Faxina.Linux;

// Faxina para Linux: sobe a interface num servidor local e abre o navegador.
//   faxina                 abre a interface
//   sudo faxina            idem, liberando as limpezas do sistema
//   faxina --no-browser    só mostra o endereço (para usar por túnel SSH)
//   faxina --port 8723     escolhe a porta
//   faxina --selftest / saida.json   mede tudo sem apagar nada

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        Faxina — descubra o que ocupa o disco e limpe com segurança.

        Uso: faxina [--no-browser] [--port N]
             sudo faxina            libera as limpezas do sistema (apt, logs, snaps…)
             faxina --selftest <pasta> <saida.json>   diagnóstico sem interface (não apaga nada)

        Em um servidor sem tela, rode "faxina --no-browser" e, no seu computador:
             ssh -L 8723:127.0.0.1:8723 usuario@servidor
        depois abra o endereço mostrado.
        """);
    return 0;
}

var platform = new LinuxPlatform();

if (args.Length >= 3 && args[0] == "--selftest")
{
    await SelfTest.RunAsync(platform, args[1], args[2]);
    return 0;
}

var port = 8723;
var portArg = Array.IndexOf(args, "--port");
if (portArg >= 0 && portArg + 1 < args.Length && int.TryParse(args[portArg + 1], out var p)) port = p;

var server = new WebServer(port);
var bridge = new Bridge(platform, server);

Console.WriteLine();
Console.WriteLine($"  Faxina {Assets.Version} — {LinuxUser.Name}{(LinuxUser.IsRoot ? " (root)" : "")}");
Console.WriteLine();
Console.WriteLine($"  Abra no navegador:  {server.Url}");
Console.WriteLine();
if (!LinuxUser.IsRoot) Console.WriteLine("  Dica: rode com sudo para liberar as limpezas do sistema.");
Console.WriteLine("  Ctrl+C encerra.");
Console.WriteLine();

var connected = false;
server.ClientConnected += () => { if (!connected) { connected = true; Console.WriteLine("  Interface conectada."); } };
Console.CancelKeyPress += (_, e) => { e.Cancel = true; server.Stop(); };

if (!args.Contains("--no-browser")) LinuxUser.Open(server.Url);
await server.RunAsync(bridge);
Console.WriteLine("  Até mais!");
return 0;
