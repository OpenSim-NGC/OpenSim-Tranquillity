#!/bin/bash

# saner programming env: these switches turn some bugs into errors
set -o errexit -o pipefail -o noclobber -o nounset

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BASE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"

# handle non-option arguments
if [[ $# -ne 1 ]]; then
    echo "$0: A single region name is required."
    exit 4
fi

export REGIONNAME=$1

echo "Shutdown Region $REGIONNAME"

screen -S $REGIONNAME -p 0 -X stuff "shutdown^M^M"

exit 0
