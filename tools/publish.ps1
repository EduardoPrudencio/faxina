# Publica as três versões do Faxina:
#   publish\Faxina.exe                    — Windows, usa o .NET 9 instalado no PC (~2 MB)
#   publish-portatil\Faxina-portatil.exe  — Windows, traz o .NET junto; roda de um pendrive em qualquer Windows 10/11
#   publish-linux\faxina                  — Linux x64, traz o .NET junto; roda em qualquer distribuição (glibc)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$common = @('-c', 'Release', '-p:PublishSingleFile=true', '-p:DebugType=none', '-p:GenerateDocumentationFile=false', '-v', 'q', '-nologo')

function Publish($project, $out, [string[]]$extra) {
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish (Join-Path $root "src\$project") @common @extra -o $out
    if ($LASTEXITCODE -ne 0) { throw "falha ao publicar $out" }
    # Documentação XML que o pacote do WebView2 copia junto: não serve para nada em produção
    Get-ChildItem $out -Filter *.xml | Remove-Item
}

$win = @('-r', 'win-x64', '-p:IncludeNativeLibrariesForSelfExtract=true')
Publish 'Faxina' (Join-Path $root 'publish') ($win + @('--self-contained', 'false'))
Publish 'Faxina' (Join-Path $root 'publish-portatil') ($win + @('--self-contained', 'true', '-p:Portable=true', '-p:EnableCompressionInSingleFile=true'))
Publish 'Faxina.Linux' (Join-Path $root 'publish-linux') @('-r', 'linux-x64', '--self-contained', 'true', '-p:EnableCompressionInSingleFile=true')

Get-ChildItem (Join-Path $root 'publish'), (Join-Path $root 'publish-portatil'), (Join-Path $root 'publish-linux') -File |
    Select-Object @{n = 'Arquivo'; e = { $_.FullName } }, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
