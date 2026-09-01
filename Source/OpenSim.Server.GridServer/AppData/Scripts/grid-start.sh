#!/bin/bash

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# saner programming env: these switches turn some bugs into errors
set -o errexit -o pipefail -o noclobber -o nounset

# handle non-option arguments
if [[ $# -ne 1 ]]; then
    echo "$0: A grid service name is required."
    exit 4
fi

export SERVICENAME=$1

export SERVER_NAME="$(hostname -f)"
export CONSOLE="local"
export BINDIR="$BASE_DIR"
export CONFIGDIR="${CONFIGDIR:-$HOME/config}"
export DATADIR="${DATADIR:-$HOME/data}"
export LOGDIR="${LOGDIR:-$HOME/data/log}"

# Handle a couple of different possible config file names.
export CONFIGFILE="${CONFIGFILE:-${CONFIGDIR}/GridServer.${SERVICENAME}.ini}"
export LOGCONFIG="${LOGCONFIG:-${CONFIGDIR}/GridServer.${SERVICENAME}.dll.config}"

# Verify the environment
if [ ! -d $BINDIR ]; then
    echo "Runtime directory $BINDIR does not exist!"
    exit 1
fi

if [ ! -f $CONFIGFILE ]; then
    echo "Cannot find configuration $CONFIGFILE to run!"
    exit 2
fi

echo "Starting grid service OpenSim.Server.GridServer (${SERVICENAME}) in directory ${BINDIR} with config ${CONFIGFILE}"

CMDARGS="--inifile ${CONFIGFILE} --console $CONSOLE --logconfig ${LOGCONFIG}"

(cd ${BINDIR} && screen -S ${SERVICENAME} -d -m dotnet OpenSim.Server.GridServer.dll ${CMDARGS})

exit 0
