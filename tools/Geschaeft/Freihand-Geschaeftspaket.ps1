# Freihand: Geschäftspakete bauen. Läuft auf dem eigenen PC, nie auf dem Zielgerät.
#
# Zwei Pakete (22.11.2026), damit ein Update klein bleibt und die IT nur das Programm neu prüfen muss:
# - Programmpaket  Freihand-Programm-<Datum>-<Stand>.zip : die Geschäftsausgabe (ohne jeden Online-Baustein, GE-02) als
#   eigenständiges Programm für Windows x64, ohne .NET, Git oder Administratorrechte; wird nach %LOCALAPPDATA%\Programs
#   entpackt. Nur Laufzeiten für Windows x64, keine Modelle. PRUEFSUMMEN.txt nennt die SHA-256 jeder Datei.
# - Modellpaket  Freihand-Modelle-<Datum>.zip : die Modelle „small“, „large-v3-turbo-q5_0“ und Silero VAD nach Prüfung der
#   SHA-256-Summen (R31); wird einmal nach %LOCALAPPDATA% entpackt (Datenordner Freihand\Modelle) und bleibt bei Updates.
#   Zur Laufzeit wird nichts geladen (WE-14).
# Neben jedes ZIP kommt <ZIP>.sha256 mit der Prüfsumme des ZIP.
#
# Aufruf in einem normalen PowerShell-Fenster im Repository:
#   & .\tools\Geschaeft\Freihand-Geschaeftspaket.ps1
#   & .\tools\Geschaeft\Freihand-Geschaeftspaket.ps1 -Modelle <Ordner> -Ziel <Ordner>
#   & .\tools\Geschaeft\Freihand-Geschaeftspaket.ps1 -OhneModelle     (nur das Programmpaket, für Updates)

param(
    [string]$Modelle = (Join-Path $env:LOCALAPPDATA "Freihand\Modelle"),
    [string]$Ziel = (Join-Path $env:USERPROFILE "Downloads"),
    [switch]$OhneModelle
)

$ErrorActionPreference = "Stop"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$stand = (git -C $repo log --oneline -1 --format="%h").Trim()
$datum = Get-Date -Format "yyyyMMdd"
$programmName = "Freihand-Programm-$datum-$stand"
$modellName = "Freihand-Modelle-$datum"
$arbeit = Join-Path ([IO.Path]::GetTempPath()) "Freihand-Pakete-$datum-$stand"
$programm = Join-Path $arbeit "programm\Freihand"
$modellWurzel = Join-Path $arbeit "modelle\Freihand"

# Dieselben Prüfsummen wie src/Freihand.Adapters/Recognition/KnownModels.cs (ein Test hält beide gleich)
$pruefsummen = @{
    "ggml-small.bin" = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b"
    "ggml-large-v3-turbo-q5_0.bin" = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2"
    "ggml-silero-v5.1.2.bin" = "29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf"
}

function Schritt($text) { Write-Host ""; Write-Host "== $text" -ForegroundColor Cyan }
function Abbruch($text) { Write-Host $text -ForegroundColor Red; exit 1 }

# PRUEFSUMMEN.txt: je Datei „SHA-256  relativer Pfad“, sortiert; die Datei selbst steht nicht darin
function Pruefsummen($ordner, $datei) {
    $basis = (Resolve-Path $ordner).Path.TrimEnd('\') + '\'
    $zeilen = Get-ChildItem -Recurse -File $ordner | Sort-Object FullName | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant(), $_.FullName.Substring($basis.Length)
    }
    [IO.File]::WriteAllLines($datei, [string[]]$zeilen, (New-Object Text.UTF8Encoding($false)))
}

# ZIP mit dem Ordner „Freihand“ an der Wurzel, daneben <ZIP>.sha256
function Packen($quelle, $name) {
    $zip = Join-Path $Ziel "$name.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path $quelle -DestinationPath $zip -CompressionLevel Optimal
    $summe = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$zip.sha256", "$summe  $name.zip`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host ("Fertig: {0} ({1} MB)" -f $zip, [math]::Round((Get-Item $zip).Length / 1MB)) -ForegroundColor Green
    Write-Host "SHA-256: $summe"
}

if (git -C $repo status --porcelain) { Abbruch "Das Repository hat ungesicherte Änderungen; das Paket soll genau einem Stand entsprechen." }
if (-not (Test-Path $Ziel)) { Abbruch "Zielordner fehlt: $Ziel" }
$Ziel = (Resolve-Path $Ziel).Path
if (Test-Path $arbeit) { Remove-Item -Recurse -Force $arbeit }

Schritt "Geschäftsausgabe bauen ($stand)"
dotnet publish (Join-Path $repo "src\Freihand.App\Freihand.App.csproj") -c Release -r win-x64 --self-contained true `
    -p:FreihandAusgabe=Geschaeft -p:RestoreLockedMode=true -o $programm --nologo -v quiet
if ($LASTEXITCODE -ne 0) { Abbruch "Bauen gescheitert." }
if ((git -C $repo status --porcelain)) { Abbruch "Der Bau hat Dateien im Repository geändert (Sperrdateien?); bitte melden." }

Schritt "Nur Windows x64"
# Das Ziel NurGeschaeftsLaufzeiten in Freihand.App.csproj lässt Laufzeiten für andere Systeme weg (z. B. linux-x64); hier
# wird nur geprüft, wie in der CI: weniger Grösse, weniger Fragen beim Virenschutz
$fremd = Get-ChildItem -Recurse -File $programm | Where-Object { $_.Extension -in ".so", ".dylib" }
if ($fremd) { Abbruch ("Laufzeiten für andere Systeme im Paket: " + (($fremd | ForEach-Object Name) -join ", ")) }
foreach ($laufzeit in "runtimes\vulkan\win-x64\ggml-vulkan-whisper.dll", "runtimes\win-x64\whisper.dll") {
    if (-not (Test-Path (Join-Path $programm $laufzeit))) { Abbruch "$laufzeit fehlt – vermutlich Virenschutz. Nichts umgehen; bitte melden." }
}
Write-Host "Nur Laufzeiten für Windows x64, Vulkan und CPU vorhanden."

Schritt "Inhalt prüfen"
$verboten = Get-ChildItem -Recurse $programm | Where-Object {
    $_.Name -like "Anthropic*" -or $_.Name -like "Freihand.Online*" -or $_.FullName -match '\\runtimes\\cuda\\' -or $_.Name -like "*.bin"
}
if ($verboten) { Abbruch ("Online-, CUDA- oder Modellteile im Programmpaket: " + (($verboten | ForEach-Object Name) -join ", ")) }
foreach ($datei in "Freihand.exe", "Freihand.dll", "Freihand.Recognizer.exe", "Freihand.Zielprobe.exe", "Fixtures\au01-testtext.txt") {
    if (-not (Test-Path (Join-Path $programm $datei))) { Abbruch "$datei fehlt – vermutlich Virenschutz. Nichts umgehen; bitte melden." }
}
# Sicherheitsprüfung, S1: kein Messprotokoll per Umgebungsvariable (FREIHAND_MESSPROTOKOLL), nur das Tagesprotokoll
if ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $programm "Freihand.dll"))).Contains("MeasurementLog")) {
    Abbruch "Messprotokoll in der Geschäftsausgabe – falsch gebaut."
}
Write-Host "Kein Online-Baustein, keine CUDA-Laufzeit, keine Modelle, kein Messprotokoll."

Schritt "Programmpaket"
Copy-Item (Join-Path $PSScriptRoot "LIESMICH.txt") $programm
Copy-Item (Join-Path $repo "docs\pakete\it-merkblatt.md") (Join-Path $programm "IT-Merkblatt.md")
Copy-Item (Join-Path $repo "tools\Beta\Freihand-Auswertung.ps1") $programm
Copy-Item (Join-Path $repo "tools\Beta\Beobachtungsblatt.md") $programm
Pruefsummen $programm (Join-Path $programm "PRUEFSUMMEN.txt")
Packen $programm $programmName

if (-not $OhneModelle) {
    Schritt "Modellpaket"
    $modellOrdner = New-Item -ItemType Directory -Force -Path (Join-Path $modellWurzel "Modelle")
    foreach ($eintrag in $pruefsummen.GetEnumerator()) {
        $quelle = Join-Path $Modelle $eintrag.Key
        if (-not (Test-Path $quelle)) { Abbruch "Modell fehlt: $quelle" }
        $summe = (Get-FileHash -Algorithm SHA256 $quelle).Hash.ToLowerInvariant()
        if ($summe -ne $eintrag.Value) { Abbruch "Prüfsumme stimmt nicht: $($eintrag.Key)" }
        Copy-Item $quelle $modellOrdner
        Write-Host "$($eintrag.Key) geprüft."
    }
    Pruefsummen $modellOrdner (Join-Path $modellOrdner "PRUEFSUMMEN.txt")
    Packen $modellWurzel $modellName
}

Remove-Item -Recurse -Force $arbeit
