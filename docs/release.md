# Release

Ein Release entsteht, wenn ein Tag der Form `v4.1.0` gepusht wird. Der Workflow `Release` baut die GUI (`Bootrix.exe`) und die Kommandozeile (`bootrix-cli.exe`) für x64 und Arm64 als einzelne, komprimierte, selbstenthaltene Dateien, startet den Rauchtest der GUI (x64), signiert (wenn eingerichtet), packt zwei ZIP-Archive mit Lizenz und Drittanbieter-Hinweisen, erzeugt `SHA256SUMS.txt` sowie je eine Stückliste (CycloneDX) für GUI und Kommandozeile, erstellt einen Herkunftsnachweis (Build Provenance) für die Archive und legt einen **Entwurf** des GitHub-Releases an. Der Entwurf wird von Hand geprüft und veröffentlicht.

Ein Lauf ohne Tag (Aktion „Run workflow“) macht dasselbe, lädt das Ergebnis aber nur als Artefakt `release-dry-run` hoch.

## Signierung

Die Signierung ist vorbereitet, aber ausgeschaltet, solange kein Konto eingerichtet ist. Vorgesehen ist die [SignPath Foundation](https://signpath.org/) (kostenlos für Open-Source-Projekte) oder Azure Artifact Signing. Für SignPath:

1. Projekt `bootrix` mit der Signierrichtlinie `release-signing` und einer Artefaktkonfiguration für die ZIP-Struktur anlegen (EXE-Dateien im Wurzelverzeichnis).
2. Im Repository die Variable `SIGNPATH_ORGANIZATION_ID` und das Secret `SIGNPATH_API_TOKEN` setzen.

Sobald die Variable gesetzt ist, läuft der Job `Signieren`, und die Archive enthalten die signierten Dateien.

## Was vor dem ersten Release noch von Hand zu tun ist

- Signierkonto einrichten (siehe oben). Ohne Signatur zeigt Windows SmartScreen Warnungen, und manche Virenscanner stufen unbekannte Dateien als verdächtig ein.
- Den Schlüssel für signierte Katalogaktualisierungen erzeugen (`tools/` enthält das Signierwerkzeug) und den öffentlichen Schlüssel in `ManifestTrust.TrustedKeys` eintragen; bis dahin bleibt der Aktualisierungskanal des Katalogs ausgeschaltet.
- Die Punkte in `docs/hardware-checkliste.md` auf echter Hardware abarbeiten.
