#!/bin/bash

# saner programming env: these switches turn some bugs into errors
set -o errexit -o pipefail -o noclobber -o nounset

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# handle non-option arguments
if [[ $# -ne 1 ]]; then
    echo "$0: A service name is required."
    exit 4
fi

export SERVICENAME=$1

export SERVER_NAME="$(hostname -f)"
export CONSOLE="local"
export BINDIR="$BASE_DIR"
export CONFIGDIR="${CONFIGDIR:-$HOME/config}"
export DATADIR="${DATADIR:-$HOME/data}"
export LOGDIR="${LOGDIR:-$HOME/data/log}"

if [ ! -d $BINDIR ]; then
    echo "Runtime directory $BINDIR does not exist!"
    exit 1
fi

if [ ! -f $CONFIGDIR ]; then
    echo "Cannot find configuration directory $CONFIGDIR to"
    exit 2
fi

export CONFIGFILE="${CONFIGFILE:-$CONFIGDIR/${SERVICENAME}.ini}"
export LOGCONFIG="${LOGCONFIG:-$BINDIR/OpenSim.Server.${SERVICENAME}.dll.config}"

if [ ! -f $CONFIGFILE ]; then
    echo "Cannot find configuration $CONFIGFILE to run!"
    exit 2
fi

echo "Starting service OpenSim.Server.${SERVICENAME} in directory ${BINDIR} with config ${CONFIGFILE}, Logs at ${LOGDIR}."

CMDARGS="--inifile ${CONFIGFILE} --console $CONSOLE --logconfig ${LOGCONFIG}"

(cd ${BINDIR} && screen -S "${SERVICENAME}" -d -m dotnet OpenSim.Server.$SERVICENAME.dll ${CMDARGS})

exit 0
