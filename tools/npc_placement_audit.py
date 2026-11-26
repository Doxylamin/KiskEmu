"""Audit of where the server puts the NPCs and objects quests point at, against the official servers.

The bug class: a quest NPC (or quest object) the server spawns at another position, height or facing than official, so it
stands outside the dialog camera, under the ground, in the air or with its back to the player; and objectives whose NPC or
object never spawns, or only on another map.

Inputs (all committed, the audit runs in CI):
- tools/data/quests.json                 quest givers / finishers, AskNpc / CloseNpc / KillNpc / CollectFakeItem NPCs,
                                         UseEnvObj objects, with the objective's map
- tools/data/world-npcs.json             what the server spawns: MapData.dat SpawnInfoList (the client's own table) plus the
                                         rows tools/quest_supplement.py merged in (`*_QuestSup`, unique key 0: OURS, improvised)
- tools/data/world-objects.json          MapData env objects (EnvObjData id, position, yaw, unique key)
- tools/data/official-npc-placements.json  official appears (41 36 / 34 36) from captures, tools/official_npc_placements.py
- tools/data/map-bases.json              instance layers and the base map whose geometry they share

Server = client table: the server loads the MapData position and yaw unchanged (GameData / Combat.cs: Direction = Yaw) and
puts a standing NPC on the ground at runtime when the navmesh floor or terrain is within 100 of the spawn Z
(GameSession.Npcs.cs Grounded). Official appears sit ~20 below the MapData Z (on the ground), so a Z difference up to
Z_OFF is what that grounding absorbs; beyond it the server keeps the MapData Z and the NPC floats or sinks.

Official samples are matched by the MapData UniqueKey the appear carries; a server spawn without one (the supplement) is
matched to the nearest official appear of the same NPC id on the map. An official NPC that walked (move state set) or was
seen far from its first sighting is `moving`: its position and heading say nothing about the spawn.

Terrain (optional, never in the committed report): with `--terrain [DIR]` (default research/local/terrain, TerrainProbe's
<map>/terrain.bin grids) every quest spawn's MapData Z is compared with the ground under it, flagging `z_terrain` when the
server's grounding cannot reach it (more than Z_OFF away; a bridge or a floor of a building is a false positive). The
findings are printed, the committed report stays the same on every machine.

    python tools/npc_placement_audit.py            # writes tools/reports/npc-placement-audit.md
    python tools/npc_placement_audit.py --check    # exits 1 when the committed report is out of date
    python tools/npc_placement_audit.py --terrain  # also the terrain check (needs research/local/terrain)
"""
import argparse
import json
import math
import struct
import sys
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data'
REPORT = ROOT / 'tools/reports/npc-placement-audit.md'
TERRAIN = ROOT / 'research/local/terrain'

POS_OFF = 100.0       # horizontal units (cm): 1 m
Z_OFF = 100.0         # the server's grounding tolerance (GameSession.Npcs.cs Grounded)
HEADING_OFF = 15.0    # degrees
MOVING = 100.0        # an official sighting this far from the first one: the NPC walks
TOP = 40
LEVEL_CAP = 45
TALK_ROLES = ('start', 'finish', 'AskNpc', 'CloseNpc')
KILL_ROLES = ('KillNpc', 'CollectFakeItem')
# What a flag means, in report order. Severity: placement flags first, then spawn problems, then information.
FLAGS = {
    'pos_off': f'official position more than {POS_OFF / 100:g} m away (horizontal)',
    'z_off': f'official height more than {Z_OFF:g} away from the spawn Z: the server grounding cannot reach it',
    'heading_off': f'official heading more than {HEADING_OFF:g} degrees off',
    'missing_spawn': 'the quest names an NPC / object no map spawns',
    'wrong_map': 'spawned, but not on the objective\'s map nor on a layer of it',
    'official_unmatched': 'official appears of this NPC id on the map at a unique key the server does not have',
    'improvised': 'server spawn is OURS (quest supplement), not a MapData row',
    'other_layer': 'spawned on an instance layer of the objective\'s map only (fine when the quest enters the layer)',
    'moving': 'the official NPC walked: position and heading not compared',
    'no_sample': 'no official appear captured yet',
}
PLACEMENT_FLAGS = ('pos_off', 'z_off', 'heading_off')


def load(name):
    return json.loads((DATA / name).read_text(encoding='utf-8'))


def angle_delta(a, b):
    """Smallest signed difference a - b in degrees."""
    return (a - b + 180.0) % 360.0 - 180.0


def band(level):
    if level is None:
        return '?'
    if level > LEVEL_CAP:
        return f'{LEVEL_CAP + 1}+ (above cap)'
    low = level // 10 * 10
    return f'{max(low, 1)}-{low + 9}' if low < 40 else f'40-{LEVEL_CAP}'


def compare(server, official):
    """Deltas and placement flags between a server spawn {x, y, z, yaw} and an official sample {x, y, z, heading,
    samples, spread, walking}; official None = no_sample."""
    if official is None:
        return {'flags': ['no_sample']}
    dxy = math.hypot(official['x'] - server['x'], official['y'] - server['y'])
    dz = official['z'] - server['z']
    dh = angle_delta(official['heading'], server['yaw'])
    out = {'dxy': round(dxy, 1), 'dz': round(dz, 1), 'dh': round(dh, 1), 'flags': []}
    if official.get('walking') or official.get('spread', 0) > MOVING:
        out['flags'].append('moving')
        return out
    if dxy > POS_OFF:
        out['flags'].append('pos_off')
    if abs(dz) > Z_OFF:
        out['flags'].append('z_off')
    if abs(dh) > HEADING_OFF:
        out['flags'].append('heading_off')
    return out


def severity(row):
    """Sort key, worst first: the size of the placement error in 'metres' (heading: 10 degrees ~ 1 m)."""
    c = row['compare']
    if not any(f in c['flags'] for f in PLACEMENT_FLAGS):
        return 0.0
    return max(c.get('dxy', 0) / 100, abs(c.get('dz', 0)) / 100, abs(c.get('dh', 0)) / 10)


def quest_refs(quests):
    """[(code, role, kind 'npc' | 'object', id, map)] for every NPC / object a quest names (unavailable quests skipped)."""
    out = []
    for code, q in sorted(quests.items()):
        if q.get('unavailable'):
            continue
        qmap = q.get('map') or 0
        if isinstance(q.get('start_npc'), int):
            out.append((code, 'start', 'npc', q['start_npc'], qmap))
        if isinstance(q.get('finish_npc'), int):
            out.append((code, 'finish', 'npc', q['finish_npc'], q.get('finish_map') or qmap))
        for step in q.get('steps') or []:
            for o in step.get('objectives') or []:
                kind, omap = o.get('type'), o.get('map') or qmap
                if kind in TALK_ROLES and isinstance(o.get('npc'), int):
                    out.append((code, kind, 'npc', o['npc'], omap))
                elif kind in KILL_ROLES:
                    out.extend((code, kind, 'npc', n, omap) for n in o.get('npcs') or [] if isinstance(n, int))
                elif kind == 'UseEnvObj':
                    out.extend((code, kind, 'object', e, omap) for e in o.get('envs') or [] if isinstance(e, int))
    return sorted(set(out))


def server_spawns(world, objects):
    """{('npc' | 'object', id): [spawn]} with spawn {map, name, x, y, z, yaw, unique_key, route, improvised}. Unique keys as
    u32 (world-objects.json keeps them as signed i32, the wire and world-npcs.json unsigned)."""
    out = defaultdict(list)
    for map_id, rows in world.get('maps', {}).items():
        for r in rows:
            name, npc_id, x, y, z, yaw, unique_key = r[:7]
            route = r[8] if len(r) > 8 else -1
            out[('npc', npc_id)].append({'map': int(map_id), 'name': name, 'x': x, 'y': y, 'z': z, 'yaw': yaw,
                                         'unique_key': (unique_key or 0) & 0xFFFFFFFF, 'route': route,
                                         'improvised': not unique_key or name.endswith('_QuestSup') or '_QuestSup_' in name})
    for map_id, entry in objects.get('maps', {}).items():
        for o in entry.get('objects', []):
            x, y, z = o['position']
            out[('object', o['env'])].append({'map': int(map_id), 'name': o['name'], 'x': x, 'y': y, 'z': z, 'yaw': o.get('yaw', 0.0),
                                              'unique_key': (o.get('unique_key') or 0) & 0xFFFFFFFF, 'route': -1, 'improvised': not o.get('unique_key')})
    return out


def official_index(official):
    """by_key {(kind, map, unique_key): sample}, by_id {(kind, map, id): [sample]}; sample {id, x, y, z, heading, ...}."""
    by_key, by_id = {}, defaultdict(list)
    for kind, section in (('npc', 'npcs'), ('object', 'objects')):
        for map_id, rows in official.get(section, {}).items():
            for unique_key, r in rows.items():
                sample = {'id': r[0], 'x': r[1], 'y': r[2], 'z': r[3], 'heading': r[4], 'samples': r[5], 'spread': r[6],
                          'walking': r[7] if len(r) > 7 else 0, 'unique_key': int(unique_key), 'map': int(map_id)}
                by_key[(kind, int(map_id), int(unique_key))] = sample
                by_id[(kind, int(map_id), r[0])].append(sample)
    return by_key, by_id


def audit(quests=None, world=None, objects=None, official=None, bases=None):
    quests = load('quests.json')['quests'] if quests is None else quests
    world = load('world-npcs.json') if world is None else world
    objects = load('world-objects.json') if objects is None else objects
    official = load('official-npc-placements.json') if official is None else official
    bases = {int(k): v for k, v in (load('map-bases.json')['bases'] if bases is None else bases).items()}
    base = lambda m: bases.get(m, m)
    spawns = server_spawns(world, objects)
    by_key, by_id = official_index(official)
    sampled_maps = {m for (_kind, m, _k) in by_key}

    refs = quest_refs(quests)
    quests_of = defaultdict(set)
    for code, role, kind, ident, map_id in refs:
        quests_of[(kind, ident, map_id)].add((code, role))

    targets = {}      # (kind, id, map) -> {'spawn_flags', 'rows', 'quests', 'placement'}
    for (kind, ident, map_id), users in sorted(quests_of.items()):
        roles = {role for _c, role in users}
        placement = kind == 'object' or bool(roles & set(TALK_ROLES))
        all_spawns = spawns.get((kind, ident), [])
        # The objective's map and its instance layers (same base geometry): a quest NPC often stands in a layer too.
        here = [s for s in all_spawns if not map_id or base(s['map']) == base(map_id)]
        flags = []
        if not all_spawns:
            flags.append('missing_spawn')
        elif not here:
            flags.append('wrong_map')
        elif map_id and not any(s['map'] == map_id for s in here):
            flags.append('other_layer')
        rows = []
        if placement:
            for s in sorted(here, key=lambda s: (s['map'], s['name'], s['unique_key'])):
                sample = by_key.get((kind, s['map'], s['unique_key'])) if s['unique_key'] else None
                matched_by = 'key' if sample else None
                if sample is None and s['improvised']:
                    near = by_id.get((kind, s['map'], ident), [])
                    if near:
                        sample = min(near, key=lambda o: math.hypot(o['x'] - s['x'], o['y'] - s['y']))
                        matched_by = 'id'
                c = compare(s, sample)
                if s['improvised']:
                    c['flags'].append('improvised')
                rows.append({'spawn': s, 'official': sample, 'matched_by': matched_by, 'compare': c})
            # Official appears of this id on the map that no server spawn claims.
            known = {(s['map'], s['unique_key']) for s in all_spawns}
            unmatched = [o for m in {s['map'] for s in here} | ({map_id} if map_id else set())
                         for o in by_id.get((kind, m, ident), []) if (o['map'], o['unique_key']) not in known]
            if unmatched and not any(r['matched_by'] == 'id' for r in rows):
                flags.append('official_unmatched')
        else:
            unmatched = []
        targets[(kind, ident, map_id)] = {'spawn_flags': flags, 'rows': rows, 'quests': sorted(users), 'placement': placement,
                                          'unmatched': unmatched, 'name': (all_spawns[0]['name'] if all_spawns else ''),
                                          'on_maps': sorted({s['map'] for s in all_spawns}),
                                          'sampled_map': map_id in sampled_maps}
    # Standing NPCs no quest names, with an official sample: the same comparison, as a check that the method finds offsets.
    claimed = {(r['spawn']['map'], r['spawn']['unique_key']) for t in targets.values() for r in t['rows']}
    templates = world.get('templates', {})
    others = []
    for (kind, ident), rows in spawns.items():
        if kind != 'npc' or templates.get(str(ident), {}).get('type') in (None, 'Monster'):
            continue
        for s in rows:
            sample = by_key.get(('npc', s['map'], s['unique_key'])) if s['unique_key'] else None
            if sample and s['route'] == -1 and (s['map'], s['unique_key']) not in claimed:
                others.append({'id': ident, 'spawn': s, 'official': sample, 'compare': compare(s, sample)})
    others.sort(key=lambda r: (-severity(r), r['spawn']['map'], r['spawn']['unique_key']))
    return {'targets': targets, 'quests': quests, 'refs': refs, 'sampled_maps': sorted(sampled_maps), 'others': others,
            'captures': len(official.get('captures', [])),
            'official_spawns': sum(len(v) for v in official.get('npcs', {}).values()),
            'official_objects': sum(len(v) for v in official.get('objects', {}).values())}


def target_flags(t):
    flags = set(t['spawn_flags'])
    for r in t['rows']:
        flags.update(r['compare']['flags'])
    return flags


def fmt_pos(p, z_key='z'):
    return f"{p['x']:.0f}, {p['y']:.0f}, {p[z_key]:.0f}"


def quest_list(users, limit=4):
    codes = sorted({c for c, _r in users})
    return ', '.join(codes[:limit]) + (f' +{len(codes) - limit}' if len(codes) > limit else '')


def render(result):
    targets, quests = result['targets'], result['quests']
    placement = {k: t for k, t in targets.items() if t['placement']}
    rows = [(k, t, r) for k, t in placement.items() for r in t['rows']]
    sampled = [x for x in rows if x[2]['official'] is not None]
    lines = ['# NPC placement audit: quest NPCs and objects against the official servers', '',
             'Generated by `python tools/npc_placement_audit.py`; do not edit by hand. Refresh the official samples with',
             '`python tools/official_npc_placements.py` after recording new captures (server->client only), then run the audit.', '',
             '## Inputs', '',
             f"- {len({c for c, *_ in result['refs']})} available quests (above-cap / unavailable ones skipped) naming "
             f"{len({(k[0], k[1]) for k in targets})} NPCs / objects on {len({k[2] for k in targets})} maps.",
             f"- Placement checked for talk NPCs (giver, finisher, AskNpc, CloseNpc) and UseEnvObj objects: {len(placement)} "
             f"(id, map) pairs, {len(rows)} server spawns. Kill targets are only checked for a spawn on the right map "
             "(monsters wander).",
             f"- Official samples: {result['official_spawns']} NPC spawns and {result['official_objects']} objects from "
             f"{result['captures']} captures, on maps {', '.join(map(str, result['sampled_maps'])) or 'none'}.",
             f"- **{len(sampled)} of {len(rows)} quest spawns have an official sample**; the other {len(rows) - len(sampled)} are "
             "unknown, not correct. A quest-gated NPC only appears while its step is open, so a capture of the map without the "
             "quest has no sample of it either.",
             '- Server position = the MapData row (the client\'s own table), yaw unchanged; standing NPCs are grounded at runtime '
             f'when the floor is within {Z_OFF:g} (official appears sit ~20 below the MapData Z). Rows marked OURS come from '
             'the quest supplement and have no client row.',
             '- Class: no quest in quests.json is gated by class (QuestClassReward only changes rewards), so placement does not '
             'differ by class; race and level band are below.',
             '- Terrain height: not in this report (needs research/local/terrain, see `--terrain`).', '',
             '## Flags', '', '| Flag | Meaning | (id, map) pairs |', '| --- | --- | ---: |']
    counts = Counter(f for t in targets.values() for f in target_flags(t))
    for flag, meaning in FLAGS.items():
        lines.append(f'| `{flag}` | {meaning} | {counts.get(flag, 0)} |')

    worst = sorted((x for x in sampled if severity(x[2]) > 0), key=lambda x: (-severity(x[2]), x[0]))
    lines += ['', f'## Worst placement offsets (official sample, not moving; top {TOP} of {len(worst)})', '']
    if worst:
        lines += ['Δ = official - server. Heading in degrees.', '',
                  '| NPC / object | Map | Spawn | Server x, y, z / yaw | Official x, y, z / heading | Δxy | Δz | Δheading | Flags | Quests |',
                  '| --- | ---: | --- | --- | --- | ---: | ---: | ---: | --- | --- |']
        for (kind, ident, map_id), t, r in worst[:TOP]:
            s, o, c = r['spawn'], r['official'], r['compare']
            lines.append(f"| {kind} {ident} | {s['map']} | {s['name']}{' (OURS)' if s['improvised'] else ''} | "
                         f"{fmt_pos(s)} / {s['yaw']:.0f} | {fmt_pos(o)} / {o['heading']:.0f} | {c['dxy']:.0f} | {c['dz']:.0f} | "
                         f"{c['dh']:.0f} | {', '.join(c['flags'])} | {quest_list(t['quests'])} |")
    else:
        lines.append('None among the sampled spawns.')

    spawn_problems = sorted((k, t) for k, t in targets.items() if {'missing_spawn', 'wrong_map', 'official_unmatched'} & set(t['spawn_flags']))
    lines += ['', f'## Spawn problems ({len(spawn_problems)})', '']
    if spawn_problems:
        lines += ['| NPC / object | Objective map | Flag | Spawned on | Roles | Quests |', '| --- | ---: | --- | --- | --- | --- |']
        for (kind, ident, map_id), t in spawn_problems:
            on = t['on_maps']
            where = ', '.join(map(str, on[:6])) + (f' +{len(on) - 6}' if len(on) > 6 else '') or '—'
            if 'official_unmatched' in t['spawn_flags']:
                where += f" (official: {len(t['unmatched'])} other spawn{'s' if len(t['unmatched']) != 1 else ''})"
            roles = ', '.join(sorted({r for _c, r in t['quests']}))
            lines.append(f"| {kind} {ident} {t['name']} | {map_id} | {', '.join(f for f in t['spawn_flags'])} | {where} | {roles} | "
                         f"{quest_list(t['quests'])} |")

    def summary(title, key_of):
        groups = defaultdict(lambda: Counter())
        for k, t in targets.items():
            keys = {key_of(code, k) for code, _r in t['quests']}
            flags = target_flags(t)
            for key in keys:
                g = groups[key]
                g['pairs'] += 1
                g['spawns'] += len(t['rows'])
                g['sampled'] += sum(1 for r in t['rows'] if r['official'] is not None)
                for f in ('pos_off', 'z_off', 'heading_off', 'missing_spawn', 'wrong_map', 'improvised'):
                    g[f] += f in flags
        out = ['', f'## By {title}', '',
               f'| {title.capitalize()} | (id, map) pairs | Quest spawns | Sampled | pos_off | z_off | heading_off | missing_spawn | wrong_map | improvised |',
               '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |']
        for key in sorted(groups, key=lambda k: (str(type(k)), k)):
            g = groups[key]
            out.append(f"| {key} | {g['pairs']} | {g['spawns']} | {g['sampled']} | {g['pos_off']} | {g['z_off']} | {g['heading_off']} | "
                       f"{g['missing_spawn']} | {g['wrong_map']} | {g['improvised']} |")
        return out

    lines += summary('map', lambda code, k: k[2])
    lines += summary('race', lambda code, k: quests[code].get('race') or '?')
    lines += summary('level band', lambda code, k: band(quests[code].get('level')))

    others = result['others']
    off = [r for r in others if severity(r) > 0]
    lines += ['', f'## Other standing NPCs with an official sample ({len(others)}, {len(off)} off)', '',
              'NPCs no quest names (guards, villagers, vendors), compared the same way: a check that the method finds offsets, '
              'and placement bugs outside quests.', '']
    if off:
        lines += ['| NPC | Map | Spawn | Samples | Δxy | Δz | Δheading | Flags |', '| --- | ---: | --- | ---: | ---: | ---: | ---: | --- |']
        for r in off[:TOP // 2]:
            s, o, c = r['spawn'], r['official'], r['compare']
            lines.append(f"| npc {r['id']} | {s['map']} | {s['name']} | {o['samples']} | {c['dxy']:.0f} | {c['dz']:.0f} | {c['dh']:.0f} | "
                         f"{', '.join(c['flags'])} |")
    moving = sum(1 for r in others if 'moving' in r['compare']['flags'])
    lines.append(f"{'' if not off else chr(10)}{moving} of them walked in the capture (not compared).")

    lines += ['', f'## Sampled quest spawns ({len(sampled)})', '',
              'Every quest spawn with an official sample. `key`: matched by the MapData UniqueKey; `id`: an OURS spawn matched to '
              'the nearest official appear of the same NPC id.', '',
              '| NPC / object | Map | Spawn | Match | Samples | Δxy | Δz | Δheading | Flags |',
              '| --- | ---: | --- | --- | ---: | ---: | ---: | ---: | --- |']
    for (kind, ident, map_id), t, r in sorted(sampled, key=lambda x: (x[2]['spawn']['map'], x[0][0], x[0][1], x[2]['spawn']['name'])):
        s, o, c = r['spawn'], r['official'], r['compare']
        lines.append(f"| {kind} {ident} | {s['map']} | {s['name']} | {r['matched_by']} | {o['samples']} | {c['dxy']:.0f} | {c['dz']:.0f} | "
                     f"{c['dh']:.0f} | {', '.join(c['flags']) or 'ok'} |")
    lines.append('')
    return '\n'.join(lines)


# --- optional terrain check -------------------------------------------------------------------------------------------

def read_terrain(path):
    """TerrainProbe's terrain.bin ("AIONHGT1", version 1): a GroundAt(x, y) function, bilinear, None outside / no data."""
    raw = Path(path).read_bytes()
    if raw[:8] != b'AIONHGT1' or struct.unpack_from('<i', raw, 8)[0] != 1:
        raise ValueError(f'{path}: not a version 1 terrain grid')
    ox, oy, sx, sy = struct.unpack_from('<4d', raw, 12)
    w, h = struct.unpack_from('<2i', raw, 44)
    heights = memoryview(raw)[52:52 + w * h * 4].cast('f')

    def ground_at(x, y):
        fx, fy = (x - ox) / sx, (y - oy) / sy
        if not (0 <= fx <= w - 1 and 0 <= fy <= h - 1):
            return None
        ix, iy = min(int(fx), w - 2), min(int(fy), h - 2)
        tx, ty = fx - ix, fy - iy
        a, b = heights[iy * w + ix], heights[iy * w + ix + 1]
        c, d = heights[(iy + 1) * w + ix], heights[(iy + 1) * w + ix + 1]
        v = (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty
        return None if math.isnan(v) else v
    return ground_at


def terrain_check(result, directory=TERRAIN, bases=None):
    """[(target key, spawn, ground)] of quest spawns more than Z_OFF from the terrain; None when there is no terrain."""
    directory = Path(directory)
    if not directory.is_dir():
        return None
    bases = {int(k): v for k, v in (load('map-bases.json')['bases'] if bases is None else bases).items()}
    grids, out = {}, []
    for key, t in sorted(result['targets'].items()):
        for r in t['rows']:
            s = r['spawn']
            m = bases.get(s['map'], s['map'])
            if m not in grids:
                path = directory / str(m) / 'terrain.bin'
                grids[m] = read_terrain(path) if path.exists() else None
            if grids[m] and (ground := grids[m](s['x'], s['y'])) is not None and abs(s['z'] - ground) > Z_OFF:
                out.append((key, s, ground))
    return out


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--check', action='store_true', help='fail when the committed report is stale')
    parser.add_argument('--terrain', nargs='?', const=str(TERRAIN), help='also compare spawn Z with the terrain grids in DIR')
    args = parser.parse_args(argv)
    result = audit()
    text = render(result)
    if args.check:
        current = REPORT.read_text(encoding='utf-8') if REPORT.exists() else ''
        if current != text:
            print(f'{REPORT.relative_to(ROOT)} is out of date: run python tools/npc_placement_audit.py', file=sys.stderr)
            return 1
        return 0
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text(text, encoding='utf-8', newline='\n')
    counts = Counter(f for t in result['targets'].values() for f in target_flags(t))
    print(f"{REPORT.relative_to(ROOT)}: {len(result['targets'])} (id, map) pairs; " +
          ', '.join(f'{f} {counts[f]}' for f in FLAGS if counts.get(f)))
    if args.terrain:
        found = terrain_check(result, args.terrain)
        if found is None:
            print(f'terrain check skipped: no terrain grids in {args.terrain} (TerrainProbe output, research/local/terrain)')
        else:
            print(f'terrain: {len(found)} quest spawns more than {Z_OFF:g} from the ground (bridges / floors are false positives)')
            for (kind, ident, map_id), s, ground in found:
                print(f"  {kind} {ident} map {s['map']} {s['name']}: z {s['z']:.0f}, ground {ground:.0f} ({s['z'] - ground:+.0f})")
    return 0


if __name__ == '__main__':
    sys.exit(main())
