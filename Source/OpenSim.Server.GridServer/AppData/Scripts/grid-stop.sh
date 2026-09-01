#!/bin/bash

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# saner programming env: these switches turn some bugs into errors
set -o errexit -o pipefail -o noclobber -o nounset

! getopt --test > /dev/null 
if [[ ${PIPESTATUS[0]} -ne 4 ]]; then
    echo "I’m sorry, `getopt --test` failed in this environment."
    exit 1
fi

# handle non-option arguments
if [[ $# -ne 1 ]]; then
    echo "$0: A grid service name is required."
    exit 4
fi

export SERVICENAME=$1

echo "Stopping grid service OpenSim.Server.GridServer (${SERVICENAME})"

screen -S ${SERVICENAME} -p 0 -X stuff "shutdown^M"

exit 0
