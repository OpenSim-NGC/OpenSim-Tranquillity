#!/bin/bash

! getopt --test > /dev/null 
if [[ ${PIPESTATUS[0]} -ne 4 ]]; then
    echo "I’m sorry, `getopt --test` failed in this environment."
    exit 1
fi

OPTIONS=r:v
LONGOPTS=release:,verbose

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
    echo "$0: An web service name is required."
    exit 4
fi

SERVICENAME=$1

# If no release was specified figure out what we should use
if [ "${release}" == "" ]; then

    while read -r -a service
    do 
	if [ "${service[0]}" == "${SERVICENAME}" ]
	then
	    if test ${service[1]+_}
	    then
		    release="${service[1]}"
	    else
		    release="default"
	    fi

	    break
	fi
    done < $HOME/bin/WebServiceList.txt
fi

# 
# Options handled.  Start things up.
#

BINDIR="$HOME/release/${release}"
 
if [ ! -d $BINDIR ]; then
    echo "Runtime directory $BINDIR does not exist!"
    exit 1
fi

# Check for .env settings and if we find them load them
if [ -x ${HOME}/.env.${SERVICENAME} ]
then
    source ${HOME}/.env.${SERVICENAME} 
fi

# Start it up
echo "Starting web service ${SERVICENAME} in directory ${BINDIR}"

(cd ${BINDIR} && screen -S ${SERVICENAME} -d -m dotnet ${BINDIR}/${SERVICENAME}.dll)

exit 0
