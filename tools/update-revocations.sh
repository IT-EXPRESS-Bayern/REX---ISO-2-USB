#!/usr/bin/env bash
# Rebuilds src/Bootrix.Core/Boot/revocations.json from the public sources:
#   - microsoft/secureboot_objects: DBX hashes, revoked certificates, Windows SVNs, CA certificates
#   - rhboot/shim: SbatLevel_Variable.txt (newest SBAT revocation level)
#
# Usage: tools/update-revocations.sh [secureboot_objects-tag] [shim-ref]
# Without arguments the newest v* release tag of secureboot_objects and the shim default branch are used.
# Needs bash, curl, git, jq, openssl and sha256sum (GNU coreutils; on macOS use coreutils or shasum -a 256).
set -euo pipefail

SBO_REPO=microsoft/secureboot_objects
SHIM_REPO=rhboot/shim
OUT="$(cd "$(dirname "$0")/.." && pwd)/src/Bootrix.Core/Boot/revocations.json"

for tool in curl git jq openssl sha256sum; do
  command -v "$tool" >/dev/null || { echo "missing tool: $tool" >&2; exit 1; }
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

fetch() { curl -fsSL --retry 3 --retry-delay 2 -o "$2" "$1"; }

sbo_tag="${1:-}"
if [[ -z "$sbo_tag" ]]; then
  sbo_tag="$(git ls-remote --tags --refs "https://github.com/$SBO_REPO" 'v*' \
    | sed 's#.*refs/tags/##' | grep -v -- '-signed$' | sort -V | tail -n 1)"
fi
# Peeled commit for annotated tags, plain tag target otherwise.
sbo_commit="$(git ls-remote "https://github.com/$SBO_REPO" "refs/tags/$sbo_tag" "refs/tags/$sbo_tag^{}" | tail -n 1 | cut -f1)"
[[ -n "$sbo_commit" ]] || { echo "tag $sbo_tag not found in $SBO_REPO" >&2; exit 1; }

shim_ref="${2:-HEAD}"
shim_commit="$(git ls-remote "https://github.com/$SHIM_REPO" "$shim_ref" | head -n 1 | cut -f1)"
[[ -n "$shim_commit" ]] || { echo "ref $shim_ref not found in $SHIM_REPO" >&2; exit 1; }

# Pin to the commit so the recorded hashes match the recorded revision.
sbo_raw="https://raw.githubusercontent.com/$SBO_REPO/$sbo_commit"
dbx_path="PreSignedObjects/DBX/dbx_info_msft_latest.json"
fetch "$sbo_raw/$dbx_path" "$work/dbx.json"
fetch "https://raw.githubusercontent.com/$SHIM_REPO/$shim_commit/SbatLevel_Variable.txt" "$work/sbat.txt"

# The newest level is the last "sbat,1,<date>" block; every line after it up to the first non-entry line belongs to it.
LC_ALL=C awk '
  /^sbat,1,[0-9]+$/ { n = 0; collecting = 1; lines[n++] = $0; next }
  collecting && /^[A-Za-z0-9._-]+,[0-9]+$/ { lines[n++] = $0; next }
  { collecting = 0 }
  END { for (i = 0; i < n; i++) print lines[i] }
' "$work/sbat.txt" > "$work/sbat-level.txt"
[[ "$(wc -l < "$work/sbat-level.txt")" -ge 2 ]] || { echo "no SBAT level found" >&2; exit 1; }

# Only the DB certificates are needed: they are the CAs that sign boot binaries.
cert_files=(
  MicWinProPCA2011_2011-10-19.der
  MicCorUEFCA2011_2011-06-27.der
  "windows uefi ca 2023.der"
  "microsoft uefi ca 2023.der"
  "microsoft option rom uefi ca 2023.der"
)
: > "$work/certs.ndjson"
for name in "${cert_files[@]}"; do
  fetch "$sbo_raw/PreSignedObjects/DB/Certificates/${name// /%20}" "$work/cert.der"
  jq -nc \
    --arg name "$(openssl x509 -inform DER -in "$work/cert.der" -noout -subject -nameopt multiline | sed -n 's/^ *commonName *= *//p')" \
    --arg sha1 "$(openssl x509 -inform DER -in "$work/cert.der" -noout -fingerprint -sha1 | cut -d= -f2 | tr -d ':' | tr 'A-F' 'a-f')" \
    --arg sha256 "$(openssl x509 -inform DER -in "$work/cert.der" -noout -fingerprint -sha256 | cut -d= -f2 | tr -d ':' | tr 'A-F' 'a-f')" \
    --arg der "$(openssl base64 -A -in "$work/cert.der")" \
    '{name: $name, sha1: $sha1, sha256: $sha256, der: $der}' >> "$work/certs.ndjson"
done

emit() {
  # emit <key> <file with one JSON value per line> <trailing comma yes/no>
  printf '  "%s": [\n' "$1"
  sed 's/^/    /; $!s/$/,/' "$2"
  if [[ "$3" == yes ]]; then printf '  ],\n'; else printf '  ]\n'; fi
}

jq -c '.images | to_entries[] | .key as $m | .value[]
  | {hash: (.authenticodeHash | ascii_upcase), machine: $m, file: .filename, company: .companyName,
     added: .dateOfAddition, description: (.description // "")}
  | if .description == "" then del(.description) else . end' "$work/dbx.json" > "$work/images.ndjson"
jq -c '.certificates[]
  | {subject: .subjectName, sha1: (.thumbprint | ascii_downcase), added: .dateOfAddition, description: (.description // "")}' \
  "$work/dbx.json" > "$work/revoked-certs.ndjson"
jq -c '.svns[]
  | {component: .filename, guid: (.guid | capture("\\{(?<g>[0-9a-fA-F-]+)\\}").g | ascii_downcase), version: .version,
     changed: .dateOfLastChange, description: (.description // "")}' "$work/dbx.json" > "$work/svns.ndjson"

sbat_date="$(head -n 1 "$work/sbat-level.txt" | cut -d, -f3)"
sbat_components="$(tail -n +2 "$work/sbat-level.txt" | jq -Rn '[inputs | split(",") | {(.[0]): (.[1] | tonumber)}] | add')"

{
  printf '{\n  "schemaVersion": 1,\n  "retrieved": "%s",\n' "$(date -u +%Y-%m-%d)"
  printf '  "sources": [\n'
  jq -nc --arg repo "$SBO_REPO" --arg ref "$sbo_tag" --arg commit "$sbo_commit" --arg file "$dbx_path" \
    --arg sha "$(sha256sum "$work/dbx.json" | cut -d' ' -f1)" --arg license "BSD-2-Clause-Patent" \
    '{repository: $repo, ref: $ref, commit: $commit, file: $file, sha256: $sha, license: $license}' | sed 's/^/    /; s/$/,/'
  jq -nc --arg repo "$SHIM_REPO" --arg ref "$shim_ref" --arg commit "$shim_commit" --arg file "SbatLevel_Variable.txt" \
    --arg sha "$(sha256sum "$work/sbat.txt" | cut -d' ' -f1)" --arg license "BSD-2-Clause" \
    '{repository: $repo, ref: $ref, commit: $commit, file: $file, sha256: $sha, license: $license}' | sed 's/^/    /'
  printf '  ],\n'
  printf '  "sbat": %s,\n' "$(jq -nc --arg level "$sbat_date" --argjson c "$sbat_components" '{level: $level, components: $c}')"
  emit images "$work/images.ndjson" yes
  emit revokedCertificates "$work/revoked-certs.ndjson" yes
  emit svns "$work/svns.ndjson" yes
  emit trustedCertificates "$work/certs.ndjson" no
  printf '}\n'
} > "$work/revocations.json"

jq empty "$work/revocations.json"
mv "$work/revocations.json" "$OUT"
echo "wrote $OUT ($(jq '.images | length' "$OUT") image hashes, secureboot_objects $sbo_tag, SBAT level $sbat_date)"
