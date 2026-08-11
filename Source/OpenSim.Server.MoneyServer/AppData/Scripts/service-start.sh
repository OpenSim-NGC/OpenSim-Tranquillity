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

# Handle a couple of different possible config file names.
CONFIGFILE=""
if [ -f ${CONFIGDIR}/OpenSim.Server.${SERVICENAME}.ini ]; then
    export CONFIGFILE="${CONFIGDIR}/OpenSim.Server.${SERVICENAME}.ini"
elif [ -f ${CONFIGDIR}/${SERVICENAME}.ini ]; then
    export CONFIGFILE="${CONFIGDIR}/${SERVICENAME}.ini"
fi

# Same as above for the log configuration file.  If none is found then we will use 
# the default log config file in the runtime directory generated from App.confi
export LOGCONFIG=""
if [ -f ${CONFIGDIR}/OpenSim.Server.${SERVICENAME}.dll.config ]; then
    LOGCONFIG="${CONFIGDIR}/OpenSim.Server.${SERVICENAME}.dll.config"
elif [ -f ${CONFIGDIR}/${SERVICENAME}.dll.config ]; then
    LOGCONFIG="${CONFIGDIR}/${SERVICENAME}.dll.config"
else
    LOGCONFIG="$BINDIR/OpenSim.Server.${SERVICENAME}.dll.config"
fi

if [ ! -d $BINDIR ]; then
    echo "Runtime directory $BINDIR does not exist!"
    exit 1
fi

if [ ! -f $CONFIGFILE ]; then
    echo "Cannot find configuration $CONFIGFILE to run!"
    exit 2
fi

echo "Starting service OpenSim.Server.${SERVICENAME} in directory ${BINDIR} with config ${CONFIGFILE}, Logs at ${LOGDIR}."

CMDARGS="--inifile ${CONFIGFILE} --console $CONSOLE --logconfig ${LOGCONFIG}"

(cd ${BINDIR} && screen -S "${SERVICENAME}" -d -m dotnet OpenSim.Server.$SERVICENAME.dll ${CMDARGS})

exit 0
