#!/usr/bin/env bash
# Signs a Bootrix manifest and writes the envelope that SignedManifestVerifier accepts to stdout.
#
#   sign-manifest.sh <manifest.json> <private-key.pem> <key-id> > envelope.json
#
# The manifest file is signed byte for byte and embedded unchanged (base64), so it must be final before this runs.
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <manifest.json> <private-key.pem> <key-id>" >&2
  exit 2
fi

manifest=$1
key=$2
key_id=$3

if ! [[ $key_id =~ ^[A-Za-z0-9._-]+$ ]]; then
  echo "key id may only contain letters, digits, '.', '-' and '_'" >&2
  exit 2
fi

signature=$(mktemp)
trap 'rm -f "$signature"' EXIT

# ECDSA P-384 over SHA-384; openssl writes the signature as a DER sequence.
openssl dgst -sha384 -sign "$key" -out "$signature" "$manifest"

printf '{\n  "format": "bootrix-manifest-1",\n  "algorithm": "ecdsa-p384-sha384-der",\n  "keyId": "%s",\n  "signed": "%s",\n  "signature": "%s"\n}\n' \
  "$key_id" \
  "$(openssl base64 -A -in "$manifest")" \
  "$(openssl base64 -A -in "$signature")"
