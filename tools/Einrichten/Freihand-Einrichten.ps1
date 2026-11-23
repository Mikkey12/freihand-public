# Freihand einrichten und aktualisieren.
#
# - Holt den Stand aus GitHub in eine eigene Arbeitskopie (%LOCALAPPDATA%\Freihand\Quelle) und baut ihn.
# - Beendet ein laufendes Freihand regulär (Freihand.exe --beenden), nie hart.
# - Legt das Programm nach %LOCALAPPDATA%\Programs\Freihand (ohne Administratorrechte), mit Eintrag im Startmenü,
#   schaltet den Autostart ein (abschaltbar im Menü von Freihand) und startet Freihand.
# - Einstellungen, Modelle, Sicherungen, Schlüssel und Protokoll in %LOCALAPPDATA%\Freihand bleiben unberührt.
# - Virenschutz wird nicht umgangen: Fehlt Freihand.dll nach dem Kopieren, meldet das Skript es und hört auf.
#
# Aufruf in einem normalen PowerShell-Fenster (nicht als Administrator):
#   & "<Pfad>\Freihand-Einrichten.ps1"                      # Stand main
#   & "<Pfad>\Freihand-Einrichten.ps1" -Stand <Branch>      # anderer Stand, z. B. zum Ausprobieren vor dem Merge
#   & "<Pfad>\Freihand-Einrichten.ps1" -OhneAutostart

param(
    [string]$Stand = "main",
    [string]$Repository,
    [switch]$OhneAutostart
)

$ErrorActionPreference = "Stop"
if (-not $Repository) {
    $Repository = git -C $PSScriptRoot remote get-url origin
    if ($LASTEXITCODE -ne 0 -or -not $Repository) { throw "Bitte die Projektadresse mit -Repository angeben." }
}
$daten = Join-Path $env:LOCALAPPDATA "Freihand"
$quelle = Join-Path $daten "Quelle"
$ziel = Join-Path $env:LOCALAPPDATA "Programs\Freihand"
$build = Join-Path $quelle "src\Freihand.App\bin\Release\net10.0-windows"
$verknuepfung = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Freihand.lnk"

function Schritt($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Abbruch($text) { Write-Host $text -ForegroundColor Red; exit 1 }

foreach ($werkzeug in "git", "dotnet") {
    if (-not (Get-Command $werkzeug -ErrorAction SilentlyContinue)) { Abbruch "$werkzeug fehlt; bitte zuerst installieren." }
}

Schritt "Stand '$Stand' holen"
if (-not (Test-Path (Join-Path $quelle ".git"))) {
    New-Item -ItemType Directory -Force -Path $daten | Out-Null
    git clone --quiet -- $Repository $quelle
    if ($LASTEXITCODE -ne 0) { Abbruch "Klonen gescheitert (Anmeldung bei GitHub?)." }
}
$origin = git -C $quelle remote get-url origin
if ($LASTEXITCODE -ne 0 -or $origin -cne $Repository) { Abbruch "Die vorhandene Arbeitskopie verwendet eine andere Projektadresse. Bitte die Quelle prüfen." }
if (git -C $quelle status --porcelain) { Abbruch "In $quelle gibt es Änderungen von Hand; bitte prüfen, nichts wird überschrieben." }
git -C $quelle fetch --quiet origin
if ($LASTEXITCODE -ne 0) { Abbruch "Holen von GitHub gescheitert." }
git -C $quelle checkout --quiet --detach "origin/$Stand"
if ($LASTEXITCODE -ne 0) { Abbruch "Stand 'origin/$Stand' nicht gefunden." }
Write-Host ("Stand: " + (git -C $quelle log --oneline -1))

Schritt "Bauen"
dotnet build (Join-Path $quelle "src\Freihand.App\Freihand.App.csproj") -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Abbruch "Bauen gescheitert." }
if (-not (Test-Path (Join-Path $build "Freihand.dll"))) {
    Abbruch "Freihand.dll fehlt im Build-Ordner – vermutlich Virenschutz-Quarantäne. Nichts umgehen; bitte melden."
}

Schritt "Laufendes Freihand regulär beenden"
if (Get-Process -Name "Freihand" -ErrorAction SilentlyContinue) {
    & (Join-Path $build "Freihand.exe") --beenden
    $frist = (Get-Date).AddSeconds(20)
    while ((Get-Process -Name "Freihand", "Freihand.Recognizer" -ErrorAction SilentlyContinue) -and (Get-Date) -lt $frist) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-Process -Name "Freihand" -ErrorAction SilentlyContinue) {
        Abbruch "Freihand hat sich nicht beendet. Bitte über das Menü im Infobereich beenden und das Skript neu starten."
    }
    Write-Host "Beendet."
}
else {
    Write-Host "Freihand lief nicht."
}

Schritt "Programmordner $ziel"
New-Item -ItemType Directory -Force -Path $ziel | Out-Null
robocopy $build $ziel /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Abbruch "Kopieren gescheitert (robocopy $LASTEXITCODE)." }
if (-not (Test-Path (Join-Path $ziel "Freihand.dll"))) {
    Abbruch "Freihand.dll fehlt im Programmordner – vermutlich Virenschutz-Quarantäne. Nichts umgehen; bitte melden."
}

Schritt "Startmenü"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($verknuepfung)
$link.TargetPath = Join-Path $ziel "Freihand.exe"
$link.WorkingDirectory = $ziel
$link.Description = "Freihand – Diktieren"
$link.Save()
Write-Host "Eintrag 'Freihand' im Startmenü."

Schritt "Autostart"
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if ($OhneAutostart) {
    Write-Host "Nicht eingeschaltet (-OhneAutostart); im Menü von Freihand einschaltbar."
}
else {
    New-ItemProperty -Path $run -Name "Freihand" -Value ('"' + (Join-Path $ziel "Freihand.exe") + '"') -PropertyType String -Force | Out-Null
    Write-Host "Eingeschaltet; im Menü von Freihand abschaltbar ('Mit Windows starten')."
}

Schritt "Freihand starten"
Start-Process -FilePath (Join-Path $ziel "Freihand.exe") -WorkingDirectory $ziel
Write-Host ""
Write-Host "Fertig. Für ein Update dieses Skript erneut ausführen." -ForegroundColor Green
