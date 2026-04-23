#!/bin/bash

# Check if a file argument is provided
if [ $# -eq 0 ]; then
    echo "No file provided as an argument."
    exit 1
fi

FILE=$1

# Associative array of directories and their corresponding scripts
declare -A WATCH_DIRS_SCRIPTS=(
    ["/app/shared/content/video-input"]="/app/scripts/ingest-video.py"
    ["/app/shared/content/audio-input"]="/app/scripts/transcribe-audio.py"
)

# Function to execute the appropriate script based on the file location
execute_script() {
    local FILE="$1"

    for DIR in "${!WATCH_DIRS_SCRIPTS[@]}"; do
        if [[ "$FILE" == "$DIR/"* ]]; then
            local SCRIPT="${WATCH_DIRS_SCRIPTS[$DIR]}"
            echo "Executing script for file: $FILE" | tee -a /app/logfile.log
            python3 "$SCRIPT" "$FILE" >> /app/logfile.log 2>&1
            return
        fi
    done

    echo "No matching script found for file: $FILE" | tee -a /app/logfile.log
}

# Execute the script with the provided file
execute_script "$FILE"