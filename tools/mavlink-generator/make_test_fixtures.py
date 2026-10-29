#!/usr/bin/env python3
"""Writes reference MAVLink 2 frames encoded by pymavlink, which tests/Ketras.Tests (Mavlink/FramingTests) compares byte for byte
with Ketras.Mavlink's encoder.

Usage: make_test_fixtures.py <output.json>
NaN is written as 0x7FC00000 (Python's quiet NaN); the C# side uses the same bit pattern.
"""

import json
import math
import sys

from pymavlink.dialects.v20 import common as mavlink


class Sink:
    def write(self, data):
        pass


def frames():
    mav = mavlink.MAVLink(Sink(), srcSystem=255, srcComponent=190)
    mav.seq = 7
    nan = math.nan
    yield 'HEARTBEAT', mav.heartbeat_encode(6, 8, 0, 0, 4, 3), {}
    yield 'COMMAND_LONG', mav.command_long_encode(1, 1, 22, 0, nan, 0, 0, nan, nan, nan, 120.5), {}
    yield 'COMMAND_LONG_ZEROS', mav.command_long_encode(1, 1, 400, 0, 1, 0, 0, 0, 0, 0, 0), {}
    yield 'MISSION_ITEM_INT', mav.mission_item_int_encode(1, 1, 3, 6, 16, 0, 1, 0, 0, 0, nan, 473977418, 85455939, 100.0, 0), {}
    yield 'PARAM_SET', mav.param_set_encode(1, 1, b'NAV_LOITER_RAD', 80.0, 9), {}
    yield 'STATUSTEXT', mav.statustext_encode(4, b'Preflight check failed', 0, 0), {}
    payload = bytes(range(251))
    yield 'FILE_TRANSFER_PROTOCOL', mav.file_transfer_protocol_encode(0, 1, 1, payload), {}


def main():
    out = []
    mav = mavlink.MAVLink(Sink(), srcSystem=255, srcComponent=190)
    for name, message, _ in frames():
        mav.seq = 7
        out.append({'name': name, 'hex': message.pack(mav).hex()})
    with open(sys.argv[1], 'w', newline='\n') as f:
        json.dump(out, f, indent=2)
        f.write('\n')
    print(f'{len(out)} frames')


if __name__ == '__main__':
    main()
