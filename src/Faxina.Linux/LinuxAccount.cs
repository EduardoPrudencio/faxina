using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Faxina.Core;

namespace Faxina.Linux;

/// <summary>
/// Troca a senha do usuário (o que chamou o sudo, se for o caso). Exige root: confere a senha atual contra o
/// /etc/shadow com o crypt() do próprio sistema e grava a nova com chpasswd, passando tudo pela entrada padrão.
/// </summary>
public static class LinuxAccount
{
    private const int MinLength = 8;
    private static readonly object CryptLock = new();   // crypt() usa um buffer estático

    public static AccountInfo Get()
    {
        var user = LinuxUser.Name;
        if (!LinuxUser.IsRoot)
            return new AccountInfo(user, "linux", false,
                "Trocar a senha pelo Faxina exige root. Rode-o com sudo, ou use o comando passwd no terminal.", null, MinLength);
        if (user == "root")
            return new AccountInfo(user, "linux", false,
                "O Faxina está rodando direto como root. Use sudo a partir do seu usuário para trocar a senha dele.", null, MinLength);
        if (Proc.Which("chpasswd") == null)
            return new AccountInfo(user, "linux", false, "O comando chpasswd não existe neste sistema.", null, MinLength);
        return new AccountInfo(user, "linux", true, null, null, MinLength);
    }

    public static async Task ChangeAsync(string current, string next, CancellationToken ct)
    {
        var account = Get();
        if (!account.CanChange) throw new PasswordException(account.Reason!);
        var user = account.User;

        var hash = ShadowHash(user) ?? throw new PasswordException("Não encontrei este usuário no /etc/shadow.");
        if (hash.Length == 0 || hash.StartsWith('!') || hash.StartsWith('*'))
            throw new PasswordException("Este usuário não tem senha local ativa (conta bloqueada ou login só por chave). Use passwd no terminal.");
        if (!Verify(current, hash)) throw new PasswordException("A senha atual está incorreta.", wrongCurrent: true);

        var (code, output) = await Proc.RunWithInputAsync("chpasswd", "", $"{user}:{next}\n", ct);
        if (code != 0)
        {
            var reason = output.Contains("BAD PASSWORD", StringComparison.OrdinalIgnoreCase) || output.Contains("pam_chauthtok", StringComparison.Ordinal)
                ? "o sistema recusou a nova senha pela política de senhas"
                : $"chpasswd terminou com código {code}";
            throw new PasswordException($"Não foi possível trocar a senha: {reason}.");
        }
    }

    private static string? ShadowHash(string user)
    {
        foreach (var line in LinuxUser.ReadText("/etc/shadow").Split('\n'))
        {
            var f = line.Split(':');
            if (f.Length > 1 && f[0] == user) return f[1];
        }
        return null;
    }

    // ───── crypt() da libcrypt (suporta yescrypt, SHA-512, SHA-256… o que o sistema usar) ─────

    private delegate IntPtr CryptFn([MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string salt);
    private static readonly CryptFn? Crypt = LoadCrypt();

    private static CryptFn? LoadCrypt()
    {
        foreach (var lib in new[] { "libcrypt.so.1", "libcrypt.so.2", "libcrypt.so" })
        {
            if (NativeLibrary.TryLoad(lib, out var handle) && NativeLibrary.TryGetExport(handle, "crypt", out var fn))
                return Marshal.GetDelegateForFunctionPointer<CryptFn>(fn);
        }
        return null;
    }

    private static bool Verify(string password, string hash)
    {
        if (Crypt == null) throw new PasswordException("Não encontrei a libcrypt para conferir a senha atual.");
        string? computed;
        lock (CryptLock)
        {
            var ptr = Crypt(password, hash);
            computed = ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
        }
        if (computed == null || computed.StartsWith('*')) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(computed), Encoding.UTF8.GetBytes(hash));
    }
}
