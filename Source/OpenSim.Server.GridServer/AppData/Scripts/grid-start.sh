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
CONFIGFILE=""
if [ -f ${CONFIGDIR}/GridServer.${SERVICENAME}.ini ]; then
    export CONFIGFILE="${CONFIGDIR}/GridServer.${SERVICENAME}.ini"
elif [ -f ${CONFIGDIR}/OpenSim.Server.GridServer.ini ]; then
    export CONFIGFILE="${CONFIGDIR}/OpenSim.Server.GridServer.ini"
fi

# Same as above for the log configuration file.  If none is found then we will use 
# the default log config file in the runtime directory generated from App.confi
export LOGCONFIG=""
if [ -f ${CONFIGDIR}/GridServer.${SERVICENAME}.dll.config ]; then
    export LOGCONFIG="${CONFIGDIR}/GridServer.${SERVICENAME}.dll.config"
elif [ -f ${CONFIGDIR}/OpenSim.Server.GridServer.dll.config ]; then
    export LOGCONFIG="${CONFIGDIR}/OpenSim.Server.GridServer.dll.config"
else
    export LOGCONFIG="${BINDIR}/OpenSim.Server.GridServer.dll.config"
fi

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
