using System.Runtime.InteropServices;
using Faxina.Core;

namespace Faxina.Windows;

/// <summary>Troca a senha do usuário atual pela mesma API do "Alterar uma senha" do Ctrl+Alt+Del.</summary>
public static class WindowsAccount
{
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserChangePassword(string? domainName, string userName, string oldPassword, string newPassword);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? serverName, string userName, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_24
    {
        [MarshalAs(UnmanagedType.Bool)] public bool usri24_internet_identity;
        public uint usri24_flags;
        public string? usri24_internet_provider_name;
        public string? usri24_internet_principal_name;
        public IntPtr usri24_user_sid;
    }

    private static string User => Environment.UserName;
    private static bool IsDomain => !string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    public static AccountInfo Get()
    {
        if (IsDomain)
            return new AccountInfo($@"{Environment.UserDomainName}\{User}", "domain", true, null, null, 8);

        var email = MicrosoftAccountEmail();
        if (email != null)
            return new AccountInfo(User, "microsoft", false,
                $"Este usuário entra com a conta Microsoft {email}. A senha dela é trocada no site da Microsoft e passa a valer neste PC no próximo login.",
                "https://account.microsoft.com/security", 8);

        return new AccountInfo(User, "local", true, null, null, 8);
    }

    /// <summary>Contas locais ligadas a uma conta Microsoft têm "internet identity" (USER_INFO_24).</summary>
    private static string? MicrosoftAccountEmail()
    {
        if (NetUserGetInfo(null, User, 24, out var buf) != 0) return null;
        try
        {
            var info = Marshal.PtrToStructure<USER_INFO_24>(buf);
            return info.usri24_internet_identity ? info.usri24_internet_principal_name ?? "" : null;
        }
        finally { NetApiBufferFree(buf); }
    }

    public static Task ChangeAsync(string current, string next) => Task.Run(() =>
    {
        var account = Get();
        if (!account.CanChange) throw new PasswordException(account.Reason!);

        var code = NetUserChangePassword(IsDomain ? Environment.UserDomainName : null, User, current, next);
        switch (code)
        {
            case 0: return;
            case 86:   // ERROR_INVALID_PASSWORD
                throw new PasswordException("A senha atual está incorreta.", wrongCurrent: true);
            case 2245: // NERR_PasswordTooShort: tamanho, complexidade ou histórico
            case 1325: // ERROR_PASSWORD_RESTRICTION
                throw new PasswordException("O Windows recusou a nova senha: ela não atende à política de senhas deste computador (tamanho, complexidade ou repetição de senhas antigas).");
            case 2242: // NERR_PasswordExpired
                throw new PasswordException("Sua senha expirou. Troque-a pela tela de login do Windows.");
            case 1909: // ERROR_ACCOUNT_LOCKED_OUT
                throw new PasswordException("A conta está bloqueada por excesso de tentativas. Aguarde ou fale com o administrador.");
            case 5:    // ERROR_ACCESS_DENIED
                throw new PasswordException("O Windows não permitiu trocar a senha deste usuário.");
            case 1355: case 53: case 2453: // domínio inacessível
                throw new PasswordException("Não consegui falar com o servidor do domínio. Conecte-se à rede da empresa (ou VPN) e tente de novo.");
            default:
                throw new PasswordException($"O Windows não conseguiu trocar a senha (código {code}).");
        }
    });
}
