#!/bin/bash

# saner programming env: these switches turn some bugs into errors
set -o errexit -o pipefail -o noclobber -o nounset

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# handle non-option arguments
if [[ $# -ne 1 ]]; then
    echo "$0: A region name is required."
    exit 4
fi

export REGIONNAME=$1

export SERVER_NAME="$(hostname -f)"
export CONSOLE="local"
export BINDIR="$BASE_DIR"
export CONFIGDIR="${CONFIGDIR:-$HOME/config}"
export DATADIR="${DATADIR:-$HOME/data}"
export LOGDIR="${LOGDIR:-$HOME/data/log}"
export REGIONCONFIG="${REGIONCONFIG:-${CONFIGDIR}/regions/${REGIONNAME}}"

if [ ! -d $BINDIR ]; then
    echo "Runtime directory $BINDIR does not exist!"
    exit 1
fi

if [ ! -d $CONFIGDIR ]; then
    echo "Cannot find configuration directory $CONFIGDIR"
    exit 2
fi

if [ ! -d $REGIONCONFIG ]; then
    echo "Cannot find region configuration directory $REGIONCONFIG"
    exit 2
fi

export CONFIGFILE="${CONFIGFILE:-${CONFIGDIR}/OpenSim.Server.RegionServer.ini}"
export LOGCONFIG="${LOGCONFIG:-${REGIONCONFIG}/OpenSim.Server.RegionServer.dll.config}"
export DEFAULTCONFIG="${DEFAULTCONFIG:-${BINDIR}/OpenSimDefaults.ini}"

if [ ! -d $REGIONCONFIG ]; then
    echo "Region configuration $REGIONCONFIG not found!"
    exit 2
fi

echo "Starting Region $REGIONNAME in directory $BINDIR with config $REGIONCONFIG Logs at ${LOGDIR}."

CMDARGS="--inimaster $DEFAULTCONFIG --inifile $CONFIGFILE --inidirectory $REGIONCONFIG --console $CONSOLE --logconfig ${LOGCONFIG}"

(cd ${BINDIR} && screen -S "${REGIONNAME}" -d -m dotnet OpenSim.Server.RegionServer.dll ${CMDARGS})

exit 0
