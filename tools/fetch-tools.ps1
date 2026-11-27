<#
.SYNOPSIS
    Fetches the third-party tools Aetherium Forge needs into tools\ and verifies them.

.DESCRIPTION
    Downloads texconv.exe (DirectXTex, MIT) from the pinned official GitHub release and checks its SHA-256.
    With -From, copies an existing texconv.exe instead of downloading; the hash is still verified, so only the
    exact pinned official build is accepted.

.PARAMETER From
    Path to an existing texconv.exe to copy instead of downloading.

.PARAMETER Force
    Replace tools\texconv.exe even if it already matches.

.EXAMPLE
    .\tools\fetch-tools.ps1

.EXAMPLE
    .\tools\fetch-tools.ps1 -From 'C:\SomeFolder\texconv.exe'
#>
[CmdletBinding()]
param(
    [string] $From,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Pinned release: DirectXTex "may2026" (texconv 2026.5.8.1). Update tag, URL and hash together.
$TexconvTag = 'may2026'
$TexconvUrl = "https://github.com/microsoft/DirectXTex/releases/download/$TexconvTag/texconv.exe"
$TexconvSha256 = 'DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06'

$toolsDir = $PSScriptRoot
$target = Join-Path $toolsDir 'texconv.exe'

function Test-Hash([string] $path) {
    (Get-FileHash -Path $path -Algorithm SHA256).Hash -eq $TexconvSha256
}

if ((Test-Path $target) -and -not $Force -and (Test-Hash $target)) {
    Write-Host "texconv.exe ($TexconvTag) already present and verified."
    return
}

$temp = Join-Path ([System.IO.Path]::GetTempPath()) "texconv-$([guid]::NewGuid().ToString('N')).exe"
try {
    if ($From) {
        Copy-Item -LiteralPath $From -Destination $temp
    }
    else {
        Write-Host "Downloading texconv.exe ($TexconvTag) from $TexconvUrl"
        Invoke-WebRequest -Uri $TexconvUrl -OutFile $temp -UseBasicParsing
    }

    if (-not (Test-Hash $temp)) {
        throw "texconv.exe hash mismatch. Expected $TexconvSha256 (DirectXTex $TexconvTag). Refusing to install."
    }

    Move-Item -LiteralPath $temp -Destination $target -Force
    Write-Host "Installed $target"
}
finally {
    if (Test-Path $temp) {
        Remove-Item -LiteralPath $temp -Force
    }
}
