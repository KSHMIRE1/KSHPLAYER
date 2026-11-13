# KSHDESK · « Mise à jour » : met aussi à jour KSHPLAYER (et KSHPLAYER PRO) s'ils sont installés.
# Lancé en arrière-plan par build.cmd. Journal : %LOCALAPPDATA%\KSHDESK\maj-kshplayer.log
# Si une vidéo est ouverte, on ne coupe rien : KSHPLAYER se met de toute façon à jour tout seul quand on le ferme.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$logDir = Join-Path $env:LOCALAPPDATA 'KSHDESK'
$log = Join-Path $logDir 'maj-kshplayer.log'
function Log($m) {
    try {
        if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }
        Add-Content -Path $log -Value ("[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $m) -Encoding UTF8
    } catch {}
}
function V($s) { $p = @(([string]$s -split '[^0-9]+') | Where-Object { $_ -ne '' }); while ($p.Count -lt 3) { $p += '0' }; [version]("{0}.{1}.{2}" -f $p[0], $p[1], $p[2]) }

$base = 'https://raw.githubusercontent.com/KSHMIRE1/KSHPLAYER/telechargements'
try {
    $info = Invoke-RestMethod ("$base/version.json?t=" + [DateTime]::UtcNow.Ticks) -TimeoutSec 20
    $latest = V $info.version
} catch { Log "KSHPLAYER : version en ligne illisible ($($_.Exception.Message))"; exit 0 }

$editions = @(
    @{ Name = 'KSHPLAYER';     Exe = 'KSHPLAYER.exe';    Process = 'KSHPLAYER';    Setup = $info.files.windows },
    @{ Name = 'KSHPLAYER PRO'; Exe = 'KSHPLAYERPRO.exe'; Process = 'KSHPLAYERPRO'; Setup = $info.files.windows_pro }
)
foreach ($e in $editions) {
    $exe = Join-Path $env:LOCALAPPDATA ("Programs\" + $e.Name + "\" + $e.Exe)
    if (-not (Test-Path $exe) -or -not $e.Setup) { continue }
    $have = V (Get-Item $exe).VersionInfo.FileVersion
    if ($have -ge $latest) { Log "$($e.Name) $have : déjà à jour"; continue }
    if (Get-Process -Name $e.Process -ErrorAction SilentlyContinue) {
        Log "$($e.Name) $have → $latest : une vidéo est ouverte, la mise à jour se fera à sa fermeture"
        continue
    }
    try {
        $dir = Join-Path $env:TEMP 'KSHPLAYER-maj'
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        $file = Join-Path $dir $e.Setup
        Invoke-WebRequest ("$base/" + [Uri]::EscapeDataString($e.Setup) + "?v=" + $info.version) -OutFile $file -TimeoutSec 600
        $got = V (Get-Item $file).VersionInfo.FileVersion
        if ($got -ne $latest) { Log "$($e.Name) : installateur téléchargé en $got au lieu de $latest, on réessaiera"; continue }
        # pas de -Wait : il attendrait aussi l'aperçu Espace relancé par l'installateur (qui tourne en permanence)
        $setup = Start-Process -FilePath $file -ArgumentList '/update' -PassThru
        if (-not $setup.WaitForExit(600000)) { Log "$($e.Name) : l'installateur n'a pas fini en 10 min"; continue }
        $now = V (Get-Item $exe).VersionInfo.FileVersion
        if ($now -ne $latest) { Log "$($e.Name) : toujours en $now après l'installateur"; continue }
        Log "$($e.Name) $have → $latest : mis à jour"
    } catch { Log "$($e.Name) : mise à jour impossible ($($_.Exception.Message))" }
}
