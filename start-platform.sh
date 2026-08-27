#!/usr/bin/env bash
# Starts the whole dotnet_demo platform:
#   15 ASP.NET Core services on the .NET 6 runtime (one process each)
#   1 legacy service on .NET Framework 4.7.2 (executed by Mono on macOS/Linux)
#
#   ./start-platform.sh              # start everything
#   ./start-platform.sh --no-traffic # start without the synthetic traffic driver
#   ./stop-platform.sh               # stop everything
#
# Runtimes are pinned. Every project sets RollForward=Disable, so a .NET 6 app will
# refuse to start on .NET 9 rather than silently rolling forward. That means the
# .NET 6 host has to be used explicitly, because the system "dotnet" on this machine
# is the 9.x SDK and ships only the 9.x runtime.
#
#   DOTNET6   path to a dotnet host with the 6.0 runtime (default ~/.dotnet6/dotnet)
#   MONO      path to the mono runtime          (default: mono on PATH)
#
# Logs land in ./logs/<service>.log
set -uo pipefail

cd "$(dirname "$0")"

LOG_DIR="./logs"
PID_FILE="./.platform-pids"
mkdir -p "$LOG_DIR"

DOTNET6="${DOTNET6:-$HOME/.dotnet6/dotnet}"
MONO="${MONO:-$(command -v mono 2>/dev/null || echo mono)}"

if [[ "${1:-}" == "--no-traffic" ]]; then
  export PLATFORM_TRAFFIC=off
fi

# ---- runtime preflight -----------------------------------------------------
if [[ ! -x "$DOTNET6" ]]; then
  echo "error: .NET 6 host not found at $DOTNET6" >&2
  echo "       Install it with:" >&2
  echo "         curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- \\" >&2
  echo "           --channel 6.0 --runtime aspnetcore --install-dir \$HOME/.dotnet6 --no-path" >&2
  echo "       or point DOTNET6 at an existing one." >&2
  exit 1
fi

if ! "$DOTNET6" --list-runtimes 2>/dev/null | grep -q 'Microsoft.AspNetCore.App 6\.'; then
  echo "error: $DOTNET6 has no ASP.NET Core 6.x runtime:" >&2
  "$DOTNET6" --list-runtimes 2>&1 | sed 's/^/  /' >&2
  exit 1
fi

if ! command -v "$MONO" >/dev/null 2>&1; then
  echo "error: mono not found (needed to run the .NET Framework 4.7.2 service)." >&2
  echo "       Install it with:  brew install mono   (or set MONO=/path/to/mono)" >&2
  exit 1
fi

if [[ -f "$PID_FILE" ]] && kill -0 "$(head -1 "$PID_FILE" 2>/dev/null)" 2>/dev/null; then
  echo "error: platform already running (see $PID_FILE). Run ./stop-platform.sh first." >&2
  exit 1
fi
: > "$PID_FILE"

# Retry dropped OTLP batches in memory instead of losing them on a transient
# 5xx from the ingest endpoint (OpenTelemetry .NET experimental feature).
export OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY="${OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY:-in_memory}"

echo "Runtimes"
echo "  .NET 6 host : $DOTNET6 ($("$DOTNET6" --list-runtimes | grep 'NETCore.App 6' | awk '{print $2}'))"
echo "  Mono        : $MONO ($("$MONO" --version 2>/dev/null | head -1 | awk '{print $5}'))"
echo

echo "Building..."
dotnet build src/dotnet_demo.Platform/dotnet_demo.Platform.csproj -v q || exit 1
dotnet build src/dotnet_demo.Legacy.MainframeAdapter/dotnet_demo.Legacy.MainframeAdapter.csproj -v q || exit 1

PLATFORM_DLL="src/dotnet_demo.Platform/bin/Debug/net6.0/dotnet_demo.Platform.dll"
LEGACY_EXE="src/dotnet_demo.Legacy.MainframeAdapter/bin/Debug/net472/dotnet_demo.Legacy.MainframeAdapter.exe"

SERVICES=(
  dotnet_demo-api-gateway
  dotnet_demo-auth-service
  dotnet_demo-member-service
  dotnet_demo-provider-service
  dotnet_demo-claims-intake
  dotnet_demo-claims-validation
  dotnet_demo-eligibility-service
  dotnet_demo-benefits-service
  dotnet_demo-pricing-service
  dotnet_demo-adjudication-service
  dotnet_demo-payment-service
  dotnet_demo-notification-service
  dotnet_demo-audit-service
  dotnet_demo-document-service
  dotnet_demo-reporting-service
)

start_one() {
  local name="$1"; shift
  echo "  starting $name"
  "$@" > "$LOG_DIR/$name.log" 2>&1 &
  echo "$! $name" >> "$PID_FILE"
}

echo
echo "Starting legacy tier (.NET Framework 4.7.2 on Mono)"
start_one dotnet_demo-legacy-mainframe-adapter "$MONO" "$LEGACY_EXE"

echo
echo "Starting ${#SERVICES[@]} platform services (.NET 6)"
# Dependencies first, entry point last, so the gateway's traffic driver finds a live graph.
for ((i=${#SERVICES[@]}-1; i>=0; i--)); do
  start_one "${SERVICES[$i]}" "$DOTNET6" "$PLATFORM_DLL" --service "${SERVICES[$i]}"
done

echo
echo "Waiting for services to become healthy..."
ports=(6016 6001 6002 6003 6004 6005 6006 6007 6008 6009 6010 6011 6012 6013 6014 6015)
deadline=$((SECONDS + 120))
unhealthy=()
for p in "${ports[@]}"; do
  until /usr/bin/curl -s -o /dev/null -m 2 "http://localhost:$p/health"; do
    if (( SECONDS > deadline )); then
      unhealthy+=("$p")
      break
    fi
    sleep 1
  done
done

echo
if (( ${#unhealthy[@]} > 0 )); then
  echo "warning: no health response from port(s): ${unhealthy[*]} - check $LOG_DIR/" >&2
else
  echo "All 16 services healthy."
fi

echo
echo "  gateway      http://localhost:6001/claims/submit"
echo "  catalog      $DOTNET6 $PLATFORM_DLL --list"
echo "  logs         $LOG_DIR/"
echo "  stop         ./stop-platform.sh"
echo
if [[ "${PLATFORM_TRAFFIC:-}" != "off" ]]; then
  echo "The gateway drives one synthetic claim flow every 6s; traces are flowing to OpenObserve."
fi
