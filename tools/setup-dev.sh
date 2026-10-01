#!/usr/bin/env bash
# Installs the tools the test suite uses to cross-check binary formats on Debian/Ubuntu.
set -euo pipefail

sudo apt-get update
sudo apt-get install -y dosfstools mtools gdisk fdisk e2fsprogs nasm xorriso p7zip-full qemu-system-x86 ovmf

if ! command -v dotnet >/dev/null; then
  curl -sSL https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet"
  echo 'Add $HOME/.dotnet to PATH'
fi
