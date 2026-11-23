# Freihand

Freihand ist ein lokales Diktatprogramm für Windows 11 x64. Mit Strg+Alt+Y startest und beendest du die Aufnahme. Whisper erkennt Sprache auf dem Gerät. Freihand schreibt den Text in das beim Start gewählte Feld. Über Strg+Alt+D öffnest du das Diktatfenster.

## Ausgaben

| Funktion | Privatausgabe | Geschäftsausgabe |
| --- | --- | --- |
| Lokale Spracherkennung, eigene Wörter und Feldmessung | Ja | Ja |
| Markierten Text auf ausdrücklichen Wunsch mit Claude korrigieren oder glätten | Mit eigenem API-Schlüssel | Nicht enthalten |
| Zeitlich begrenzte, verschlüsselte Prüfaufnahmen | Optional | Nicht enthalten |

Beide Ausgaben entstehen aus demselben Quellcode. Die Geschäftsausgabe schliesst das Online-Projekt beim Bauen aus.

## Einrichten

Für die Geschäftsausgabe beschreibt die [Anleitung](tools/Geschaeft/LIESMICH.txt) das Einrichten, Aktualisieren und Entfernen der Programm- und Modellpakete. Die [technischen Hinweise](docs/pakete/it-merkblatt.md) erklären die Datenverarbeitung. Modelldateien werden separat bereitgestellt. Dateinamen und SHA-256-Sollwerte stehen im [Modellinventar](docs/freigabe/inventar.md).

## Bauen und testen

Voraussetzungen sind Windows x64 und das .NET-10-SDK. Die Privatausgabe ist der Standardbuild. Für die eigenständige Geschäftsausgabe den Ausgabeschalter ausdrücklich setzen.

```powershell
dotnet build Freihand.slnx -c Release
dotnet publish src/Freihand.App/Freihand.App.csproj -c Release -r win-x64 --self-contained true -p:FreihandAusgabe=Geschaeft -p:RestoreLockedMode=true
dotnet test Freihand.slnx -c Release --filter "Kategorie!=Desktop"
dotnet test tests/Freihand.App.ReviewTests -c Release --filter "Kategorie!=Desktop"
```
