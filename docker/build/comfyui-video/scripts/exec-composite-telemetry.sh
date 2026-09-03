#!/bin/bash
# Tee composite stdout to container logs (PID 1). Preserve stderr on the docker exec stream.
# Exit code is the Python process, not tee. Requires bash (pipefail); /bin/sh is dash.
# This file MUST use LF line endings. CRLF makes pipefail fail and tee look for /proc/1/fd/1\r.
set -o pipefail
"$@" | tee /proc/1/fd/1
exit $?
