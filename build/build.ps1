#requires -Version 5.1
<#
.SYNOPSIS
    Compile et teste LDI12 Diagnostic.

.DESCRIPTION
    La solution DOIT être compilée avec MSBuild 17 ou supérieur, pas avec « dotnet build ».
    Raison : LDI12.App est une application WPF ciblant .NET Framework 4.6.2, et le compilateur
    de balisage XAML (Microsoft.WinFx.targets) n'existe que dans le MSBuild complet livré avec
    Visual Studio. Le SDK dotnet compile les bibliothèques sans erreur, mais produit un projet
    WPF sans les champs générés depuis le XAML.

    MSBuild 16 (VS 2019) ne convient pas non plus : il ne sait pas résoudre un SDK .NET 7.

.EXAMPLE
    .\build\build.ps1
    .\build\build.ps1 -Configuration Release -SkipTests
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'LDI12.Diagnostic.sln'

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild `
                            -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null
        foreach ($candidate in $found) {
            if ((& $candidate -version -nologo) -match '^(\d+)\.' -and [int]$Matches[1] -ge 17) { return $candidate }
        }
    }

    # vswhere ne remonte pas toujours une installation Visual Studio complète : on cherche aussi
    # dans les emplacements standard.
    $patterns = @(
        'C:\Program Files\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2026\*\MSBuild\Current\Bin\MSBuild.exe'
    )
    foreach ($pattern in $patterns) {
        $candidate = Get-Item $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw @'
MSBuild 17 ou supérieur est introuvable.
Installer « Build Tools pour Visual Studio 2022 » avec la charge de travail
« Développement .NET desktop » : c'est le seul composant capable de compiler
une application WPF ciblant .NET Framework 4.6.2.
'@
}

$msbuild = Find-MSBuild
Write-Host "MSBuild  : $msbuild"
Write-Host "Solution : $solution"
Write-Host "Config   : $Configuration"
Write-Host ''

& $msbuild $solution -t:Restore,Build -p:Configuration=$Configuration -v:m -nologo
if ($LASTEXITCODE -ne 0) { throw "Échec de la compilation (code $LASTEXITCODE)." }

if (-not $SkipTests) {
    Write-Host ''
    & dotnet test $solution --no-build --configuration $Configuration -v q
    if ($LASTEXITCODE -ne 0) { throw "Échec des tests (code $LASTEXITCODE)." }
}

Write-Host ''
Write-Host 'Terminé.' -ForegroundColor Green
