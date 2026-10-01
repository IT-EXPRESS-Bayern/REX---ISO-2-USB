# Häufige Fragen

## Warum fragt Bootrix nach Administratorrechten?

Datenträger lassen sich nur mit Administratorrechten direkt beschreiben. Die Oberfläche selbst läuft ohne diese Rechte. Erst wenn Sie einen Stick beschreiben, prüfen oder eine Windows-ISO bearbeiten, startet Bootrix einen kleinen zweiten Prozess und Windows fragt einmal nach (UAC). Dieser Prozess nimmt nur Aufträge von der Bootrix-Oberfläche desselben Benutzers an und beendet sich, wenn sie geschlossen wird. Die Kommandozeile `bootrix-cli` verlangt die Rechte gleich beim Start.

## Mein Virenscanner meldet Bootrix. Ist das ein Fehlalarm?

Möglich. Programme, die Datenträger direkt beschreiben, Boot-Code anpassen und Windows-Abbilder verändern, sehen für Heuristiken ähnlich aus wie Schadsoftware, und unsignierte Dateien ohne Reputation werden schneller beanstandet. Prüfen Sie, dass die Datei von der Release-Seite dieses Repositories stammt und die Prüfsumme in `SHA256SUMS.txt` stimmt. Signierte Releases und der Herkunftsnachweis (Build Provenance) sind vorbereitet; ob das Release signiert ist, steht in den Release-Notizen. Der Quelltext ist offen (GPL-3.0), jede Datei lässt sich nachbauen.

## Wie viele Daten sendet Bootrix?

Keine Nutzungsdaten, keine Telemetrie. Das Programm verbindet sich nur, wenn Sie selbst etwas herunterladen (Windows-ISO bei Microsoft, Linux-Abbilder bei den Anbietern, Werkzeuge wie `oscdimg` von Microsoft) oder den Katalog aktualisieren.

## Welches Abbild darf auf welchen Stick?

Bootrix zeigt vor dem Schreiben einen Plan: Partitionstabelle, Dateisysteme, Startweg und Warnungen. Wenn ein Abbild nicht auf den Stick passt, steht dort warum (zu klein, `install.wim` zu groß für FAT32 und kein Teilen möglich, 32-Bit-UEFI und so weiter). Die Voreinstellung „Automatisch“ wählt für Windows-Medien FAT32 mit geteilter `install.wim` oder NTFS mit UEFI:NTFS, je nachdem, was Rechner und Abbild brauchen.

## Was heißt MBR, GPT, BIOS und UEFI?

- **BIOS (Legacy/CSM):** Alte Rechner bis etwa 2012 starten so; sie brauchen eine MBR-Partitionstabelle.
- **UEFI:** Moderne Rechner; sie starten von FAT32 (und mit UEFI:NTFS auch von NTFS), meist mit GPT, MBR geht häufig auch.
- Wenn Sie es nicht wissen: Auf der Werkstatt-Seite „Diesen PC analysieren“ zeigt Bootrix, wie der Rechner startet, und schlägt Einstellungen vor.

## Warum bootet mein Stick nicht, obwohl er richtig beschrieben ist?

Häufige Ursachen: Secure Boot ist an und das Medium trägt kein von der Firmware akzeptiertes Zertifikat (bei Windows-Medien der Normalfall: Microsoft-Zertifikat; bei Linux der Shim des Anbieters), das BIOS startet nicht von USB, oder der Rechner kennt das Format nicht (alte BIOS-Versionen brauchen „Alt-BIOS-Anpassungen“ in den Optionen). Die Hardware-Checkliste im Repository beschreibt die geprüften Kombinationen.

## Secure Boot und das Zertifikat 2023

Die ältesten Microsoft-Zertifikate für Secure Boot laufen 2026 aus; neuere Medien enthalten einen mit dem „Windows UEFI CA 2023“ signierten Boot-Manager. Rechner ohne dieses Zertifikat in ihrer Firmware starten solche Medien nicht, ältere Rechner mit nur dem alten Zertifikat starten die bisherigen Medien. Bootrix lässt die Dateien der Quell-ISO standardmäßig unverändert (Einstellung „Automatisch“) und kann auf Wunsch den neueren Boot-Manager einsetzen.

## Warum ist Tiny11 Core nicht aktualisierbar?

Tiny11 Core entfernt den Komponentenspeicher (WinSxS), die Wiederherstellungsumgebung und weitere Teile, damit das Abbild sehr klein wird. Dadurch lassen sich danach keine Updates, Features oder Sprachen mehr einspielen. Bootrix verlangt deshalb eine ausdrückliche Bestätigung.

## Kann ich ein Windows-11-Medium für Rechner ohne TPM 2.0 machen?

Ja, in den Optionen für Windows-Medien lassen sich die Prüfungen für TPM, Secure Boot, Arbeitsspeicher, Prozessor und Laufwerk einzeln überspringen. Microsoft unterstützt solche Installationen nicht, und manche Updates können später abgelehnt werden.

## Wo liegen Protokolle?

Die Oberfläche schreibt unter `%LOCALAPPDATA%\Bootrix\logs`, die Kommandozeile und der Administrator-Prozess unter `C:\ProgramData\Bootrix\logs` (nur für Administratoren lesbar). In den Einstellungen erzeugt „Diagnosepaket erstellen“ eine ZIP-Datei mit beiden Protokollen und einer Systembeschreibung ohne Rechner- und Benutzernamen; Abbilder und Kennwörter stehen nie darin.

## Wie stelle ich einen Stick nach einem Hybrid-Abbild wieder normal her?

Auf der Seite „Werkzeuge“ den Punkt „Laufwerk wiederherstellen“ wählen (oder `bootrix-cli restore-drive`). Das löscht die Partitionstabellen sowie Anfang und Ende des Laufwerks und legt eine einzige Partition über den ganzen Datenträger an.
