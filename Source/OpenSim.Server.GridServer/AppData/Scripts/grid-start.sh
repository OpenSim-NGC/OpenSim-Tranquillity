#!/bin/bash

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# saner programming env: these switches turn some bugs into errors
set -o errexit -o pipefail -o noclobber -o nounset

# handle non-option arguments
if [[ $# -ne 1 || -z $1 ]]; then
    echo "$0: A grid service name is required."
    exit 4
fi

export SERVICENAME=$1

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

# Handle a couple of different possible config file names.
export CONFIGFILE="${CONFIGFILE:-${CONFIGDIR}/GridServer.${SERVICENAME}.ini}"

# Verify the environment
if [ ! -d "$BINDIR" ]; then
    echo "Runtime directory $BINDIR does not exist!"
    exit 1
fi

if [ ! -f "$CONFIGFILE" ]; then
    echo "Cannot find configuration $CONFIGFILE to run!"
    exit 2
fi

echo "Starting grid service OpenSim.Server.GridServer (${SERVICENAME}) in directory ${BINDIR} with config ${CONFIGFILE}"

cd "$BINDIR"
exec dotnet OpenSim.Server.GridServer.dll --inifile "$CONFIGFILE" --inidirectory "" --console "$CONSOLE"
