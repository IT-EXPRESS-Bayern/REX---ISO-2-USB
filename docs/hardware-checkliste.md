# Hardware-Checkliste

Diese Pfade lassen sich in der CI nicht prüfen und müssen vor einem Release von Hand getestet werden.

- [ ] USB-Stick (neu, 3.x) und alter Stick (<= 1 GB, USB 1.1/2.0)
- [ ] USB-SATA-Adapter mit 4096-Byte-Sektoren
- [ ] SD-Karte im Kartenleser, Kartenleser ohne Medium
- [ ] UEFI-PC mit Secure Boot (Zertifikate 2011 und 2023)
- [ ] Legacy-BIOS-PC (CSM) und alter PC mit BIOS ohne LBA-Erweiterung
- [ ] Windows-11-Setup-Stick (FAT32 mit gesplittetem install.wim, NTFS mit UEFI:NTFS)
- [ ] Linux-Hybrid-ISO im DD-Modus und im ISO-Modus mit Persistenz
- [ ] FreeDOS-Stick auf altem PC
- [ ] DVD-Brenner (CD-R/RW, DVD+R/-R/RW, DVD+R DL, BD-R)
- [ ] USB-Floppy
- [ ] Mac (Intel ohne T2, mit T2) mit macOS-Recovery-Stick
- [ ] Windows-Setup-Stick mit Antwortdatei: lokales Konto, Computername aus `{serial}`, Zeitzone und Sprache kommen auf dem Kunden-PC an; eine autounattend.xml aus einem Fremd-ISO liegt danach als `autounattend.xml.original` auf dem Stick
- [ ] Hardware-Prüfung abschalten: Windows 11 Setup startet auf einem PC ohne TPM 2.0 und ohne Secure Boot ohne Hinweis, auch wenn die Antwortdatei des Images behalten wird (`ExistingAnswerFile = Keep`)
- [ ] DISM auf dem Stick: boot.wim wird in den Arbeitsordner kopiert, gemountet und zurückgeschrieben (FAT32 und NTFS); bei Treibern in install.wim reicht der freie Platz auf dem Stick für eine zweite Kopie des Abbilds
- [ ] `$WinPEDriver$` mit Intel-RST/VMD-Treiber: Setup findet auf einem PC der 11. bis 14. Generation die Datenträger, der Treiber ist danach im installierten System vorhanden (`pnputil /enum-drivers`)
- [ ] Treiberordner im Broker: wird mit einem anderen Administratorkonto bestätigt, liest Bootrix den Ordner nur, wenn der angemeldete Benutzer ihn lesen darf
- [ ] Windows UEFI CA 2023: Stick aus einem 25H2-Image bootet auf einem PC, dessen db nur die 2023-Zertifikate enthält; wimlib öffnet dazu `\\?\Volume{...}\sources\boot.wim` direkt

## Rohschreiben: komprimierte Images, Persistenz, Apple, Wiederherstellen, Prüfen

- [ ] Debian-/Kali-Live-ISO im DD-Modus mit Persistenz (MBR-Hybrid): Stick bootet, Persistenz-Eintrag im Bootmenü, Änderungen bleiben nach Neustart erhalten (`persistence.conf` mit `/ union`)
- [ ] Ubuntu-ISO (MBR + GPT) im DD-Modus mit Persistenz: Partition „writable“ wird von casper mit `persistent` gefunden
- [ ] Persistenz auf einem Stick mit 4096-Byte-Sektoren (USB-SATA-Adapter): muss mit `PersistenceLayoutUnsupported` abgelehnt werden, nichts darf geschrieben werden
- [ ] Windows liest die neue Tabelle nach dem Schreiben (UPDATE_PROPERTIES) ohne den Stick zu ziehen; kein „Laufwerk formatieren“-Dialog für die ext-Partition
- [ ] `.img.xz`, `.img.gz`, `.img.zst`, `.zip` (Raspberry Pi OS, ChromeOS Flex): direkt schreiben ohne vorheriges Entpacken, bei gzip/bzip2 (unbekannte Länge) mit zu kleinem Stick: Abbruch mit „Image passt nicht auf das Ziel“, keine Dauerschleife
- [ ] `.bmap` neben dem Image (Raspberry Pi OS): nur belegte Blöcke werden geschrieben, Stick bootet; mit „Lücken mit Nullen füllen“ ebenfalls
- [ ] Mac-`.dmg` (UDZO/ULFO) als Quelle: Stick bootet am Intel-Mac (Option beim Einschalten), am T2-Mac erst nach Freigabe im Startsicherheitsdienstprogramm
- [ ] Backup-GPT eines Mac-GPT-Abbilds liegt nach dem Schreiben am Ende des Sticks (macOS Festplattendienstprogramm meldet keine Tabellenwarnung)
- [ ] „Stick wiederherstellen“ nach einem Hybrid-ISO: Windows zeigt eine Partition über die volle Größe, keine alten Partitionen tauchen nach dem Neustecken wieder auf (alte Backup-GPT hinter dem Image)
- [ ] „Stick gegen ISO prüfen“: DD-Stick (Rohvergleich) und ISO-Modus-Stick (Dateivergleich) bestehen; ein absichtlich veränderter Block bzw. eine veränderte Datei wird mit Offset bzw. Dateinamen gemeldet
