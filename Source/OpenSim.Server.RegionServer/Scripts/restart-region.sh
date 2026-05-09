#!/bin/bash

SERVER_NAME="$(hostname -f)"

! getopt --test > /dev/null 
if [[ ${PIPESTATUS[0]} -ne 4 ]]; then
    echo "I’m sorry, `getopt --test` failed in this environment."
    exit 1
fi

OPTIONS=av
LONGOPTS=all,verbose

# -use ! and PIPESTATUS to get exit code with errexit set
# -temporarily store output to be able to check for errors
# -activate quoting/enhanced mode (e.g. by writing out “--options”)
# -pass arguments only via   -- "$@"   to separate them correctly
! PARSED=$(getopt --options=$OPTIONS --longoptions=$LONGOPTS --name "$0" -- "$@")
if [[ ${PIPESTATUS[0]} -ne 0 ]]; then
    # e.g. return value is 1
    #  then getopt has complained about wrong arguments to stdout
    exit 2
fi

# read getopt’s output this way to handle the quoting right:
eval set -- "$PARSED"

all=n
v=n 

# now enjoy the options in order and nicely split until we see --
while true; do
    case "$1" in
        -v|--verbose)
            v=y
            shift
            ;;
        -a|--all)
            all=y
            shift
            ;;
        --)
            shift
            break
            ;;
        *)
            echo "Programming error"
            exit 3
            ;;
    esac
done

REGIONNAME=""

# handle non-option arguments
if [ "$all" == "n" ]  && [ $# -ne 1 ]; then
    echo "$0: A single region name is required."
    exit 4
else
    REGIONNAME=$1
fi

while read -r -a region
do
    if test ${region[1]+_}
    then
        this=${region[0]}
        if [[ "${this}" =~ ^# ]]; then
            continue
        fi
        
        if [ "$all" == "y" ] || [ "${this}" == "${REGIONNAME}" ]
        then
            echo "Restarting Region: ${this}"
            sudo systemctl restart opensim-region@${this}.service
            sleep 10
        fi
    fi
done < $HOME/bin/RegionList.txt

exit 0
