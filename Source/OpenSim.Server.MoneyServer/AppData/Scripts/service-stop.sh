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

echo "Stopping service OpenSim.Server.${SERVICENAME}"

screen -S "${SERVICENAME}" -p 0 -X stuff "shutdown^M^M"

exit 0
