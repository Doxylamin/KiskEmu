"""Official NPC and object placements from server->client captures, for tools/npc_placement_audit.py.

AVION2 (a passive capture decoder, separate checkout) hex-dumps the decrypted game stream of a recorded .pcap
(`avion2 dump <pcap> --opcode XXYY`, opcode bytes in wire order). Three records are read from it:

- `21 36` MapLoad_NT: count u32, map u32, ... Every spawn after it belongs to that map until the next map load.
- `41 36` NpcAppear_NT: key varint, field mask u16, u8, NpcData id u32, u8, u8 2, position f32 x3, direction f32 (degrees),
  ..., MapData spawn UniqueKey u32, u8 0 (the last five bytes). Mask bit 0x0001 = move_state present (walking / a patrol).
  Same layout as server/Aion.Protocol/Native/GamePackets.cs NpcAppear.
- `34 36` EnvObjSpawn_NT: key varint, desc type u8 (4 = env object, 1 = trigger), u8, EnvObjData id u32, position f32 x3,
  i32, yaw f32, 3 state bytes, create time i64, 8 bytes, MapData UniqueKey u32 (39 bytes after the position).

Only ids, map, position, heading and how often / how far apart each spawn was seen are kept: no raw bytes, no player,
account or character data (player appears are 45 36 and never read). The file is merged: captures already read (by file
name) are skipped, so new captures add to the old samples even after the old .pcap files are gone.

    python tools/official_npc_placements.py                       # every .pcap in %LOCALAPPDATA%/AVION2/captures
    python tools/official_npc_placements.py a.pcap b.pcap         # only these
    python tools/official_npc_placements.py --avion2 path/to/avion2.exe --rebuild

File format (tools/data/official-npc-placements.json): captures [file stems read], npcs {map: {unique_key: [npc_id, x, y, z,
direction, samples, spread, walking]}}, objects {map: {unique_key: [env_id, x, y, z, yaw, samples, spread]}}. x/y/z and the
heading are the first sighting; spread is the largest horizontal distance of a later sighting from it (a wanderer or a
patrol has a big one); walking counts the appears that carried a move state.
"""
import argparse
import json
import math
import os
import struct
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/official-npc-placements.json'
OPCODES = ('2136', '4136', '3436')
AVION2_CLI = Path('src/Avion2.Cli/bin/Release/net10.0/avion2.exe')


def default_captures():
    base = os.environ.get('LOCALAPPDATA')
    return Path(base) / 'AVION2' / 'captures' if base else None


def locate_avion2(configured=None):
    """--avion2, else $AVION2_CLI, else an AVION2 checkout next to this repository (or to its main checkout)."""
    for candidate in (configured, os.environ.get('AVION2_CLI')):
        if candidate:
            return Path(candidate)
    for parent in ROOT.parents:
        path = parent / 'AVION2' / AVION2_CLI
        if path.exists():
            return path
    return None


def varint(b, i):
    value = shift = 0
    while True:
        c = b[i]
        i += 1
        value |= (c & 0x7F) << shift
        shift += 7
        if c < 0x80:
            return value, i


def parse_npc_appear(body):
    """(npc_id, x, y, z, direction, unique_key, walking) from a 41 36 body, or None."""
    try:
        _key, i = varint(body, 0)
        mask = struct.unpack_from('<H', body, i)[0]
        i += 2
        if body[i + 6] != 2:
            return None
        npc_id = struct.unpack_from('<I', body, i + 1)[0]
        x, y, z, direction = struct.unpack_from('<4f', body, i + 7)
        unique_key = struct.unpack_from('<I', body, len(body) - 5)[0]
    except (IndexError, struct.error):
        return None
    if not all(math.isfinite(v) for v in (x, y, z, direction)) or body[-1] != 0:
        return None
    return npc_id, x, y, z, direction % 360, unique_key, bool(mask & 0x0001)


def parse_env_spawn(body):
    """(env_id, x, y, z, yaw, unique_key) from a 34 36 env object body (desc type 4), or None."""
    try:
        _key, i = varint(body, 0)
        if body[i] != 4:
            return None
        env_id = struct.unpack_from('<I', body, i + 2)[0]
        x, y, z = struct.unpack_from('<3f', body, i + 6)
        yaw = struct.unpack_from('<f', body, i + 22)[0]
        unique_key = struct.unpack_from('<I', body, i + 6 + 39)[0]
    except (IndexError, struct.error):
        return None
    if not all(math.isfinite(v) for v in (x, y, z, yaw)):
        return None
    return env_id, x, y, z, yaw % 360, unique_key


def parse_dump(lines):
    """Yield ('npc' | 'object', map, record) from `avion2 dump` lines (`HH:MM:SS.fff dN XX YY len N hex`)."""
    current = None
    for line in lines:
        parts = line.split()
        if len(parts) < 7 or not parts[1].startswith('d') or parts[4] != 'len':
            continue
        opcode = parts[2] + parts[3]
        if opcode not in OPCODES:
            continue
        try:
            body = bytes.fromhex(parts[6])
        except ValueError:
            continue
        if opcode == '2136':
            if len(body) >= 8:
                current = struct.unpack_from('<I', body, 4)[0] or current
            continue
        if current is None:
            continue
        record = parse_npc_appear(body) if opcode == '4136' else parse_env_spawn(body)
        if record and record[-1 if opcode == '3436' else -2]:
            yield ('npc' if opcode == '4136' else 'object'), current, record


def add_samples(data, samples):
    """Merge parsed samples into data['npcs'] / data['objects'] (first sighting kept, spread and counts grown)."""
    for kind, map_id, record in samples:
        if kind == 'npc':
            npc_id, x, y, z, direction, unique_key, walking = record
            row = [npc_id, round(x, 1), round(y, 1), round(z, 1), round(direction, 1), 0, 0.0, 0]
        else:
            env_id, x, y, z, yaw, unique_key = record
            row = [env_id, round(x, 1), round(y, 1), round(z, 1), round(yaw, 1), 0, 0.0]
            walking = False
        bucket = data.setdefault('npcs' if kind == 'npc' else 'objects', {}).setdefault(str(map_id), {})
        entry = bucket.setdefault(str(unique_key), row)
        entry[5] += 1
        entry[6] = round(max(entry[6], math.hypot(x - entry[1], y - entry[2])), 1)
        if kind == 'npc' and walking:
            entry[7] += 1
    return data


def dump_capture(avion2, pcap):
    """The dump lines of the three records, in stream order (one pass; the map load must come before its spawns)."""
    wanted = tuple(f' {op[:2]} {op[2:]} ' for op in OPCODES)
    with subprocess.Popen([str(avion2), 'dump', str(pcap)], stdout=subprocess.PIPE, text=True, errors='replace') as proc:
        lines = [line for line in proc.stdout if line[:1].isdigit() and any(w in line[:30] for w in wanted)]
    return lines


def sort_data(data):
    for kind in ('npcs', 'objects'):
        data[kind] = {m: dict(sorted(rows.items(), key=lambda kv: int(kv[0])))
                      for m, rows in sorted(data.get(kind, {}).items(), key=lambda kv: int(kv[0]))}
    data['captures'] = sorted(set(data.get('captures', [])))
    return data


def write(path, data):
    """One spawn per line, so a refresh diffs by spawn."""
    out = ['{', f'  "source": {json.dumps(data["source"])},', f'  "captures": {json.dumps(data.get("captures", []))},']
    for n, kind in enumerate(('npcs', 'objects')):
        out.append(f'  "{kind}": {{')
        maps = list(data.get(kind, {}).items())
        for i, (map_id, rows) in enumerate(maps):
            out.append(f'    "{map_id}": {{')
            items = list(rows.items())
            out += [f'      "{k}": {json.dumps(v)}' + (',' if j < len(items) - 1 else '') for j, (k, v) in enumerate(items)]
            out.append('    }' + (',' if i < len(maps) - 1 else ''))
        out.append('  }' + (',' if n == 0 else ''))
    out.append('}')
    Path(path).write_text('\n'.join(out) + '\n', encoding='utf-8', newline='\n')


def load(path=DATA):
    try:
        return json.loads(Path(path).read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return {}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('pcaps', nargs='*', type=Path, help='captures (default: every .pcap in %%LOCALAPPDATA%%/AVION2/captures)')
    parser.add_argument('--avion2', help='path to avion2.exe (the AVION2 CLI); default $AVION2_CLI or a sibling AVION2 checkout')
    parser.add_argument('--rebuild', action='store_true', help='forget the committed samples and read every capture again')
    args = parser.parse_args(argv)
    avion2 = locate_avion2(args.avion2)
    if avion2 is None or not avion2.exists():
        print('AVION2 CLI not found: build it in the AVION2 checkout (dotnet build -c Release src/Avion2.Cli) or pass --avion2',
              file=sys.stderr)
        return 1
    pcaps = args.pcaps
    if not pcaps:
        folder = default_captures()
        pcaps = sorted(folder.glob('*.pcap')) if folder and folder.exists() else []
    data = {} if args.rebuild else load()
    data.setdefault('source', 'official server->client captures decoded by AVION2 (41 36 NpcAppear_NT, 34 36 EnvObjSpawn_NT, '
                              '21 36 MapLoad_NT); tools/official_npc_placements.py')
    seen = set(data.get('captures', []))
    read = 0
    for pcap in pcaps:
        if pcap.stem in seen:
            continue
        samples = list(parse_dump(dump_capture(avion2, pcap)))
        add_samples(data, samples)
        data.setdefault('captures', []).append(pcap.stem)
        read += 1
        print(f'{pcap.name}: {sum(1 for s in samples if s[0] == "npc")} npc / {sum(1 for s in samples if s[0] == "object")} object appears')
    write(DATA, sort_data(data))
    print(f'{read} new captures; {sum(len(v) for v in data["npcs"].values())} npc spawns, '
          f'{sum(len(v) for v in data["objects"].values())} objects on {len(data["npcs"])} maps -> {DATA.relative_to(ROOT)}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
