# Faxina

App para descobrir o que ocupa o disco e limpar com segurança, no **Windows** e no **Linux**.

- **Visão geral** — uso do disco, composição por pasta e o espaço que costuma passar despercebido (no Windows: pagefile, hibernação, pontos de restauração, Lixeira, OneDrive só na nuvem; no Linux: swap, journald, Snaps, Flatpak, Lixeira, reserva do ext4).
- **Explorar disco** — treemap navegável de qualquer disco ou pasta, tipos de arquivo e pastas de desenvolvimento (node_modules, bin/obj…).
- **Maiores arquivos** — os 300 maiores, filtráveis por tipo, com abrir/Lixeira/excluir.
- **Limpeza** — limpezas medidas antes de rodar. Windows: Temp, Windows Update, DISM, hibernação, pontos de restauração, discos de VM sem uso, snapshots da Dell, logs do McAfee… Linux: cache do apt/dnf/pacman, pacotes órfãos e kernels antigos, journald, logs rotacionados, Snaps antigos, runtimes Flatpak, ~/.cache… Nos dois: npm, Yarn, pnpm, NuGet, Gradle, pip, Cypress, node_modules parados, Docker, VS Code/Cursor.
- **Programas** — o que está instalado (Windows: Win32 + Store; Linux: apt/rpm, Snap, Flatpak), por tamanho, com recomendações e botão de desinstalar.
- **Conta** — troca a senha do usuário atual, sempre conferindo a senha atual. Windows: contas locais e de domínio (mesma API do Ctrl+Alt+Del); conta Microsoft é detectada e leva ao site da Microsoft, onde essa senha é trocada. Linux: exige `sudo`; confere a senha atual no `/etc/shadow` com o `crypt()` do sistema e grava com `chpasswd`. As senhas nunca vão na linha de comando nem em logs, e três senhas atuais erradas bloqueiam novas tentativas por um tempo crescente.

## Como roda em cada sistema

**Windows** — janela própria (WinForms + WebView2). Abre sem elevação; o botão **Reabrir como admin** libera as limpezas do sistema.

**Linux** — sobe um servidor local em `127.0.0.1` e abre a interface no navegador. Não depende de bibliotecas gráficas, então roda em qualquer distribuição x64 (testado no Ubuntu 20.04) e também em servidores sem tela:

```bash
./faxina                 # abre no navegador
sudo ./faxina            # libera as limpezas do sistema (apt, logs, snaps…)
./faxina --no-browser    # só mostra o endereço; num servidor, use: ssh -L 8723:127.0.0.1:8723 usuario@servidor
```

O servidor só aceita conexões locais e exige um token aleatório (vai no endereço mostrado no terminal) em toda chamada. Com `sudo`, caches e Lixeira continuam sendo os do usuário que chamou o sudo. A análise não atravessa outros sistemas de arquivos montados (`/proc`, `/mnt/c` no WSL, outro disco em `/home`); cada disco aparece separado.

## Estrutura

| Projeto | O que tem |
|---|---|
| `src/Faxina.Core` | varredura, ponte JS ↔ C# (`Bridge.cs`), limpezas comuns (`Cleanup.cs`), interface web em `wwwroot` (embutida no assembly) |
| `src/Faxina` | Windows: janela WebView2, limpezas e programas do Windows (`Windows/`) |
| `src/Faxina.Linux` | Linux: servidor HTTP local, limpezas e pacotes do Linux |

Cada sistema implementa `IPlatform` (`Faxina.Core/Platform.cs`); a interface pergunta `system.info` e se adapta (`platform: "windows" | "linux"`).

## Rodar e publicar

```bash
dotnet run --project src/Faxina
```

```bash
powershell -ExecutionPolicy Bypass -File tools/publish.ps1
```

Gera três versões, cada uma num arquivo só:

| Versão | Arquivo | Precisa no computador |
|---|---|---|
| Windows instalada | `publish/Faxina.exe` (~2 MB) | .NET 9 Desktop Runtime |
| Windows portátil (pendrive) | `publish-portatil/Faxina-portatil.exe` (~46 MB) | nada além do WebView2, que já vem no Windows 10/11 |
| Linux x64 | `publish-linux/faxina` (~37 MB) | nada (o .NET vai junto) e um navegador |

A portátil guarda o cache da interface na pasta temporária do Windows, não em `%LOCALAPPDATA%`. Se o PC não tiver WebView2, o app oferece o link de download da Microsoft.

O ícone é gerado por `tools/make-icon.ps1`.

Para mexer só na interface, sirva `src/Faxina.Core/wwwroot` num servidor estático: sem o app ela usa dados de exemplo (`mock.js`). Dentro do app a interface vem dos recursos embutidos, então mudanças nela exigem recompilar.

Diagnóstico sem interface (só mede, não apaga nada):

```bash
Faxina.exe --selftest C:\ relatorio.json
./faxina --selftest / relatorio.json
```
