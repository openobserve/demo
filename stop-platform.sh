#!/usr/bin/env bash
# Stops every process started by start-platform.sh.
set -uo pipefail

cd "$(dirname "$0")"
PID_FILE="./.platform-pids"

if [[ ! -f "$PID_FILE" ]]; then
  echo "No $PID_FILE — nothing recorded as running."
else
  while read -r pid name; do
    [[ -z "${pid:-}" ]] && continue
    if kill -0 "$pid" 2>/dev/null; then
      kill "$pid" 2>/dev/null && echo "  stopped $name ($pid)"
    fi
  done < "$PID_FILE"
  rm -f "$PID_FILE"
fi

# Anything left holding a platform port (e.g. started by hand).
for p in $(seq 6001 6016); do
  pid=$(lsof -ti TCP:$p -sTCP:LISTEN 2>/dev/null | head -1)
  if [[ -n "$pid" ]]; then
    kill "$pid" 2>/dev/null && echo "  stopped stray listener on :$p ($pid)"
  fi
done

echo "Platform stopped."
