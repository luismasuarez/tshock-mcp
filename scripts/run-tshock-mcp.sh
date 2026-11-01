#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
binary="$repo/src/TShock.MCP.Server/bin/Release/net8.0/tshock-mcp"

if [[ ! -x "$binary" ]]; then
  echo "tshock-mcp: binary not found at $binary; run 'dotnet build -c Release' first." >&2
  exit 1
fi

if [[ -z "${TSHOCK_TOKEN:-}" ]]; then
  token_file="${TSHOCK_TOKEN_FILE:-$HOME/.config/tshock-mcp/token}"
  if [[ ! -r "$token_file" ]]; then
    echo "tshock-mcp: cannot read token file: $token_file" >&2
    exit 1
  fi
  TSHOCK_TOKEN="$(<"$token_file")"
  export TSHOCK_TOKEN
fi

exec "$binary" "$@"
