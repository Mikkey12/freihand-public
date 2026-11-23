# Schemas

Versionierte JSON-Schemas für Profile und Befehle als Daten (OF-01). Sie beschreiben das Austauschformat und
dienen Editoren und Prüfern; massgeblich ist die Prüfung beim Import in `src/Freihand.Core`, die strenger ist
als das Schema.

| Datei | Art (`kind`) | Inhalt |
| --- | --- | --- |
| [`profiles.v1.schema.json`](profiles.v1.schema.json) | `freihand.profiles` | Profile: Erkennungs- und Umformweg samt Endpunkt, Datenklasse, Sprache, Aufnahmeart, Audioquelle, Ausgabe, Formatregeln, Strenge (BA-01, BA-21) |
| [`profiles.v2.schema.json`](profiles.v2.schema.json) | `freihand.profiles` | Profile ab Version 2: wie Version 1, dazu Erkennungsmodus `live` oder `sentence` (Pflicht, ER-20), Umformstil `mail`, `polite`, `summary` (BA-10), persönliches Vokabular (BA-03) und Programme für den Profilwechsel (BA-02). Der Export schreibt immer Version 2 |
| [`commands.v1.schema.json`](commands.v1.schema.json) | `freihand.commands` | Befehlsliste: Wendungen, Klasse, Geltungsbereich, Wirkung (Grundentscheid 5, Anhang A) |

## Regeln

- Jedes Dokument nennt `kind` und `schemaVersion`. Ein neueres Dokument wird mit klarer Meldung abgewiesen,
  bevor ein Feld gelesen wird. Eine neue Version erhält eine neue Datei (`*.v2.schema.json`); alte Dateien
  bleiben unverändert.
- Dokumente der Version 1 liest der Import weiter und hebt sie beim Lesen auf Version 2 (Modus `live`, kein
  Vokabular, keine Programme, kein Stil); ein Export der Version 1 gibt es nicht. Ein Dokument der Version 1 mit
  einem Feld ab Version 2 wird abgewiesen: ein Dokument wird nie halb als Version 1 und halb als Version 2 gelesen.
  Die Befehle (`commands.v1.schema.json`) bleiben bei Version 1.
- Unbekannte Felder, doppelte Felder, Kommentare und nachgestellte Kommas werden abgewiesen. Ein Import gilt
  ganz oder gar nicht.
- Nur die App prüft zusätzlich: zugelassene Endpunkte (Liste beim Import), keine Geheimnisse (Schlüssel,
  Tokens, Passwörter), keine ausführbaren Inhalte (Skripte, Skript-Adressen, Platzhalter), keine Steuer- oder
  Richtungszeichen, eindeutige Bezeichner und Wendungen, keine doppelten Vokabulareinträge und Programme (ein
  Programm gehört höchstens zu einem Profil).
- Die Tests unter `tests/Freihand.Core.Tests/Profiles` und `.../Commands` gleichen Schema und Code ab: gleiche
  Aufzählungen, eingebaute Daten und Exporte erfüllen das Schema, und jeder Fehler, den das Schema ausdrücken
  kann, wird auch vom Schema erkannt.
