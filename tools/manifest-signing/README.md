# Signierte Metadaten

Katalog, Rettungsmedien-Liste, Sperrdaten und Update-Informationen holt Bootrix als signiertes Manifest
(`SignedManifestVerifier`, `src/Bootrix.Core/Net`). Ohne gültige Signatur gibt Bootrix keine Download-Adresse frei.

Der private Schlüssel liegt offline (Hardware-Token oder verschlüsselter Datenträger) und kommt nie in dieses Repository.
Im Repository liegen nur öffentliche Schlüssel und Testvektoren; die Tests erzeugen ihre Schlüssel selbst.

## Format

Die Hülle enthält die exakten signierten Bytes, damit vor der Prüfung nichts neu serialisiert werden muss:

```json
{
  "format": "bootrix-manifest-1",
  "algorithm": "ecdsa-p384-sha384-der",
  "keyId": "2026-a",
  "signed": "<Base64 der Manifest-Datei, Byte für Byte>",
  "signature": "<Base64 der DER-codierten ECDSA-Signatur über diese Bytes>"
}
```

Das Manifest selbst (die Datei, die signiert wird):

```json
{
  "format": 1,
  "channel": "catalog",
  "version": 17,
  "issuedUtc": "2026-10-01T12:00:00Z",
  "expiresUtc": "2026-10-15T12:00:00Z",
  "payload": { },
  "artifacts": [
    { "name": "linux.json", "url": "https://example.org/linux.json", "size": 48211, "sha256": "..." }
  ]
}
```

- `channel` trennt die Dokumentarten (Katalog, Sperrdaten, App-Update); der Rollback-Zähler gilt je Kanal.
- `version` steigt mit jeder Veröffentlichung. Ein Dokument mit niedrigerer Version als die höchste bereits gesehene wird abgelehnt.
- `expiresUtc` verhindert, dass ein Server veraltete Daten dauerhaft ausliefert (Freeze-Schutz). Zwei bis vier Wochen sind ein sinnvoller Zeitraum.
- `payload` ist frei; `artifacts` listet Dateien mit Größe und SHA-256, die der Downloader prüft.

## Schlüssel

ECDSA auf P-384 (läuft in .NET unter Windows und Linux ohne Zusatzpakete). Bootrix bekommt zwei öffentliche Schlüssel
eingebettet, den aktuellen und den nächsten; so lässt sich rotieren, ohne dass eine Version dem neuen Schlüssel blind vertrauen muss.

```bash
openssl ecparam -name secp384r1 -genkey -noout -out manifest-key.pem          # privat, offline aufbewahren
openssl ec -in manifest-key.pem -pubout -outform DER | openssl base64 -A      # öffentlich, für ManifestPublicKey.FromBase64
```

## Signieren

```bash
tools/manifest-signing/sign-manifest.sh manifest.json manifest-key.pem 2026-a > envelope.json
```

Die `keyId` ist der Name, unter dem der öffentliche Schlüssel in Bootrix eingebettet ist. Das Skript braucht nur `bash` und `openssl`.
`envelope.json` wird auf dem Katalog-Server veröffentlicht.
