#!/bin/bash

# saner programming env: these switches turn some bugs into errors
set -o errexit -o pipefail -o noclobber -o nounset

! getopt --test > /dev/null 
if [[ ${PIPESTATUS[0]} -ne 4 ]]; then
    echo "I’m sorry, `getopt --test` failed in this environment."
    exit 1
fi

OPTIONS=r:d:m:v
LONGOPTS=release:,delay:,message:,verbose

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

release=""
message="Maintenance Restart"
delay=60
v=n 

# now enjoy the options in order and nicely split until we see --
while true; do
    case "$1" in
        -v|--verbose)
            v=y
            shift
            ;;
        -r|--release)
            release="$2"
            shift 2
            ;;
        -d|--delay)
            delay=$2
            shift 2
            ;;
        -m|--message)
            message="$2"
            shift 2
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

# handle non-option arguments
if [[ $# -ne 1 ]]; then
    echo "$0: A single region name is required."
    exit 4
fi

REGIONNAME=$1

# If no release was specified figure out what we should use
if [ "${release}" == "" ]; then

    while read -r -a region
    do 
	if [ "${region[0]}" == "${REGIONNAME}" ]
	then
	    if test ${region[1]+_}
	    then
		release="${region[1]}"
	    else
		release="opensim"
	    fi

	    break
	fi
    done < $HOME/bin/RegionList.txt
fi

# 
# Options handled.  Shut things down.
#

BINDIR="$HOME/release/${release}"
CONFIGDIR="$HOME/config"
CONFIGFILE="$HOME/config/OpenSim.ini"
DEFAULTCONFIG="$HOME/config/OpenSimDefaults.ini"
REGIONCONFIG="${CONFIGDIR}/regions/${REGIONNAME}"
CONSOLE="basic"

if [ ! -d $BINDIR ]; then
    echo "Runtime directory $BINDIR does not exist!"
    exit 1
fi

if [ ! -d $REGIONCONFIG ]; then
    echo "Region configuration $REGIONCONFIG not found!"
    exit 2
fi

if [ ! -f $CONFIGFILE ]; then
    echo "Cannot find configuration $CONFIGFILE to run!"
    exit 2
fi

echo "Stopping Region $REGIONNAME in directory $BINDIR with config $REGIONCONFIG"

command="region restart bluebox \"${message}\" ${delay}^M"

screen -S $REGIONNAME -p 0 -X stuff "${command}"

exit 0
