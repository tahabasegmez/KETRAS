#!/bin/bash
# Regenerates src/Ketras.Mavlink/Messages/Generated from MAVLink XML (default: the common.xml of a local
# PX4-Autopilot clone; the XML is only read). Needs Python 3 with pymavlink (only its XML parser is used).
#   MAVLINK_XML=/path/to/common.xml tools/mavlink-generator/generate.sh
set -e
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
xml="${MAVLINK_XML:-$HOME/PX4-Autopilot/src/modules/mavlink/mavlink/message_definitions/v1.0/common.xml}"
python3 "$here/generate_mavlink.py" "$xml" "$here/../../src/Ketras.Mavlink/Messages/Generated"
