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
    @{ Nav = 1;  Name = 'intervention';  Scroll = @() },
    @{ Nav = 2;  Name = 'assistant';     Scroll = @() },
    @{ Nav = 2;  Name = 'assistant-analyse'; Scroll = @(); Assistant = 'running' },
    @{ Nav = 2;  Name = 'assistant-plan'; Scroll = @(); Assistant = 'result' },
    @{ Nav = 3;  Name = 'materiel';      Scroll = @() },
    @{ Nav = 4;  Name = 'stockage';      Scroll = @() },
    @{ Nav = 5;  Name = 'windows';       Scroll = @() },
    @{ Nav = 6;  Name = 'reseau';        Scroll = @() },
    @{ Nav = 7;  Name = 'securite';      Scroll = @() },
    @{ Nav = 8;  Name = 'performances';  Scroll = @() },
    @{ Nav = 9; Name = 'reparations';   Scroll = @() },
    @{ Nav = 10; Name = 'nettoyage';     Scroll = @() },
    @{ Nav = 11; Name = 'espace-disque'; Scroll = @(); Disk = 'C:\Program Files\Git' },
    @{ Nav = 12; Name = 'donnees';       Scroll = @(700, 1400) },
    @{ Nav = 12; Name = 'donnees-restauration'; Scroll = @(700); Mode = 'restore' },
    @{ Nav = 12; Name = 'donnees-transfert'; Scroll = @(); Mode = 'transfert' },
    @{ Nav = 12; Name = 'donnees-bilan';     Scroll = @(); Mode = 'bilan' },
    @{ Nav = 13; Name = 'outils';        Scroll = @() },
    @{ Nav = 14; Name = 'disques';       Scroll = @(700, 1400); Demo = 'disks' },
    @{ Nav = 15; Name = 'plantages';     Scroll = @(700); Demo = 'crash' },
    @{ Nav = 16; Name = 'programmes';    Scroll = @(); Demo = 'processes' },
    @{ Nav = 16; Name = 'programmes-demarrage'; Scroll = @(); Demo = 'autoruns' },
    @{ Nav = 17; Name = 'magasin-pilotes'; Scroll = @(); Demo = 'drivers' },
    @{ Nav = 18; Name = 'reseau-local';  Scroll = @(); Demo = 'lan-devices' },
    @{ Nav = 18; Name = 'reseau-local-connexions'; Scroll = @(); Demo = 'lan-connections' },
    @{ Nav = 18; Name = 'reseau-local-trafic'; Scroll = @(); Demo = 'lan-traffic' },
    @{ Nav = 18; Name = 'reseau-local-statistiques'; Scroll = @(); Demo = 'lan-stats' },
    @{ Nav = 19; Name = 'surveillance';  Scroll = @() },
    @{ Nav = 20; Name = 'mesures';       Scroll = @() },
    @{ Nav = 0;  Name = 'recherche';     Scroll = @(); Search = 'reseau' },
    @{ Nav = 21; Name = 'rapports';      Scroll = @() },
    @{ Nav = 22; Name = 'historique';    Scroll = @() },
    @{ Nav = 23; Name = 'reglages';      Scroll = @() },
    @{ Nav = 24; Name = 'a-propos';      Scroll = @() }
)

function Capture([string]$theme, [int]$nav, [string]$name, [int]$scroll, [string]$mode, [string]$search, [string]$disk, [string]$assistant, [string]$demo) {
    $file = Join-Path $Out ("{0}-{1:00}-{2}{3}.png" -f $theme, $nav, $name, $(if ($scroll -gt 0) { "-$scroll" } else { '' }))
    $arguments = @('--screenshot', $file, '--theme', $theme, '--nav', $nav, '--size', $Size)
    if ($mode) { $arguments += @('--data-mode', $mode) }
    if ($search) { $arguments += @('--search', $search) }
    if ($disk) { $arguments += @('--disk-scan', ('"{0}"' -f $disk)) }
    if ($assistant) { $arguments += @('--assistant-demo', $assistant) }
    if ($demo -eq 'crash') { $arguments += @('--crash-demo') }
    if ($demo -eq 'processes' -or $demo -eq 'autoruns') { $arguments += @('--programs-demo', $demo) }
    if ($demo -eq 'drivers') { $arguments += @('--drivers-demo') }
    if ($demo -eq 'disks') { $arguments += @('--disks-demo') }
    if ($demo -like 'lan-*') { $arguments += @('--lan-demo', $demo.Substring(4)) }
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
        Capture $theme $screen.Nav $screen.Name 0 $screen.Mode $screen.Search $screen.Disk $screen.Assistant $screen.Demo
        foreach ($offset in $screen.Scroll) { Capture $theme $screen.Nav $screen.Name $offset $screen.Mode $screen.Search $screen.Disk $screen.Assistant $screen.Demo }
    }
}
