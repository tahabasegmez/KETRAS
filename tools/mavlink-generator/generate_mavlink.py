#!/usr/bin/env python3
"""Generates Ketras.Mavlink's MAVLink message classes, enums and registry from MAVLink XML.

Only pymavlink's XML parser (mavparse) is used, for wire ordering, CRC_EXTRA and lengths. Every
line of C# comes from the templates in this file, so the output carries no third-party code.

Usage: generate_mavlink.py <dialect.xml> <output_dir>
<include> files are followed; each message and enum is emitted once.
"""

import os
import re
import sys

from pymavlink.generator import mavparse

# MAVLink type -> (C# type, size, BinaryPrimitives suffix)
CS_TYPES = {
    'float': ('float', 4, 'Single'),
    'double': ('double', 8, 'Double'),
    'char': ('byte', 1, None),
    'int8_t': ('sbyte', 1, None),
    'uint8_t': ('byte', 1, None),
    'uint8_t_mavlink_version': ('byte', 1, None),
    'int16_t': ('short', 2, 'Int16'),
    'uint16_t': ('ushort', 2, 'UInt16'),
    'int32_t': ('int', 4, 'Int32'),
    'uint32_t': ('uint', 4, 'UInt32'),
    'int64_t': ('long', 8, 'Int64'),
    'uint64_t': ('ulong', 8, 'UInt64'),
}

# Members every generated message class already has; a field that would clash gets a "Value" suffix.
RESERVED = {'WireId', 'WireCrcExtra', 'WireMinLength', 'WireMaxLength', 'Serialize', 'Deserialize'}

# Enums in Ketras.Mavlink's public API; every other generated type is internal. Add one here when a public member needs it.
PUBLIC_ENUMS = {'AdsbAltitudeType', 'AdsbEmitterType', 'MavAutopilot', 'MavCmd',
                'MavFrame', 'MavLandedState', 'MavMissionResult', 'MavMissionType', 'MavParamType', 'MavResult',
                'MavSeverity', 'MavState', 'MavType'}


def pascal(name):
    parts = re.split(r'[_\s]+', name.strip())
    result = ''.join(p[:1].upper() + p[1:].lower() for p in parts if p)
    return '_' + result if result[:1].isdigit() else result


def xml_text(text):
    text = ' '.join((text or '').split())
    return text.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')


def base_type(field):
    return 'uint8_t' if field.type == 'uint8_t_mavlink_version' else field.type


def load(path, seen):
    path = os.path.abspath(path)
    if path in seen:
        return []
    seen.add(path)
    xml = mavparse.MAVXML(path, mavparse.PROTOCOL_2_0)
    result = [xml]
    for include in xml.include:
        result += load(os.path.join(os.path.dirname(path), include), seen)
    return result


def read_expr(field, offset):
    cs, size, prim = CS_TYPES[base_type(field)]
    if cs == 'byte':
        return f'p[{offset}]'
    if cs == 'sbyte':
        return f'(sbyte)p[{offset}]'
    return f'BinaryPrimitives.Read{prim}LittleEndian(p.Slice({offset}, {size}))'


def write_stmt(field, offset, value):
    cs, size, prim = CS_TYPES[base_type(field)]
    if cs == 'byte':
        return f'b[{offset}] = {value};'
    if cs == 'sbyte':
        return f'b[{offset}] = (byte){value};'
    return f'BinaryPrimitives.Write{prim}LittleEndian(b.Slice({offset}, {size}), {value});'


def emit_message(m):
    cls = pascal(m.name) + 'Message'
    names = {}
    for f in m.fields:
        name = pascal(f.name)
        if name in RESERVED or name == cls:
            name += 'Value'
        names[f.name] = name

    out = []
    out.append(f'/// <summary>{m.name} ({m.id}). {xml_text(m.description)}</summary>')
    out.append(f'internal sealed class {cls} : IMavlinkMessage')
    out.append('{')
    out.append(f'    public const uint WireId = {m.id};')
    out.append(f'    public const byte WireCrcExtra = {m.crc_extra};')
    out.append(f'    public const int WireMinLength = {m.wire_min_length};')
    out.append(f'    public const int WireMaxLength = {m.wire_length};')
    out.append('')
    out.append('    uint IMavlinkMessage.WireId => WireId;')
    out.append('    byte IMavlinkMessage.WireCrcExtra => WireCrcExtra;')
    out.append('    int IMavlinkMessage.WireMaxLength => WireMaxLength;')
    out.append('')

    for f in m.fields:
        cs, _, _ = CS_TYPES[base_type(f)]
        units = f' [{f.units}]' if getattr(f, 'units', '') else ''
        enum = f' Enum: {f.enum}.' if getattr(f, 'enum', '') else ''
        out.append(f'    /// <summary>{xml_text(f.description)}{units}{enum}</summary>')
        if f.array_length and f.type == 'char':
            out.append(f'    public string {names[f.name]} {{ get; set; }} = string.Empty;')
        elif f.array_length:
            out.append(f'    public {cs}[] {names[f.name]} {{ get; set; }} = new {cs}[{f.array_length}];')
        else:
            out.append(f'    public {cs} {names[f.name]} {{ get; set; }}')
        out.append('')

    # Fields go out in wire order (by type size, extensions last); the encoder trims trailing zeros.
    out.append('    public int Serialize(Span<byte> b)')
    out.append('    {')
    out.append('        b.Slice(0, WireMaxLength).Clear();')
    for f in m.ordered_fields:
        name, off = names[f.name], f.wire_offset
        if f.array_length and f.type == 'char':
            out.append(f'        MavlinkText.Write({name}, b.Slice({off}, {f.array_length}));')
        elif f.array_length:
            _, size, _ = CS_TYPES[base_type(f)]
            out.append(f'        for (var i = 0; i < {f.array_length} && i < {name}.Length; i++)')
            out.append('        {')
            out.append('            ' + write_stmt(f, f'{off} + (i * {size})', f'{name}[i]'))
            out.append('        }')
        else:
            out.append('        ' + write_stmt(f, off, name))
    out.append('        return WireMaxLength;')
    out.append('    }')
    out.append('')

    # MAVLink 2 drops trailing zero bytes and older senders omit extensions: zero-extend first.
    out.append(f'    public static {cls} Deserialize(ReadOnlySpan<byte> payload)')
    out.append('    {')
    out.append('        Span<byte> p = stackalloc byte[WireMaxLength];')
    out.append('        payload.Slice(0, Math.Min(payload.Length, WireMaxLength)).CopyTo(p);')
    out.append(f'        var m = new {cls}();')
    for f in m.ordered_fields:
        name, off = names[f.name], f.wire_offset
        if f.array_length and f.type == 'char':
            out.append(f'        m.{name} = MavlinkText.Read(p.Slice({off}, {f.array_length}));')
        elif f.array_length:
            _, size, _ = CS_TYPES[base_type(f)]
            out.append(f'        for (var i = 0; i < {f.array_length}; i++)')
            out.append('        {')
            out.append(f'            m.{name}[i] = ' + read_expr(f, f'{off} + (i * {size})') + ';')
            out.append('        }')
        else:
            out.append(f'        m.{name} = ' + read_expr(f, off) + ';')
    out.append('        return m;')
    out.append('    }')
    out.append('}')
    return cls, '\n'.join(out)


def emit_enum(e):
    name = pascal(e.name)
    prefix = e.name + '_'
    entries = [x for x in e.entry if not x.name.endswith('_ENUM_END')]
    values = [int(x.value) for x in entries]
    base = 'ulong' if values and max(values) > 0xFFFFFFFF else 'uint'
    out = []
    out.append(f'/// <summary>{e.name}. {xml_text(e.description)}</summary>')
    if getattr(e, 'bitmask', False):
        out.append('[Flags]')
    out.append(f'{"public" if name in PUBLIC_ENUMS else "internal"} enum {name} : {base}')
    out.append('{')
    used = set()
    for entry in entries:
        short = entry.name[len(prefix):] if entry.name.startswith(prefix) else entry.name
        member = pascal(short)
        if member in used:
            continue
        used.add(member)
        out.append(f'    /// <summary>{entry.name}. {xml_text(entry.description)}</summary>')
        out.append(f'    {member} = {entry.value},')
        out.append('')
    out.append('}')
    return '\n'.join(out)


HEADER = '''// <auto-generated>
// Generated by tools/mavlink-generator/generate_mavlink.py from {source}.
// Do not edit by hand; run tools/mavlink-generator/generate.sh.
// </auto-generated>
#pragma warning disable CS1591
using System.Buffers.Binary;

namespace Ketras.Mavlink.Messages;
'''


def main():
    source, out_dir = sys.argv[1], sys.argv[2]
    xmls = load(source, set())
    messages, enums = {}, {}
    for xml in xmls:
        for m in xml.message:
            messages.setdefault(m.id, m)
        for e in xml.enum:
            enums.setdefault(e.name, e)

    os.makedirs(out_dir, exist_ok=True)
    header = HEADER.format(source=os.path.basename(source))

    classes = []
    with open(os.path.join(out_dir, 'MavlinkMessages.g.cs'), 'w', newline='\n') as f:
        f.write(header + '\n')
        for mid in sorted(messages):
            cls, text = emit_message(messages[mid])
            classes.append((mid, cls, messages[mid].name))
            f.write(text + '\n\n')

    with open(os.path.join(out_dir, 'MavlinkEnums.g.cs'), 'w', newline='\n') as f:
        f.write(header + '\n')
        for name in sorted(enums):
            f.write(emit_enum(enums[name]) + '\n\n')

    with open(os.path.join(out_dir, 'MavlinkRegistry.g.cs'), 'w', newline='\n') as f:
        f.write(header + '\n')
        f.write('internal static partial class MavlinkRegistry\n{\n')
        f.write('    private static readonly Dictionary<uint, MavlinkMessageInfo> Table = new()\n    {\n')
        for mid, cls, name in classes:
            f.write(f'        [{mid}] = new("{name}", {cls}.WireCrcExtra, {cls}.WireMinLength, {cls}.WireMaxLength, {cls}.Deserialize),\n')
        f.write('    };\n}\n')

    print(f'{len(classes)} messages, {len(enums)} enums from {len(xmls)} files')


if __name__ == '__main__':
    main()
