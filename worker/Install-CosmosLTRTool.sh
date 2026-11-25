#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage:
  sudo ./Install-CosmosLTRTool.sh <published-output-directory> <config-json> [install-root]

Defaults:
  install-root: /opt/cosmos-ltr

Environment:
  DOTNET_PATH: dotnet host used for the install-time smoke check
USAGE
}

[[ $# -ge 2 && $# -le 3 ]] || {
  usage >&2
  exit 64
}
[[ ${EUID:-$(id -u)} -eq 0 ]] || {
  printf 'Run this installer as root or with sudo.\n' >&2
  exit 1
}

publish_directory=$(realpath "$1")
config_path=$(realpath "$2")
install_root=${3:-/opt/cosmos-ltr}
dotnet_path=${DOTNET_PATH:-$(command -v dotnet || true)}

[[ -d "$publish_directory" ]] || {
  printf 'Published output directory was not found: %s\n' "$publish_directory" >&2
  exit 1
}
[[ -f "$publish_directory/LTRTool.dll" ]] || {
  printf 'LTRTool.dll was not found in: %s\n' "$publish_directory" >&2
  exit 1
}
[[ -f "$config_path" ]] || {
  printf 'Configuration file was not found: %s\n' "$config_path" >&2
  exit 1
}
[[ -x "$dotnet_path" ]] || {
  printf 'dotnet host was not found. Set DOTNET_PATH or install .NET 10.\n' >&2
  exit 1
}

install -d -m 0750 "$install_root/publish/LTRTool"
cp -a "$publish_directory"/. "$install_root/publish/LTRTool/"
install -m 0600 "$config_path" "$install_root/config.json"

"$dotnet_path" "$install_root/publish/LTRTool/LTRTool.dll" --help >/dev/null

printf 'LTRTool installed at %s\n' "$install_root"
printf 'DotNetPath=%s\n' "$dotnet_path"
printf 'ApplicationPath=%s\n' "$install_root/publish/LTRTool/LTRTool.dll"
printf 'ConfigurationPath=%s\n' "$install_root/config.json"
