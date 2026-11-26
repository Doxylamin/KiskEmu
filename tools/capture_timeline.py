"""Decode a long official recording into a message timeline, both directions.

  python tools/capture_timeline.py research/local/capture-13700-<id>/capture.pcapng
  python tools/capture_timeline.py <capture.pcapng> --grep 0x8d00,0x3641 [--dir s2c] [--limit 200]

Reassembles every TCP stream to and from the game port (13328 by default) without the
bounded-prefix limits of tools/inspect_capture.py (a 10-minute official fight was 459 KB of
server stream; an hour is tens of MB), splits the wire frames, expands the 0xffff LZ4
envelopes and writes, next to the capture (``timeline-<port>/``):

- ``timeline.jsonl``: one line per message, in stream order per flow:
  {i, t (capture UTC), ms (since the first message of the capture), dir (s2c|c2s), flow (client
  port), op, name, len, body (hex, bodies up to --body-hex bytes) | file (larger bodies)}.
  ``t`` is when the last byte of the message was first seen on the wire, not when the
  application processed it.
- ``counts.json``: per flow and direction the opcode counts, reassembly diagnostics (SYN seen,
  conflicting retransmission bytes, a gap and the bytes after it, trailing partial frame).
- ``large/``: bodies above the hex limit, ``<i>-<op>.bin``.

Capture markers (AVION2, https://avion2.app: its marker key: the moment, a picture and a note on what was just done)
are read from ``--markers <file>``, or from ``<capture stem>-markers.jsonl`` beside the capture
when there is one (``session-…-markers.jsonl`` for ``session-….pcap``). Each marker,
{utc_ms, n, note, screenshot}, becomes its own timeline line at its time:
{t, ms, marker, note, screenshot, text: "== MARKER n: note (screenshot) =="} (no ``i``, ``op``);
``--grep`` prints the marker lines between the messages it shows. The marker time must come from
the clock that stamped the packets (AVION2's own recordings do; a pcap from another machine
needs its clock offset).

Reads PCAPNG and classic PCAP (microsecond or nanosecond), so AVION2's ``session-….pcap``
needs no conversion.

Names come from tools/data/opcode-names.json (the names the server sources use, from the usmap
by catalogue position or observed) and tools/protocol_knowledge.py; unnamed opcodes stay bare.
Wi-Fi link frames (multi-layer captures, linktype 105) are decapsulated when unprotected. The
raw bodies belong under ignored research/local; this tool prints hex only with --grep.
"""
import argparse
from array import array
import struct
from collections import Counter, defaultdict
from datetime import datetime, timezone
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from aion_protocol import Incomplete, lz4_block, varuint  # noqa: E402
import inspect_capture as capture  # noqa: E402

NAMES_FILE = ROOT / 'tools/data/opcode-names.json'


def load_names(path=NAMES_FILE):
    names = {}
    try:
        from protocol_knowledge import PACKETS
        names.update({op: entry[0] for op, entry in PACKETS.items()})
    except ImportError:
        pass
    if Path(path).exists():
        names.update({int(k, 16): v for k, v in json.loads(Path(path).read_text(encoding='utf-8')).items()})
    return names


def tcp_segment(frame, linktype):
    """inspect_capture.tcp_packet plus unprotected 802.11 data frames (linktype 105)."""
    if linktype != 105:
        return capture.tcp_packet(frame, linktype)
    if len(frame) < 24:
        return None, 'short_wifi'
    fc = int.from_bytes(frame[:2], 'little')
    if fc & 3 or (fc >> 2) & 3 != 2:
        return None, 'non_data_wifi'
    if fc & 0x4000:
        return None, 'protected_wifi'
    if fc & 0x400 or int.from_bytes(frame[22:24], 'little') & 15:
        return None, 'fragmented_wifi'
    size = 24 + (6 if fc & 0x300 == 0x300 else 0)
    if (fc >> 4) & 8:
        if len(frame) < size + 2:
            return None, 'short_wifi_qos'
        if frame[size] & 0x80:
            return None, 'wifi_amsdu_unsupported'
        size += 2
        if fc & 0x8000:
            size += 4
    if frame[size:size + 6] not in (b'\xaa\xaa\x03\x00\x00\x00', b'\xaa\xaa\x03\x00\x00\xf8'):
        return None, 'unsupported_wifi_llc'
    if frame[size + 6:size + 8] not in (b'\x08\x00', b'\x86\xdd'):
        return None, 'non_ip_wifi'
    return capture.tcp_packet(frame[size + 8:], 101)


def packets(path):
    """capture.pcapng_packets, or the packets of a classic PCAP file (the format AVION2 records)."""
    path = Path(path)
    with path.open('rb') as stream:
        magic = stream.read(4)
    if magic == b'\x0a\x0d\x0d\x0a':
        yield from capture.pcapng_packets(path)
        return
    formats = {b'\xd4\xc3\xb2\xa1': ('<', 1e-6), b'\xa1\xb2\xc3\xd4': ('>', 1e-6),
               b'\x4d\x3c\xb2\xa1': ('<', 1e-9), b'\xa1\xb2\x3c\x4d': ('>', 1e-9)}
    if magic not in formats:
        raise ValueError(f'{path}: neither PCAPNG nor PCAP')
    endian, resolution = formats[magic]
    with path.open('rb') as stream:
        header = stream.read(24)
        if len(header) != 24:
            raise ValueError('Truncated PCAP header')
        linktype = struct.unpack_from(endian + 'I', header, 20)[0] & 0x0fffffff
        while True:
            record = stream.read(16)
            if not record:
                return
            if len(record) != 16:
                raise ValueError('Truncated PCAP record header')
            seconds, fraction, captured, original = struct.unpack(endian + 'IIII', record)
            if captured > 16 * 1024 * 1024:
                raise ValueError('Invalid or excessively large PCAP record')
            data = stream.read(captured)
            if len(data) != captured:
                raise ValueError('Truncated PCAP record')
            yield {'timestamp': seconds + fraction * resolution, 'linktype': linktype,
                   'captured_length': captured, 'original_length': original, 'data': data}


def markers_beside(path):
    """``<capture stem>-markers.jsonl`` next to the capture, or None."""
    path = Path(path)
    candidate = path.with_name(f'{path.stem}-markers.jsonl')
    return candidate if candidate.is_file() else None


def read_markers(path):
    """[{utc_ms, n, note, screenshot}] from a markers file (one JSON object per line), in time order."""
    markers = []
    for number, line in enumerate(Path(path).read_text(encoding='utf-8-sig').splitlines(), 1):
        if not line.strip():
            continue
        try:
            entry = json.loads(line)
            markers.append({'utc_ms': int(entry['utc_ms']), 'n': int(entry.get('n', len(markers) + 1)),
                            'note': str(entry.get('note') or ''), 'screenshot': entry.get('screenshot') or None})
        except (ValueError, KeyError, TypeError) as error:
            raise ValueError(f'{path}:{number}: not a marker ({error})') from None
    return sorted(markers, key=lambda m: (m['utc_ms'], m['n']))


def marker_text(marker):
    """== MARKER n: note (screenshot) ==, leaving out what the marker doesn't have."""
    text = f"MARKER {marker['n']}" + (f": {marker['note']}" if marker['note'] else '')
    if marker['screenshot']:
        text += f" ({marker['screenshot']})"
    return f'== {text} =='


def collect_flows(path, port, max_stream_bytes):
    """{(client address, client port): {'s2c': [(position, payload, timestamp)], 'c2s': [...], 'syn': {...}}}"""
    flows = {}
    skipped = Counter()
    for packet in packets(path):
        segment, reason = tcp_segment(packet['data'], packet['linktype'])
        if reason:
            skipped[reason] += 1
            continue
        if segment['destination_port'] == port:
            key, direction = (segment['source'], segment['source_port']), 'c2s'
        elif segment['source_port'] == port:
            key, direction = (segment['destination'], segment['destination_port']), 's2c'
        else:
            skipped['other_port'] += 1
            continue
        flow = flows.setdefault(key, {'s2c': [], 'c2s': [], 'syn': {}, 'first': packet['timestamp'], 'last': packet['timestamp'],
                                      'server': segment['source'] if direction == 's2c' else segment['destination']})
        flow['last'] = packet['timestamp']
        syn = bool(segment['flags'] & 2)
        if syn and direction not in flow['syn']:
            flow['syn'][direction] = segment['sequence']
        payload = segment['payload']
        if not payload:
            continue
        segments = flow[direction]
        if len(segments) >= 4_000_000:
            raise ValueError('segment count exceeds the supported size')
        segments.append(((segment['sequence'] + syn) % 2 ** 32, payload, packet['timestamp']))
    return flows, skipped


def reassemble(segments, syn_sequence, max_stream_bytes):
    """Contiguous stream bytes from the first byte, first-arrival time per byte (as a segment index), diagnostics."""
    if not segments:
        return b'', array('I'), {'syn_observed': syn_sequence is not None, 'bytes': 0}
    reference = (syn_sequence + 1) % 2 ** 32 if syn_sequence is not None else segments[0][0]
    positioned = [(capture.serial_delta(sequence, reference), payload, index) for index, (sequence, payload, _) in enumerate(segments)]
    base = 0 if syn_sequence is not None else min(position for position, _, _ in positioned)
    size = max(position - base + len(payload) for position, payload, _ in positioned)
    if size > max_stream_bytes:
        raise ValueError(f'stream of {size} bytes exceeds --max-stream-bytes {max_stream_bytes}')
    data = bytearray(size)
    arrival = array('I', bytes(4 * size))   # segment index + 1 of the first arrival; 0 = never seen
    conflicts = 0
    for position, payload, index in positioned:
        start = position - base
        view = memoryview(arrival)[start:start + len(payload)]
        if not any(view):
            data[start:start + len(payload)] = payload
            for k in range(len(payload)):
                view[k] = index + 1
            continue
        for k, byte in enumerate(payload):
            if view[k] == 0:
                data[start + k] = byte
                view[k] = index + 1
            elif data[start + k] != byte:
                conflicts += 1
    try:
        gap = arrival.index(0)
    except ValueError:
        gap = None
    diagnostics = {'syn_observed': syn_sequence is not None, 'bytes': size, 'conflicting_bytes': conflicts,
                   'gap_at': gap, 'bytes_after_gap': (size - gap) if gap is not None else 0}
    end = size if gap is None else gap
    return bytes(data[:end]), arrival, diagnostics


def iter_frames(data):
    """(start, end, body) of each complete wire frame: varint logical length, body = opcode u16 + fields."""
    offset = 0
    while offset < len(data):
        try:
            logical, body_start = varuint(data, offset)
        except Incomplete:
            break
        if logical < 6 or logical > 16 * 1024 * 1024:
            raise ValueError(f'frame length {logical} at {offset} outside bounds')
        end = body_start + logical - 4
        if end > len(data):
            break
        yield offset, end, data[body_start:end]
        offset = end
    return


def expand(body, depth=0):
    """The messages a frame body holds: itself, or the inner frames of a 0xffff LZ4 envelope."""
    opcode = int.from_bytes(body[:2], 'little')
    if opcode != 0xffff:
        yield body
        return
    if depth > 4:
        raise ValueError('nested envelopes')
    size = int.from_bytes(body[2:6], 'little')
    decoded = lz4_block(body[6:], size)
    used = 0
    for _, end, inner in iter_frames(decoded):
        used = end
        yield from expand(inner, depth + 1)
    if used != len(decoded):
        raise ValueError(f'{len(decoded) - used} trailing bytes inside an envelope')


def decode(path, port=13328, out=None, body_hex=4096, max_stream_bytes=512 * 1024 * 1024, markers=None):
    """Write the timeline and the counts; return the counts summary.

    markers: a markers file; None = the one beside the capture if there is one; False = none.
    """
    path = Path(path)
    if markers is None:
        markers = markers_beside(path)
    out = Path(out) if out else path.parent / f'timeline-{port}'
    out.mkdir(parents=True, exist_ok=True)
    (out / 'large').mkdir(exist_ok=True)
    names = load_names()
    flows, skipped = collect_flows(path, port, max_stream_bytes)
    messages = []   # (timestamp, order, record)
    summary = {'source': str(path), 'port': port, 'skipped_packets': dict(skipped), 'flows': []}
    for order, (key, flow) in enumerate(sorted(flows.items(), key=lambda item: item[1]['first'])):
        report = {'client': f'{key[0]}:{key[1]}', 'server': flow['server'], 'client_port': key[1],
                  'first_packet_utc': iso(flow['first']), 'last_packet_utc': iso(flow['last']), 'directions': {}}
        for direction in ('c2s', 's2c'):
            segments = flow[direction]
            data, arrival, diagnostics = reassemble(segments, flow['syn'].get(direction), max_stream_bytes)
            counts = Counter()
            errors = []
            used = 0
            try:
                for start, end, body in iter_frames(data):
                    used = end
                    last_segment = max(arrival[start:end]) - 1 if end > start else 0
                    timestamp = segments[last_segment][2]
                    for message in expand(body):
                        opcode = int.from_bytes(message[:2], 'little')
                        counts[f'0x{opcode:04x}'] += 1
                        messages.append((timestamp, len(messages), {'dir': direction, 'flow': key[1], 'op': f'0x{opcode:04x}',
                                                                      'name': names.get(opcode, ''), 'len': len(message), 'raw': message}))
            except ValueError as error:
                errors.append(str(error))
            diagnostics['trailing_bytes'] = len(data) - used
            diagnostics['messages'] = sum(counts.values())
            diagnostics['errors'] = errors
            diagnostics['opcodes'] = dict(sorted(counts.items()))
            report['directions'][direction] = diagnostics
        summary['flows'].append(report)
    count = len(messages)
    first = min((item[0] for item in messages), default=0)
    last = max((item[0] for item in messages), default=0)
    marker_list = read_markers(markers) if markers else []
    outside = 0
    for marker in marker_list:
        timestamp = marker['utc_ms'] / 1000
        outside += count > 0 and not first - 1 <= timestamp <= last + 1
        # After the messages of the same instant: the key press reacts to what was on screen
        messages.append((timestamp, len(messages), {'marker': marker['n'], 'note': marker['note'],
                                                     'screenshot': marker['screenshot'], 'text': marker_text(marker)}))
    messages.sort(key=lambda item: (item[0], item[1]))
    if marker_list:
        summary['markers'] = {'file': str(markers), 'count': len(marker_list), 'outside_capture': outside}
    index = -1
    with (out / 'timeline.jsonl').open('w', encoding='utf-8') as sink:
        for timestamp, _, record in messages:
            if 'marker' in record:
                record = {'t': iso(timestamp), 'ms': round((timestamp - first) * 1000), **record}
                sink.write(json.dumps(record, ensure_ascii=False) + '\n')
                continue
            index += 1
            raw = record.pop('raw')
            record = {'i': index, 't': iso(timestamp), 'ms': round((timestamp - first) * 1000), **record}
            if len(raw) <= body_hex:
                record['body'] = raw.hex()
            else:
                file = out / 'large' / f'{index}-{record["op"]}.bin'
                file.write_bytes(raw)
                record['file'] = str(file.relative_to(out))
            sink.write(json.dumps(record) + '\n')
    summary['messages'] = count
    (out / 'counts.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf-8')
    return summary, out


def iso(timestamp):
    return datetime.fromtimestamp(timestamp, timezone.utc).isoformat(timespec='milliseconds')


def grep(out, opcodes, direction=None, limit=200, width=64):
    """Print matching timeline lines: ms, dir, op, name, len and the first bytes; capture markers always."""
    wanted = {f'0x{int(op, 16):04x}' for op in opcodes} if opcodes else None
    shown = 0
    for line in (Path(out) / 'timeline.jsonl').open(encoding='utf-8'):
        record = json.loads(line)
        if 'marker' in record:
            print(f"{record['ms']:>9} {record['text']}")
            continue
        if wanted and record['op'] not in wanted:
            continue
        if direction and record['dir'] != direction:
            continue
        body = record.get('body', '')
        print(f"{record['ms']:>9} {record['dir']} {record['op']} {record['name']:<28} {record['len']:>5} {body[:width * 2]}")
        shown += 1
        if shown >= limit:
            break


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('capture', type=Path, help='capture.pcapng, or an existing timeline-<port> directory with --grep')
    parser.add_argument('--port', type=int, default=13328)
    parser.add_argument('--out', type=Path, help='output directory (default: timeline-<port> next to the capture)')
    parser.add_argument('--body-hex', type=int, default=4096, help='bodies up to this size are inlined as hex')
    parser.add_argument('--max-stream-bytes', type=int, default=512 * 1024 * 1024)
    parser.add_argument('--markers', type=Path,
                        help='capture markers (JSONL) to merge in; default: <capture stem>-markers.jsonl beside the capture')
    parser.add_argument('--no-markers', action='store_true', help='ignore a markers file beside the capture')
    parser.add_argument('--grep', help='comma-separated opcodes to print from the timeline (decodes first unless the directory exists)')
    parser.add_argument('--dir', choices=('s2c', 'c2s'))
    parser.add_argument('--limit', type=int, default=200)
    args = parser.parse_args(argv)
    if args.capture.is_dir():
        out = args.capture
    else:
        if args.markers is not None and not args.markers.is_file():
            parser.error(f'--markers: {args.markers} not found')
        markers = False if args.no_markers else args.markers
        summary, out = decode(args.capture, args.port, args.out, args.body_hex, args.max_stream_bytes, markers)
        print(f'{summary["messages"]} messages from {len(summary["flows"])} flow(s) -> {out}')
        if 'markers' in summary:
            m = summary['markers']
            print(f"  {m['count']} marker(s) from {m['file']}"
                  + (f"; {m['outside_capture']} outside the capture's time span (other clock?)" if m['outside_capture'] else ''))
        for flow in summary['flows']:
            for direction, d in flow['directions'].items():
                print(f"  {flow['client']} {direction}: {d['messages']} messages, {d['bytes']} bytes, syn={d['syn_observed']}, "
                      f"conflicts={d.get('conflicting_bytes', 0)}, gap_at={d.get('gap_at')}, trailing={d['trailing_bytes']}"
                      + (f", errors={d['errors']}" if d['errors'] else ''))
    if args.grep is not None:
        grep(out, [op for op in args.grep.split(',') if op], args.dir, args.limit)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
