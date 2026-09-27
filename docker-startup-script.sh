#!/bin/bash
set -e

script_path=${SPORTARR_STARTUP_SCRIPT:-}
if [ -z "$script_path" ]; then
    exit 0
fi

if [[ "$script_path" != /* ]]; then
    echo "[Sportarr] ERROR: SPORTARR_STARTUP_SCRIPT must be an absolute container path" >&2
    exit 1
fi

if [ ! -f "$script_path" ]; then
    echo "[Sportarr] ERROR: Startup script not found: $script_path" >&2
    exit 1
fi

echo "[Sportarr] Running startup script: $script_path"
/bin/bash "$script_path"
echo "[Sportarr] Startup script completed"
