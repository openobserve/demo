#!/usr/bin/env bash
# Starts the browser console on http://localhost:5173.
#
# It talks to the API gateway on :6001, so start the backend first with
# ./start-platform.sh. The gateway allows this origin through CORS.
#
#   ./start-frontend.sh          # dev server on :5173
#   PORT=5180 ./start-frontend.sh
#
# If you change the port, add the new origin to the backend's allowed origins:
#   Cors__AllowedOrigins=http://localhost:5180 ./start-platform.sh
set -uo pipefail

cd "$(dirname "$0")/frontend"

PORT="${PORT:-5173}"

if [[ ! -d node_modules ]]; then
  echo "Installing dependencies..."
  npm install || exit 1
fi

# Fail with something readable instead of a Vite stack trace.
if lsof -nP -iTCP:"$PORT" -sTCP:LISTEN >/dev/null 2>&1; then
  echo "error: port $PORT is already in use by:" >&2
  lsof -nP -iTCP:"$PORT" -sTCP:LISTEN | sed 's/^/  /' >&2
  echo >&2
  echo "Either it is already serving the console (open http://localhost:$PORT)," >&2
  echo "or stop it with:  kill \$(lsof -ti TCP:$PORT -sTCP:LISTEN)" >&2
  echo "or use another port:  PORT=5180 ./start-frontend.sh" >&2
  exit 1
fi

if ! /usr/bin/curl -s -o /dev/null -m 2 http://localhost:6001/health; then
  echo "warning: the API gateway on :6001 is not responding." >&2
  echo "         Start it with ./start-platform.sh, otherwise every flow will fail." >&2
  echo >&2
fi

echo "Console on http://localhost:$PORT"
exec npm run dev -- --port "$PORT" --strictPort
