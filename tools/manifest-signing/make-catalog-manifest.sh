#!/usr/bin/env bash
# Wraps the rescue catalog in an unsigned manifest of the channel "rescue-catalog"; sign-manifest.sh signs the result.
#
#   make-catalog-manifest.sh <rescue-catalog.json> <manifest-version> <expires-utc> > manifest.json
#
# The catalog file is embedded unchanged as the payload. The manifest version is the rollback counter of the channel
# and has to be higher than that of the previous publication; <expires-utc> looks like 2026-10-15T12:00:00Z.
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 <rescue-catalog.json> <manifest-version> <expires-utc>" >&2
  exit 2
fi

catalog=$1
version=$2
expires=$3

if [ ! -f "$catalog" ]; then
  echo "no such file: $catalog" >&2
  exit 2
fi

if ! [[ $version =~ ^[1-9][0-9]*$ ]]; then
  echo "the manifest version must be a positive integer" >&2
  exit 2
fi

if ! [[ $expires =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]]; then
  echo "the expiry must be a UTC time such as 2026-10-15T12:00:00Z" >&2
  exit 2
fi

printf '{\n  "format": 1,\n  "channel": "rescue-catalog",\n  "version": %s,\n  "issuedUtc": "%s",\n  "expiresUtc": "%s",\n  "payload": ' \
  "$version" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$expires"
cat "$catalog"
printf '\n}\n'
