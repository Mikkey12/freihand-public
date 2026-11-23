# Technische Hinweise zur Geschäftsausgabe

Freihand erfasst nach dem Start eines Diktats Mikrofonblöcke und übergibt sie über eine lokale Named Pipe an den Erkennerprozess. Die Spracherkennung arbeitet mit lokalen Modelldateien. Der Build der Geschäftsausgabe enthält weder das Online-Projekt noch das Anthropic-SDK. Die Funktion für Prüfaufnahmen ist in dieser Ausgabe abgeschaltet.

Freihand registriert Tastenkombinationen bei Windows. Über UI Automation prüft es das gebundene Zielelement vor der Ausgabe und sendet Tastatureingaben über SendInput. Meldet Windows ein Passwortfeld oder erhöhte Rechte des Ziels, sperrt Freihand die Ausgabe. Zielprogramme unterscheiden sich darin, welche Eigenschaften sie melden und wie zuverlässig sie Eingaben übernehmen.

Die Feldmessung verwendet einen mitgelieferten Prüftext. Zum Messen ein leeres, entbehrliches Testfeld wählen. In Anwendungen mit automatischer Speicherung einen Testdatensatz verwenden.

Modelle, Einstellungen, Wörterlisten, gemessene Felder, Entwurfssicherungen und das Tagesprotokoll liegen unter %LOCALAPPDATA%\Freihand. Entwurfssicherungen werden per Windows-Datenschutz-API an das Benutzerkonto gebunden. Das Tagesprotokoll enthält technische Ereignisse und Messwerte. Wer ein Paket entfernt, kann die Benutzerdaten behalten. Die Schritte und die Prüfung der Paketprüfsummen stehen in der mitgelieferten LIESMICH.
