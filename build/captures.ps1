# Captures d'écran de chaque écran, en thème sombre et en thème clair.
#
# Sert à juger l'interface sur pièce : une modification de style se vérifie sur l'image, pas
# dans le XAML. Chaque capture lance l'exécutable en mode capture (--screenshot), qui fait une
# analyse, rend la fenêtre en PNG et quitte.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Size = '1440x900'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Out)) { New-Item -ItemType Directory -Path $Out | Out-Null }
Get-ChildItem $Out -Filter *.png -ErrorAction SilentlyContinue | Remove-Item -Force

# Index de navigation, nom de fichier, et décalages de défilement à capturer en plus du haut.
$screens = @(
    @{ Nav = 0;  Name = 'vue-ensemble';  Scroll = @(600) },
    @{ Nav = 1;  Name = 'materiel';      Scroll = @() },
    @{ Nav = 2;  Name = 'stockage';      Scroll = @() },
    @{ Nav = 3;  Name = 'windows';       Scroll = @() },
    @{ Nav = 4;  Name = 'reseau';        Scroll = @() },
    @{ Nav = 5;  Name = 'securite';      Scroll = @() },
    @{ Nav = 6;  Name = 'performances';  Scroll = @() },
    @{ Nav = 7;  Name = 'surveillance';  Scroll = @() },
    @{ Nav = 8;  Name = 'mesures';       Scroll = @() },
    @{ Nav = 9;  Name = 'reparations';   Scroll = @() },
    @{ Nav = 10; Name = 'nettoyage';     Scroll = @() },
    @{ Nav = 11; Name = 'donnees';       Scroll = @(700, 1400, 2100) },
    @{ Nav = 12; Name = 'outils';        Scroll = @() },
    @{ Nav = 13; Name = 'rapports';      Scroll = @() },
    @{ Nav = 14; Name = 'historique';    Scroll = @() },
    @{ Nav = 15; Name = 'reglages';      Scroll = @() },
    @{ Nav = 16; Name = 'a-propos';      Scroll = @() }
)

function Capture([string]$theme, [int]$nav, [string]$name, [int]$scroll) {
    $file = Join-Path $Out ("{0}-{1:00}-{2}{3}.png" -f $theme, $nav, $name, $(if ($scroll -gt 0) { "-$scroll" } else { '' }))
    $arguments = @('--screenshot', $file, '--theme', $theme, '--nav', $nav, '--size', $Size)
    if ($scroll -gt 0) { $arguments += @('--scroll', $scroll) }

    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(150000)) {
        Write-Warning "Capture $name ($theme) : délai dépassé, processus arrêté."
        $process.Kill()
    }

    if (Test-Path $file) { Write-Host ("  {0}" -f (Split-Path $file -Leaf)) }
    else { Write-Warning "Capture $name ($theme) : aucune image." }
}

foreach ($theme in 'dark', 'light') {
    Write-Host "Thème $theme" -ForegroundColor Cyan
    foreach ($screen in $screens) {
        Capture $theme $screen.Nav $screen.Name 0
        foreach ($offset in $screen.Scroll) { Capture $theme $screen.Nav $screen.Name $offset }
    }
}
