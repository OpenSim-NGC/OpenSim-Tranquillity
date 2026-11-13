#!/bin/bash

# saner programming env: these switches turn some bugs into errors
set -o errexit -o pipefail -o noclobber -o nounset

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# handle non-option arguments
if [[ $# -ne 0 ]]; then
    echo "$0: No service name is required for MoneyServer."
    exit 4
fi

export SERVICENAME="MoneyServer"
if [[ -z "${SERVER_NAME:-}" ]]; then
    if ! SERVER_NAME="$(hostname -f)"; then
        echo "Cannot resolve the fully qualified hostname; using the local hostname." >&2
        SERVER_NAME="$(hostname)"
    fi
fi
export SERVER_NAME
export CONSOLE="${CONSOLE:-local}"
export BINDIR="$BASE_DIR"
export CONFIGDIR="${CONFIGDIR:-/app/config}"
export DATADIR="${DATADIR:-/app/data}"

if [ ! -d "$BINDIR" ]; then
    echo "Runtime directory $BINDIR does not exist!"
    exit 1
fi

if [ ! -d "$CONFIGDIR" ]; then
    echo "Cannot find configuration directory $CONFIGDIR"
    exit 2
fi

export CONFIGFILE="${CONFIGFILE:-$CONFIGDIR/${SERVICENAME}.ini}"

if [ ! -f "$CONFIGFILE" ]; then
    echo "Cannot find configuration $CONFIGFILE to run!"
    exit 2
fi

echo "Starting service OpenSim.Server.${SERVICENAME} in directory ${BINDIR} with config ${CONFIGFILE}"

cd "$BINDIR"
exec dotnet "OpenSim.Server.$SERVICENAME.dll" --inifile "$CONFIGFILE" --inidirectory "" --console "$CONSOLE"
