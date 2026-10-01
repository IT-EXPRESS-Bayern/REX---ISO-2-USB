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

## Linux-/Live-ISO im ISO-Modus (Dateien kopieren, Bootloader selbst einrichten)

- [ ] Debian-Live-ISO (Syslinux 6.x) auf MBR/FAT32 im ISO-Modus: Stick bootet am Legacy-BIOS-PC, Isolinux-Menü erscheint, Live-System startet; `ldlinux.sys` liegt als erste Datei auf dem Volume (`fsutil file queryextents` zeigt einen Bereich)
- [ ] Ubuntu-/Mint-ISO (GRUB-Menü, kein Syslinux) im ISO-Modus mit MBR: Bootloader-Wahl „GRUB“ im Protokoll, BIOS-Start zeigt das GRUB-Menü, Live-System startet; dasselbe mit GPT (BIOS-Boot-Partition) auf einem PC mit CSM
- [ ] Syslinux-4.x/5.x-ISO (z. B. ältere Rettungs-CD) im ISO-Modus: Protokoll nennt den Wechsel auf die mitgelieferte Syslinux-Version bzw. GRUB; Menü und Live-System starten, die ausgetauschten Module (`ldlinux.c32`, `menu.c32`) passen zusammen
- [ ] Stick mit 4096-Byte-Sektoren im ISO-Modus: Bootloader-Hinweis „kein BIOS-Loader möglich“, UEFI-Start funktioniert trotzdem; kein halb eingerichteter Boot-Code
- [ ] Alte BIOS-Optionen (`LegacyBiosFixes`, Partitionsstart 64 KiB, „aktive“ Partition): Syslinux-Stick startet auch am alten PC, bei dem die Standardeinstellung hängen bleibt
- [ ] Schreiben unter Windows: Volumes werden gesperrt und ausgehängt, der Boot-Code kommt roh auf die Platte, danach hängt Windows die Laufwerke wieder ein; „Kopierte Dateien prüfen“ liest vom Stick zurück und findet keinen Unterschied (auch bei einem Stick mit mehreren Partitionen)
- [ ] Persistenz Debian-/Kali-Live im ISO-Modus: ext3-Partition „persistence“ mit `persistence.conf` (`/ union`), Boot-Eintrag enthält `persistence`; eine angelegte Datei bleibt nach dem Neustart erhalten; Windows zeigt für die ext-Partition keinen Formatieren-Dialog
- [ ] Persistenz Ubuntu/Mint/Pop!_OS im ISO-Modus: ext3-Partition „writable“ bzw. „casper-rw“, Boot-Eintrag enthält `persistent`, Änderungen bleiben erhalten (Mint: Label „writable“)
- [ ] Image mit einem Label, das nicht auf FAT passt (mehr als 11 Zeichen, Kleinbuchstaben): Menüeinträge nennen das gekürzte Label, Initramfs findet das Medium (`root=live:CDLABEL=…`, `boot=casper`, `live-media`)
- [ ] NTFS oder exFAT als Hauptpartition mit Linux-ISO: UEFI:NTFS-Partition vorhanden, UEFI-Start über den Helfer; BIOS-Start über GRUB mit NTFS/exFAT-Treiber; Ubuntu auf exFAT löst vorher die Warnung aus, dass casper das Medium dort nicht findet
- [ ] UEFI-PC mit Secure Boot und Distributions-Shim (Ubuntu, Debian, Fedora, openSUSE): der kopierte `EFI\BOOT\BOOTX64.EFI` startet, die Hinweise zur Secure-Boot-Prüfung im Protokoll stimmen mit dem Ergebnis überein (gesperrter Loader → Hinweis, Start nur mit abgeschaltetem Secure Boot)
- [ ] ESXi-Installer im ISO-Modus: `-p 1` in `boot.cfg` und `efi/boot/boot.cfg`, Installer findet seine Dateien auf dem Stick
- [ ] Fedora/RHEL-ISO im ISO-Modus: Start ohne Persistenz; eine Persistenz-Anfrage endet mit einer Warnung statt mit einem Boot-Parameter; das Label in `inst.stage2=hd:LABEL=…` bzw. `root=live:LABEL=…` stimmt nach dem Umschreiben mit dem Datenträger-Label überein
## DOS, Disketten und Formatieren

- [ ] FreeDOS-Stick (FAT16, FAT32, mit und ohne Alt-BIOS-Anpassungen) auf einem alten PC mit USB-Boot; Standard-MBR und `mbr_f` (erzwingt Laufwerk 80h)
- [ ] MS-DOS-8.0-Stick (FAT16): hängt vom CHS-Geometrie-Eintrag im BPB ab; mit USB-HDD-, USB-ZIP- und USB-FDD-Modus des BIOS probieren
- [ ] MS-DOS-Download: Prüfung von Größe, SHA-256 und Signatur von `diskcopy.dll` auf einem Rechner mit Windows, danach Boot des Sticks
- [ ] 1,44-MB-Diskette mit FreeDOS und mit MS-DOS in einem echten Diskettenlaufwerk (intern und USB), Rücklesen nach dem Schreiben
- [ ] Stick ohne Partitionstabelle (Superfloppy): nur zu prüfen, wo das BIOS ihn als Diskette startet; als Festplatte (DL=80h) stoppt der FreeDOS-Kernel
- [ ] Datenträger formatieren (FAT, FAT32 bis 32 GB, exFAT, NTFS) über „Laufwerk formatieren“ auf Stick, SD-Karte und Diskette
