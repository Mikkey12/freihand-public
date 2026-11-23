# Modell- und Bauteilinventar

Der Erkenner prüft die Modelldateien anhand fester SHA-256-Sollwerte. Die Dateien werden separat verteilt.

| Datei | SHA-256 |
| --- | --- |
| `ggml-large-v3-turbo.bin` | `1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69` |
| `ggml-large-v3-turbo-q5_0.bin` | `394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2` |
| `ggml-small.bin` | `1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b` |
| `ggml-silero-v5.1.2.bin` | `29940d98d42b91fbd05ce489f3ecf7c72f0a42f027e4875919a28fb4c04ea2cf` |

Das Geschäftspaket enthält das kleine Modell, das quantisierte Turbo-Modell und den Silero-Sprachdetektor. Die Herkunft und die Lizenzangaben der konkreten ggml-Konvertierungen sind vor einer Weiterverteilung zu prüfen.

## Dateien der Geschäftsausgabe

| Datei | Rolle | Quelle | Signatur |
| --- | --- | --- | --- |
| `Freihand.exe`, `Freihand.dll` | Bedienprozess: Symbol, Pille, Diktatfenster, Steuerung | `src/Freihand.App` | keine |
| `Freihand.Recognizer.exe` | Erkennungsprozess, von `Freihand.exe` gestartet | `src/Freihand.Recognizer` | keine |
| `Freihand.Zielprobe.exe` | Messwerkzeug für „Feld messen“, nur auf Befehl | `tools/Freihand.Zielprobe` | keine |
| `Freihand.Core.dll`, `Freihand.Adapters.dll`, `Freihand.Windows.dll` | Bibliotheken; `Freihand.Windows.dll` enthält die von CsWin32 erzeugten Windows-Aufrufe (Liste in `src/Freihand.Windows/NativeMethods.txt`) | `src/` | keine |
| `Fixtures\au01-testtext.txt` | Testtext AU-01 für das Messwerkzeug | `tests/Freihand.Windows.Tests/Fixtures` | – |
| `Freihand-Auswertung.ps1` | Auswertung des Protokolls, nur wenn der Nutzer sie startet | `tools/Beta` | keine |
| `LIESMICH.txt`, `IT-Merkblatt.md`, `Beobachtungsblatt.md`, `PRUEFSUMMEN.txt` | Begleittexte, Prüfsummen | `tools/Geschaeft`, `docs/pakete`, `tools/Beta` | – |

Nicht im Paket: das Projekt `Freihand.Online` und das Anthropic-SDK (Bedingung `FreihandAusgabe != Geschaeft` in
`Freihand.App.csproj`). Ohne sie fehlen Menüeintrag „Schlüssel“, Knöpfe und Tasten fürs Korrigieren und Glätten
(`Edition.cs`, `Shell.cs`).

## Bibliotheken und Laufzeiten

Versionen aus `packages.geschaeft.lock.json` und `Directory.Packages.props`.

| Bauteil | Version | Art | Rolle | Lizenz | Beleg |
| --- | --- | --- | --- | --- | --- |
| .NET-Laufzeit, eigenständig mitgeliefert | 10; SDK 10.0.100 mit `rollForward: latestFeature` (`global.json`); genaue Version im Paket **zu prüfen** | Laufzeit | Ausführung | MIT | [dotnet/runtime, LICENSE.TXT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| WPF (Windows Desktop) | mit .NET 10 | Laufzeit | Oberfläche | MIT | [dotnet/wpf, LICENSE.TXT](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT) |
| Windows Forms | mit .NET 10 | Laufzeit | nur das Symbol im Infobereich (`NotifyIcon`) | MIT | [dotnet/winforms, LICENSE.TXT](https://github.com/dotnet/winforms/blob/main/LICENSE.TXT) |
| Whisper.net | 1.9.1 | direkt (Erkenner) | .NET-Anbindung von whisper.cpp | MIT | Lizenzdatei im Paket; [sandrohanea/whisper.net, LICENSE](https://github.com/sandrohanea/whisper.net/blob/main/LICENSE) |
| Whisper.net.Runtime | 1.9.1 | direkt (Erkenner) | whisper.cpp für den Prozessor, nur `runtimes/win-x64/` | MIT | nuspec |
| Whisper.net.Runtime.Vulkan | 1.9.1 | direkt (Erkenner) | whisper.cpp für die Grafik über Vulkan, nur `runtimes/vulkan/win-x64/`; braucht den Vulkan-Lader des Grafiktreibers (`vulkan-1.dll`) | MIT | nuspec |
| whisper.cpp | in den beiden Laufzeitpaketen | nativ | Spracherkennung | MIT | [ggml-org/whisper.cpp, LICENSE](https://github.com/ggml-org/whisper.cpp/blob/master/LICENSE) |
| NAudio.Core | 3.1.0 | direkt (`Freihand.Adapters`) | WDL-Resampler der Aufnahme | MIT | nuspec; [naudio/NAudio, license.txt](https://github.com/naudio/NAudio/blob/master/license.txt) |
| NAudio.Wasapi | 3.1.0 | direkt (`Freihand.Windows`) | Aufnahme über WASAPI, geteilter Modus | MIT | nuspec |
| Microsoft.Extensions.AI.Abstractions | 10.2.0 | transitiv über Whisper.net | von Freihand nicht benutzt (Test `R48NoNetworkReferencesTests`) | MIT | nuspec |
| System.Numerics.Tensors | 9.0.0 | transitiv über NAudio.Core | – | MIT | nuspec |

Windows-Bestandteile ohne eigenes Paket: UI Automation, DPAPI, WASAPI, `SendInput`, `RegisterHotKey`, die
Rechtschreibprüfung von Windows (`ISpellChecker`, im Fenster „Wort lernen“). `System.Text.Json` gehört zu .NET.

## Build-Abhängigkeiten

| Bauteil | Version | Warum nicht im Paket | Lizenz |
| --- | --- | --- | --- |
| Whisper.net.Runtime.Cuda.Windows | 1.9.1 | steht in der Sperrdatei, weil der Erkenner es auch privat nutzt; `NurGeschaeftsLaufzeiten` entfernt `runtimes/cuda/`; Paketskript und CI prüfen. Nur privat | MIT (Paket); braucht das proprietäre NVIDIA CUDA Toolkit |
| Whisper.net.Runtime.Metal | 1.9.1 | transitiv über Whisper.net.Runtime, für Apple-Geräte; die Dateien anderer Systeme entfernt das Ziel, CI prüft auf `.dylib` | MIT |
| Microsoft.Windows.CsWin32 | 0.3.363 | nur zur Bauzeit (`PrivateAssets="all"`); der erzeugte Code liegt in `Freihand.Windows.dll` | MIT (nuspec) |
| Microsoft.Windows.SDK.Win32Metadata 71.0.30-preview, Microsoft.Windows.WDK.Win32Metadata 0.13.25-experimental, Microsoft.Windows.SDK.Win32Docs 0.1.42-alpha | siehe Spalte Bauteil | transitiv über CsWin32, nur zur Bauzeit (`src/Freihand.Windows/packages.lock.json`) | **zu prüfen** |

Nur private Ausgabe, in der Geschäftsausgabe gar nicht aufgelöst (steht nicht in `packages.geschaeft.lock.json`):
Anthropic 13.7.0 über `Freihand.Online` (MIT laut `Directory.Packages.props`).

## Benchmark und Tests

`tools/Freihand.Benchmark` (Machbarkeitstest) und die Testprojekte gelangen nicht ins Geschäftspaket.

| Bauteil | Version | Rolle | Lizenz |
| --- | --- | --- | --- |
| NAudio.WinMM | 3.1.0 | Audio im Messwerkzeug | MIT |
| org.k2fsa.sherpa.onnx | 2.0.1 | Parakeet im Vergleich, nur im Messwerkzeug (bringt ONNX Runtime mit) | Apache 2.0 |
| Microsoft.NET.Test.Sdk | 17.14.1 | Tests | MIT |
| xunit | 2.9.3 | Tests | Apache 2.0 |
| xunit.runner.visualstudio | 3.1.4 | Tests | Apache 2.0 |
