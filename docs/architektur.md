# Architektur

Bootrix besteht aus vier Projekten und zwei Testprojekten:

| Projekt | Inhalt |
| --- | --- |
| `Bootrix.Core` | Plattformneutrale Logik: Planer, Dateisysteme (FAT, ext), Partitionstabellen, Image-Analyse, Downloader, Katalog, Jobs, Berichte, Darstellungslogik. Läuft und wird getestet unter Linux und Windows. |
| `Bootrix.Windows` | Alles, was Windows braucht: Datenträger (IOCTL, Volumes), Formatieren, DISM, IMAPI2, Broker, Zielgeräte-Erfassung, Schreibwege. |
| `Bootrix.App` | WPF-Oberfläche (Fluent). Dieselbe EXE dient auch als erhöhter Broker (`--broker`). |
| `Bootrix.Cli` | Kommandozeile `bootrix-cli`. |

## Prozesse und Rechte

Die Oberfläche läuft ohne Administratorrechte. Alles, was Datenträger oder Systemdienste anfasst, läuft im **Broker**: derselbe Prozess, per UAC erhöht gestartet, ohne Fenster. Die Oberfläche startet ihn erst, wenn etwas Privilegiertes gebraucht wird, und spricht mit ihm über eine abgesicherte Named Pipe (nur die startende Sitzung erreicht sie; Geheimnis aus einer kurzlebigen Datei, Prüfung von Prozess-ID und Programmdatei des Clients). Die Kommandozeile läuft selbst erhöht und führt dieselben Aufträge im eigenen Prozess aus (`LocalEngine`).

Der Broker misstraut der Oberfläche:

- Jede Anfrage (`EngineJobRequest`, polymorphes JSON) wird validiert (`EngineRequestValidator`), Pfade nur in der absoluten Form ohne Geräte, Streams und `..` (`EnginePathRules`).
- Der Broker arbeitet nur in `C:\ProgramData\Bootrix`. Der Ordner gehört Administratoren und SYSTEM, sonst startet er nicht (`ProtectedFolder`).
- Dateien des Benutzers werden nie mit den Rechten des Brokers geöffnet: Abbilder über `ImpersonatingImageStreamProvider`, Quell- und Zieldateien (Tiny-Build, Protokolle) über `IUserFiles` (`ClientUserFiles` kopiert im Namen des Clients in den Arbeitsbereich und wieder hinaus).
- Zerstörende Aufträge tragen die bestätigte Identität des Datenträgers (Seriennummer, Größe, Signaturen, Hash der ersten Sektoren); der Broker vergleicht sie vor dem ersten Schreibzugriff und noch einmal nach dem Sperren.
- Passwörter und Schlüssel aus Aufträgen stehen in keinem Protokoll.

## Aufträge

Ein Auftrag besteht aus

1. einem Request-Typ in `Bootrix.Core/Engine` (Record, abgeleitet von `EngineJobRequest`, mit `[JsonDerivedType]`),
2. einer Validierung (Eintrag in `EngineRequestValidator`),
3. einem Handler (`EngineJobHandler<TRequest>`) in `Bootrix.Windows`, eingetragen in `BrokerHandlers` (für die Oberfläche) und in `WindowsServiceCollectionExtensions` (für die Kommandozeile),
4. Schritten (`IJobStep`) mit Titeltexten `Step.<Schlüssel>` in `Ui.resx` und `Ui.en.resx`.

Der `JobRunner` führt die Schritte aus (gewichteter Fortschritt, Abbruch in zwei Stufen, Aufräumaktionen in umgekehrter Reihenfolge, Journal für abgebrochene Läufe).

## Schreibwege

`WriteImageJobFactory` analysiert das Abbild (`ImageInspector`), lässt `LayoutPlanner` einen unveränderlichen `MediaPlan` bauen (Partitionstabelle, Dateisysteme, Startweg, Warnungen; die Oberfläche zeigt ihn vorab) und nimmt den ersten `IMediaWriter`, der den Plan kann: `WindowsSetupWriter`, `LinuxIsoWriter`, `DosWriter`, `FormatOnlyWriter`, `RawCopyWriter` (siehe `MediaWriters.CreateDefault`). Gemeinsame Schritte (Prüfen, Partitionieren, Abschließen) liegen in `StandardSteps`; FAT schreibt Bootrix selbst, NTFS/exFAT formatiert Windows.

## Oberfläche

`ViewModels/` hält die Logik (CommunityToolkit.Mvvm), `Views/` und `Controls/` das XAML. Texte stehen nie im Code: `Ui.resx`/`Ui.en.resx` (Deutsch/Englisch, ein Test prüft die Parität), `Strings*.resx` für Fehler und Hinweise. Darstellungslogik, die sich testen lässt (Planvorschau, Fortschritt, Geräte, Berichte), liegt in `Bootrix.Core/Presentation`.

## Tests

- `tests/Bootrix.Core.Tests`: Formate gegen unabhängige Werkzeuge (`fsck.vfat`, `sfdisk`, `e2fsck`, `xorriso`, `qemu`), wo sie vorhanden sind; fehlt ein Werkzeug, wird der Test übersprungen.
- `tests/Bootrix.Windows.Tests`: Windows-Code mit `[WindowsFact]`; unter Linux nur kompiliert.
- Die CI (`.github/workflows/ci.yml`) baut und testet unter Linux und Windows, veröffentlicht die Einzeldatei-Programme und startet die Oberfläche mit `--smoke-test` (jede Seite wird einmal geladen).

Was nur echte Hardware prüft, steht in `docs/hardware-checkliste.md`.
