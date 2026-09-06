#requires -Version 5.1
<#
.SYNOPSIS
    Produit l'exécutable unique qui part sur la clé USB de l'atelier.

.DESCRIPTION
    La compilation ordinaire produit un exécutable accompagné de cent treize bibliothèques.
    C'est parfait pour développer et inutilisable pour intervenir : un dossier pareil se copie à
    moitié, une DLL manque, et le logiciel se plante chez le client sur un message que personne
    ne peut lire.

    Ce script rassemble ces bibliothèques dans l'exécutable lui-même, en trois passes :

      1. compilation ordinaire en Release ;
      2. l'hôte de sondes embarque ses propres dépendances : c'est un second processus, il ne
         peut pas emprunter le résolveur de l'interface ;
      3. l'interface embarque les siennes, plus l'hôte de sondes ainsi rendu autonome.

    Le résultat est un fichier unique dans artifacts\, avec son empreinte SHA-256 : c'est elle
    qui permettra de vérifier, six mois plus tard, que la clé USB porte bien ce qui a été publié.

    Signature : voir -CertificateThumbprint. Sans certificat, l'exécutable part non signé et
    SmartScreen annonce « Éditeur inconnu » à chaque téléchargement. C'est un achat, pas un
    développement : l'emplacement est prêt, il ne manque que l'empreinte.

.EXAMPLE
    .\build\publish.ps1
    .\build\publish.ps1 -SkipTests
    .\build\publish.ps1 -CertificateThumbprint A1B2C3...
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,

    # Empreinte du certificat de signature de code, dans le magasin personnel de l'utilisateur.
    # À défaut, la variable d'environnement LDI12_SIGN_THUMBPRINT est lue : de quoi signer sans
    # écrire l'empreinte dans une ligne de commande qui reste dans l'historique.
    [string]$CertificateThumbprint = $env:LDI12_SIGN_THUMBPRINT,

    # L'horodatage est ce qui permet à la signature de survivre à l'expiration du certificat.
    # Sans lui, l'exécutable publié aujourd'hui serait tenu pour non signé dans trois ans.
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'LDI12.Diagnostic.sln'
$artifacts = Join-Path $root 'artifacts'

# Réutilise la recherche de build.ps1 : MSBuild 17 est une exigence, pas une préférence.
function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild `
                            -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null
        foreach ($candidate in $found) {
            if ((& $candidate -version -nologo) -match '^(\d+)\.' -and [int]$Matches[1] -ge 17) { return $candidate }
        }
    }

    $patterns = @(
        'C:\Program Files\Microsoft Visual Studio\2022\*\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2026\*\MSBuild\Current\Bin\MSBuild.exe'
    )
    foreach ($pattern in $patterns) {
        $candidate = Get-Item $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }

    throw 'MSBuild 17 ou supérieur est introuvable. Voir build\build.ps1.'
}

# Signe l'exécutable publié, quand il y a de quoi le signer.
#
# Sans certificat, ce n'est pas une erreur : l'outil s'utilise depuis une clé USB tenue par le
# technicien, où SmartScreen n'intervient pas. C'est la mise en ligne qui l'exige, et le message
# ci-dessous existe pour que personne ne publie sans savoir ce que le client verra.
#
# À noter : signer cet exécutable ne change rien à la détection du pilote de capteurs. Windows
# nomme le pilote, pas le programme qui le charge : voir docs\01-architecture.md § 1.6 ter.
function Set-Signature {
    param([string]$Path, [string]$Thumbprint, [string]$Timestamp)

    if (-not $Thumbprint) {
        Write-Host '  Non signé.' -ForegroundColor Yellow
        Write-Host "  SmartScreen annoncera « Éditeur inconnu » au téléchargement. Sans conséquence"
        Write-Host "  pour une clé USB d'atelier ; à régler avant toute mise en ligne."
        return $false
    }

    $pattern = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\*\x64\signtool.exe'
    $signtool = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1

    if (-not $signtool) { throw 'signtool.exe est introuvable : installez le SDK Windows 10/11.' }

    & $signtool.FullName sign /sha1 $Thumbprint /fd sha256 /td sha256 /tr $Timestamp $Path
    if ($LASTEXITCODE -ne 0) { throw "Échec de la signature (code $LASTEXITCODE)." }

    # Vérifier après avoir signé : une signature qu'on ne relit pas est une signature qu'on
    # découvre invalide chez le client.
    & $signtool.FullName verify /pa /q $Path
    if ($LASTEXITCODE -ne 0) { throw "La signature apposée n'est pas vérifiable (code $LASTEXITCODE)." }

    Write-Host '  Signé et horodaté.' -ForegroundColor Green
    return $true
}

# Ce qui accompagne un exécutable sans être une dépendance à embarquer.
function Get-Dependencies {
    param([string]$OutputDirectory, [string]$HostFileName)

    Get-ChildItem $OutputDirectory -File |
        Where-Object { $_.Extension -in '.dll', '.exe' } |
        Where-Object { $_.Name -ne $HostFileName }
}

function Set-Bundle {
    param([string]$ProjectDirectory, [System.IO.FileInfo[]]$Files)

    $bundle = Join-Path $ProjectDirectory 'Bundle'
    if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
    New-Item -ItemType Directory -Path $bundle | Out-Null

    foreach ($file in $Files) { Copy-Item $file.FullName -Destination $bundle }
    return $bundle
}

$msbuild = Find-MSBuild
Write-Host "MSBuild : $msbuild"
Write-Host ''

# --- Passe 1 : compilation ordinaire ------------------------------------------------------
Write-Host '[1/3] Compilation Release…' -ForegroundColor Cyan
& $msbuild $solution -t:Restore,Build -p:Configuration=Release -v:m -nologo
if ($LASTEXITCODE -ne 0) { throw "Échec de la compilation (code $LASTEXITCODE)." }

if (-not $SkipTests) {
    Write-Host ''
    Write-Host 'Tests…' -ForegroundColor Cyan
    & dotnet test $solution --no-build --configuration Release -v q
    if ($LASTEXITCODE -ne 0) { throw "Échec des tests (code $LASTEXITCODE)." }
}

$appProject = Join-Path $root 'src\LDI12.App'
$hostProject = Join-Path $root 'src\LDI12.ProbeHost'
$appOutput = Join-Path $appProject 'bin\Release\net462'
$hostOutput = Join-Path $hostProject 'bin\Release\net462'

$bundles = @()

try {
    # --- Passe 2 : l'hôte de sondes devient autonome ---------------------------------------
    # Il est lancé comme un second processus, avec ou sans élévation : il ne peut pas emprunter
    # le résolveur de l'interface et doit donc porter ses propres dépendances.
    Write-Host ''
    Write-Host '[2/3] Hôte de sondes, dépendances embarquées…' -ForegroundColor Cyan

    $bundles += Set-Bundle -ProjectDirectory $hostProject `
                           -Files (Get-Dependencies -OutputDirectory $hostOutput -HostFileName 'LDI12.ProbeHost.exe')

    & $msbuild (Join-Path $hostProject 'LDI12.ProbeHost.csproj') `
        -t:Build -p:Configuration=Release -p:BundleDependencies=true -v:m -nologo
    if ($LASTEXITCODE -ne 0) { throw "Échec de la compilation de l'hôte (code $LASTEXITCODE)." }

    $bundledHost = Get-Item (Join-Path $hostOutput 'LDI12.ProbeHost.exe')
    Write-Host ("        LDI12.ProbeHost.exe : {0:N1} Mo" -f ($bundledHost.Length / 1MB))

    # --- Passe 3 : l'interface embarque tout ----------------------------------------------
    Write-Host ''
    Write-Host '[3/3] Interface, dépendances et hôte embarqués…' -ForegroundColor Cyan

    $dependencies = @(Get-Dependencies -OutputDirectory $appOutput -HostFileName 'LDI12.Diagnostic.exe')
    $dependencies += $bundledHost

    $bundles += Set-Bundle -ProjectDirectory $appProject -Files $dependencies

    & $msbuild (Join-Path $appProject 'LDI12.App.csproj') `
        -t:Build -p:Configuration=Release -p:BundleDependencies=true -v:m -nologo
    if ($LASTEXITCODE -ne 0) { throw "Échec de la compilation de l'interface (code $LASTEXITCODE)." }

    # --- Artefact ---------------------------------------------------------------------------
    $exe = Get-Item (Join-Path $appOutput 'LDI12.Diagnostic.exe')
    # ProductVersion porte l'empreinte du commit après un « + » : utile dans les propriétés du
    # fichier, illisible dans un nom de fichier. Le nom garde les trois chiffres.
    $version = $exe.VersionInfo.FileVersion
    if (-not $version) { $version = '0.0.0.0' }
    $version = ($version -split '\+')[0]
    $version = ($version -replace '\.0$', '')

    if (-not (Test-Path $artifacts)) { New-Item -ItemType Directory -Path $artifacts | Out-Null }

    $published = Join-Path $artifacts ("LDI12-Diagnostic-{0}.exe" -f $version)
    Copy-Item $exe.FullName -Destination $published -Force

    Write-Host ''
    $signed = Set-Signature -Path $published -Thumbprint $CertificateThumbprint -Timestamp $TimestampUrl

    # L'empreinte est ce qui permettra de vérifier, six mois plus tard, que la clé USB porte
    # bien ce qui a été publié, et non une copie abîmée ou une version oubliée. Elle se calcule
    # après la signature : signer modifie le fichier, et une empreinte prise avant ne
    # correspondrait à rien de ce qui est distribué.
    $hash = (Get-FileHash $published -Algorithm SHA256).Hash
    $checksum = "$published.sha256"
    "$hash  $(Split-Path $published -Leaf)" | Set-Content -Path $checksum -Encoding ASCII

    Write-Host ''
    Write-Host 'Publié.' -ForegroundColor Green
    Write-Host ("  Fichier  : {0}" -f $published)
    Write-Host ("  Taille   : {0:N1} Mo" -f ((Get-Item $published).Length / 1MB))
    Write-Host ("  SHA-256  : {0}" -f $hash)
    Write-Host ("  Signature: {0}" -f $(if ($signed) { 'apposée et horodatée' } else { 'aucune' }))
    Write-Host ''
    Write-Host "  Un seul fichier à copier. Rien à installer."
}
finally {
    # Les dossiers Bundle ne survivent pas à la publication : laissés en place, la compilation
    # de développement suivante embarquerait des bibliothèques périmées sans que rien ne le dise.
    foreach ($bundle in $bundles) {
        if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
    }
}
