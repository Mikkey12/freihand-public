# Freihand-Beta: Auswertung der Protokolle.
#
# Liest die Tagesdateien in %LOCALAPPDATA%\Freihand\Protokoll (je Tag JJJJ-MM-TT.txt, nur Zustände und Codes, kein Text)
# und zählt je Tag nur Ereigniszeilen, die Freihand bei jedem Ereignis genau einmal schreibt (Codex zu PR #56); die
# Zustandszeilen „phase=…“ sind Momentaufnahmen und werden nicht gezählt:
#   Diktate     – „ereignis diktat_start“: jeder Startversuch, auch ein abgewiesener (z. B. Gerät fehlt)
#   Gehalten    – „ereignis abschnitt_gehalten“: jeder Abschnitt, der nicht ganz geschrieben wurde
#   Meldungen   – „ereignis meldung code=…“, je Code; dazu „ereignis erkenner_neustart“ (Code ErkennerNeustart, je
#                 gestartetem Erkennerprozess ausser dem ersten, automatisch oder von Hand) und „ereignis erkenner_nicht_verfuegbar“ (Code ErkennerNichtVerfuegbar,
#                 je Ausfall), Codex zu PR #69
#   Marken      – „marke nr=…“ aus „Problem merken“, mit Uhrzeit und Nummer
#   Verzug      – „ereignis abschnitt_geschrieben verzug_ms=…“: Zeit vom Ende des Gesprochenen bis der Text im Ziel steht,
#                 je Tag Median und 95. Perzentil (Messung auf dem Zielgerät, QM-08); „verzug_ms=ungueltig“ (Audiotakt
#                 und Stoppuhr laufen auseinander) zählt als Abschnitt, aber nie als Messwert
#
# Aufruf in einem normalen PowerShell-Fenster:
#   & "<Pfad>\Freihand-Auswertung.ps1"                       # letzte 14 Tage
#   & "<Pfad>\Freihand-Auswertung.ps1" -Tage 3
#   & "<Pfad>\Freihand-Auswertung.ps1" -Ordner <Ordner> -Json  # für Tests und Weiterverarbeitung

param(
    [string]$Ordner = (Join-Path $env:LOCALAPPDATA "Freihand\Protokoll"),
    [int]$Tage = 14,
    [switch]$Json
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Ordner)) {
    Write-Host "Kein Protokollordner: $Ordner" -ForegroundColor Yellow
    exit 1
}

$grenze = (Get-Date).Date.AddDays(-$Tage)
# Alle Tage lesen, damit das Modell aus einer früheren Startzeile weiter gilt; ausgegeben werden nur Tage ab der Grenze
$dateien = Get-ChildItem -Path $Ordner -Filter "*.txt" |
    Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}\.txt$' } |
    Sort-Object Name

# Perzentil nach dem Rangverfahren „nächster Rang“ (wie in den Messberichten); ohne Werte null
function Perzentil($werte, [int]$p) {
    if ($werte.Count -eq 0) { return $null }
    $sortiert = $werte | Sort-Object
    $rang = [math]::Ceiling($p / 100.0 * $sortiert.Count)
    return [int]@($sortiert)[[math]::Max(1, $rang) - 1]
}

# Üblicher Median: bei gerader Anzahl der Mittelwert der beiden mittleren Werte (Codex-Prüfung PR #57); ohne Werte null
function Median($werte) {
    if ($werte.Count -eq 0) { return $null }
    $sortiert = @($werte | Sort-Object)
    $mitte = [math]::Floor($sortiert.Count / 2)
    if ($sortiert.Count % 2 -eq 1) { return [int]$sortiert[$mitte] }
    return [int][math]::Round(($sortiert[$mitte - 1] + $sortiert[$mitte]) / 2.0, [MidpointRounding]::AwayFromZero)
}

$ergebnis = @()
# Modell aus der letzten Startzeile; läuft Freihand über Mitternacht, gilt es im nächsten Tag weiter
$modell = "unbekannt"
foreach ($datei in $dateien) {
    $imZeitraum = [datetime]::ParseExact($datei.BaseName, "yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture) -ge $grenze
    $jeModell = [ordered]@{}
    $diktate = 0
    $gehalten = 0
    $meldungen = @{}
    $marken = @()
    $verzug = New-Object System.Collections.Generic.List[int]
    $ungueltig = 0

    foreach ($zeile in [IO.File]::ReadAllLines($datei.FullName)) {
        $leer = $zeile.IndexOf(' ')
        if ($leer -lt 0) { continue }
        $zeit = $zeile.Substring(0, $leer)
        $rest = $zeile.Substring($leer + 1)

        if ($rest.StartsWith("marke ")) {
            $nummer = if ($rest -match '\bnr=(\d+)') { [int]$Matches[1] } else { 0 }
            $quelle = if ($rest -match '\bquelle=(\S+)') { $Matches[1] } else { "-" }
            $marken += [pscustomobject]@{ Zeit = $zeit; Nummer = $nummer; Quelle = $quelle }
            continue
        }

        # Nur die reguläre Startzeile, nicht „freihand start_gescheitert“ (Codex zu PR #69)
        if ($rest -eq "freihand start" -or $rest.StartsWith("freihand start ")) {
            $modell = if ($rest -match '\bmodell=(\S+)') { $Matches[1] } else { "unbekannt" }
            continue
        }

        if (-not $imZeitraum) { continue }

        $code = if ($rest -eq "ereignis erkenner_neustart") { "ErkennerNeustart" } elseif ($rest -eq "ereignis erkenner_nicht_verfuegbar") { "ErkennerNichtVerfuegbar" } else { $null }
        if ($code) {
            if ($meldungen.ContainsKey($code)) { $meldungen[$code]++ } else { $meldungen[$code] = 1 }
            continue
        }

        if ($rest -eq "ereignis diktat_start") { $diktate++; continue }
        if ($rest.StartsWith("ereignis abschnitt_gehalten")) { $gehalten++; continue }
        if ($rest -match '^ereignis abschnitt_geschrieben verzug_ms=(\d+)') {
            $verzug.Add([int]$Matches[1])
            if (-not $jeModell.Contains($modell)) { $jeModell[$modell] = [pscustomobject]@{ Werte = New-Object System.Collections.Generic.List[int]; Ungueltig = 0 } }
            $jeModell[$modell].Werte.Add([int]$Matches[1])
            continue
        }
        if ($rest -eq "ereignis abschnitt_geschrieben verzug_ms=ungueltig") {
            $ungueltig++
            if (-not $jeModell.Contains($modell)) { $jeModell[$modell] = [pscustomobject]@{ Werte = New-Object System.Collections.Generic.List[int]; Ungueltig = 0 } }
            $jeModell[$modell].Ungueltig++
            continue
        }
        if ($rest -match '^ereignis meldung code=(\S+)') {
            $code = $Matches[1]
            if ($meldungen.ContainsKey($code)) { $meldungen[$code]++ } else { $meldungen[$code] = 1 }
        }
    }

    if (-not $imZeitraum) { continue }

    $ergebnis += [pscustomobject]@{
        Tag       = $datei.BaseName
        Diktate   = $diktate
        Gehalten  = $gehalten
        Meldungen = [int](($meldungen.Values | Measure-Object -Sum).Sum)
        Codes     = [pscustomobject]$meldungen
        Marken    = $marken
        Abschnitte = $verzug.Count + $ungueltig
        VerzugUngueltig = $ungueltig
        VerzugMedianMs = (Median $verzug)
        VerzugP95Ms = (Perzentil $verzug 95)
        Modelle = @($jeModell.Keys | ForEach-Object {
            [pscustomobject]@{
                Modell = $_
                Abschnitte = $jeModell[$_].Werte.Count + $jeModell[$_].Ungueltig
                VerzugUngueltig = $jeModell[$_].Ungueltig
                VerzugMedianMs = (Median $jeModell[$_].Werte)
                VerzugP95Ms = (Perzentil $jeModell[$_].Werte 95)
            }
        })
    }
}

if ($Json) {
    ConvertTo-Json -InputObject @($ergebnis) -Depth 5
    exit 0
}

if ($ergebnis.Count -eq 0) {
    Write-Host "Keine Protokolle in den letzten $Tage Tagen."
    exit 0
}

$ergebnis | Format-Table Tag, Diktate, Gehalten, Meldungen, @{ n = "Marken"; e = { @($_.Marken).Count } },
    @{ n = "Verzug Median s"; e = { if ($null -ne $_.VerzugMedianMs) { "{0:N1}" -f ($_.VerzugMedianMs / 1000) } else { "-" } } },
    @{ n = "Verzug p95 s"; e = { if ($null -ne $_.VerzugP95Ms) { "{0:N1}" -f ($_.VerzugP95Ms / 1000) } else { "-" } } },
    @{ n = "Verzug ungültig"; e = { $_.VerzugUngueltig } } -AutoSize
foreach ($tag in $ergebnis) {
    $codes = $tag.Codes.PSObject.Properties | Sort-Object Value -Descending
    if ($codes) {
        Write-Host ("{0} Meldungen: {1}" -f $tag.Tag, (($codes | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ", "))
    }

    foreach ($eintrag in @($tag.Modelle)) {
        $median = if ($null -ne $eintrag.VerzugMedianMs) { "{0:N1} s" -f ($eintrag.VerzugMedianMs / 1000) } else { "-" }
        $p95 = if ($null -ne $eintrag.VerzugP95Ms) { "{0:N1} s" -f ($eintrag.VerzugP95Ms / 1000) } else { "-" }
        Write-Host ("{0} Verzug mit {1}: Median {2}, p95 {3} ({4} Abschnitte, davon {5} ungültig)" -f $tag.Tag, $eintrag.Modell,
            $median, $p95, $eintrag.Abschnitte, $eintrag.VerzugUngueltig)
    }

    foreach ($marke in $tag.Marken) {
        Write-Host ("{0} Marke Nr. {1} ({2}) um {3}" -f $tag.Tag, $marke.Nummer, $marke.Quelle, $marke.Zeit.Substring(11, 8))
    }
}
