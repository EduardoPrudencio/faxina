using System.Text;
using Faxina.Core;
using Faxina.Windows;

namespace Faxina;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Saída de vssadmin/powercfg/diskpart vem na code page OEM (850 em pt-BR).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Diagnóstico sem janela (só mede, não apaga nada): Faxina.exe --selftest <pasta> <saida.json>
        if (args.Length >= 3 && args[0] == "--selftest")
        {
            SelfTest.RunAsync(new WindowsPlatform(), args[1], args[2]).GetAwaiter().GetResult();
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
