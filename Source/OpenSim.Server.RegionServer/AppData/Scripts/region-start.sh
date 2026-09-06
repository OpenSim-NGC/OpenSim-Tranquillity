#!/bin/bash

# saner programming env: these switches turn some bugs into errors
set -o errexit -o pipefail -o noclobber -o nounset

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

export SERVER_NAME="$(hostname -f)"

# handle non-option arguments
if [[ $# -ne 1 ]]; then
    echo "$0: A region name is required."
    exit 4
fi

export REGIONNAME=$1

export CONSOLE="local"
export BINDIR="$BASE_DIR"
export CONFIGDIR="${CONFIGDIR:-$HOME/config}"
export DATADIR="${DATADIR:-$HOME/data}"
export LOGDIR="${LOGDIR:-$HOME/data/log}"
export CONFIGFILE="${CONFIGFILE:-${CONFIGDIR}/RegionServer.ini}"
export DEFAULTCONFIG="${DEFAULTCONFIG:-${BINDIR}/OpenSimDefaults.ini}"

if [ ! -d $BINDIR ]; then
    echo "Runtime directory $BINDIR does not exist!"
    exit 1
fi

if [ ! -d $CONFIGDIR ]; then
    echo "Cannot find configuration directory $CONFIGDIR"
    exit 2
fi

if [ ! -d $DATADIR ]; then
    echo "Cannot find data directory $DATADIR"
    exit 2
fi

if [ ! -d $LOGDIR ]; then
    echo "Cannot find log directory $LOGDIR"
    exit 2
fi

if [ ! -f $CONFIGFILE ]; then
    echo "Cannot find Region Config File $CONFIGFILE"
    exit 2
fi

if [ ! -f $DEFAULTCONFIG ]; then
    echo "Cannot find Region Default Configuration $DEFAULTCONFIG"
    exit 2
fi

export REGIONDIR="${REGIONDIR:-${CONFIGDIR}/regions/${REGIONNAME}}"
export LOGCONFIG="${LOGCONFIG:-${REGIONDIR}/RegionServer.dll.config}"

if [ ! -d $REGIONDIR ]; then
    echo "Region configuration at $REGIONDIR not found!"
    exit 2
fi

CMDARGS="--inimaster $DEFAULTCONFIG --inifile $CONFIGFILE --inidirectory $REGIONDIR --console $CONSOLE --logconfig ${LOGCONFIG}"

(cd ${BINDIR} && screen -S "${REGIONNAME}" -d -m dotnet OpenSim.Server.RegionServer.dll ${CMDARGS})

exit 0
