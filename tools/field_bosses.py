"""Field bosses (the client's WorldMapFieldNamed table) for the server's respawn timers.

The client's WorldMapFieldNamed rows name every field boss the official world map lists ("named" monsters): the row id is
the code the official field boss table (0x9101, DevPacketData_Common_WorldMapFieldNamedInfo) sends, map x 100 + n on the
starter field maps and the Abyss, the NPC code itself on the level-80 regions; FieldNamedName is the boss's NpcData row.
The input is that table reduced to {row id: NpcData id} (for example extracted with the AION2 client table tools into a
JSON file kept outside the repository). The map of each boss is the field map whose MapData spawns that NPC
(tools/data/world-npcs.json), preferring map = code // 100.

    python tools/field_bosses.py build <world-map-field-named.json>   # writes tools/data/field-bosses.json
    python tools/field_bosses.py schedules <WorldMapFieldNamed dump> <PeriodSpawn dump>   # adds the fixed schedules
    python tools/field_bosses.py waits   # rewrites the fixed waits (VERTERON_WAITS) only

File format: {"source": ..., "bosses": [[code, map_id, npc_id], ...] sorted by code,
"schedules": {code: {"time": minutes after midnight, "cycle": minutes (0: once a day), "days": [weekday, 0 = Sunday]}},
"waits": {code: minutes}}.

Waits: CANDIDATE fixed respawn waits after a kill, fitted to the official field boss tables (0x9101) in our Verteron
captures (VERTERON_WAITS), mirrored by index onto Altgard (map 1110), whose 24 bosses have the same grade and level as
Verteron's, one by one. Every kill in the captures fits one fixed wait per boss.

Schedules: the WorldMapFieldNamed rows of SpawnType PeriodSpawn name a PeriodSpawn row (client table dumps as the client
table tools write them: {"rows": [{"ID": {"Value": ..}, ...}]}). Its Time (ticks, server local time) is the first spawn
of the day, Cycle (minutes) the repeat, bSunday..bSaturday the days. Only the 13 Abyss bosses have one; the other bosses'
respawn ranges are CANDIDATE (docs/FIELD_BOSSES.md).
"""
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/field-bosses.json'
WORLD_NPCS = ROOT / 'tools/data/world-npcs.json'


def build(table, world_npcs=WORLD_NPCS):
    """[[code, map, npc]] for every row whose NPC the server spawns; rows it does not spawn are left out."""
    maps = json.loads(Path(world_npcs).read_text(encoding='utf-8'))['maps']
    spawned = {}
    for map_id, spawns in maps.items():
        for spawn in spawns:
            spawned.setdefault(spawn[1], set()).add(int(map_id))
    bosses, missing = [], []
    for code, npc in sorted(((int(k), int(v)) for k, v in table.items())):
        where = spawned.get(npc, set())
        if not where:
            missing.append(code)
            continue
        map_id = code // 100 if code // 100 in where else min(where)
        bosses.append([code, map_id, npc])
    return bosses, missing


DAYS = ['bSunday', 'bMonday', 'bTuesday', 'bWednesday', 'bThursday', 'bFriday', 'bSaturday']
TICKS_PER_MINUTE = 600_000_000


def schedules(field_named, period_spawn):
    """{code: {time, cycle, days}} for each WorldMapFieldNamed row that names a PeriodSpawn row."""
    periods = {row['ID']['Value']: row for row in period_spawn['rows']}
    out = {}
    for row in field_named['rows']:
        if not row.get('SpawnType', '').endswith('::PeriodSpawn'):
            continue
        period = periods.get(row['PeriodSpawnId']['Value'])
        if period is None:
            continue
        cycle = period['Cycle'] if period['SpawnType'].endswith('::Cycle') else 0
        out[str(row['ID']['Value'])] = {
            'time': period['Time']['Ticks'] // TICKS_PER_MINUTE,
            'cycle': cycle,
            'days': [i for i, day in enumerate(DAYS) if period[day]],
        }
    return dict(sorted(out.items(), key=lambda kv: int(kv[0])))


# Verteron (1010) waits in minutes, with the bounds in hours the captures give (2026-11-02 to 11-05, 697 tables): the
# kill bracketed between the last table showing the boss up and the first showing it dead, intersected over all kills,
# plus the respawn time still ahead when first seen dead. "fit": the bounds pin this round value; "guess": a value
# inside wide or one-sided bounds. All CANDIDATE.
VERTERON_WAITS = {
    101001: (180, 'guess', 0.42, 3.50),
    101002: (180, 'guess', 0.45, 6.65),
    101003: (180, 'guess', 0.39, 3.46),
    101004: (240, 'guess', 1.79, 6.99),
    101005: (240, 'guess', 1.90, 6.94),
    101006: (90, 'fit', 1.45, 1.77),
    101007: (240, 'guess', 0.86, 7.22),
    101008: (180, 'fit', 2.99, 3.01),
    101009: (180, 'fit', 2.99, 3.02),
    101010: (180, 'fit', 2.92, 3.17),
    101011: (240, 'fit', 3.60, 4.24),
    101012: (360, 'guess', 5.57, 15.86),
    101013: (240, 'guess', 3.83, None),
    101014: (360, 'guess', 5.83, 15.72),
    101015: (180, 'fit', 2.70, 3.34),
    101016: (240, 'fit', 3.67, 4.31),
    101017: (120, 'fit', 1.86, 2.07),
    101018: (240, 'guess', 3.58, None),
    101019: (120, 'fit', 1.89, 2.11),
    101020: (360, 'guess', 5.88, None),
    101021: (720, 'guess', 10.80, None),
    101022: (360, 'guess', 5.68, None),
    101023: (360, 'guess', 5.74, None),
    101024: (720, 'guess', 10.85, None),
}


def waits(bosses):
    """{code: minutes}: Verteron's waits, and Altgard's by index (111000 + n gets 101000 + n's)."""
    codes = {b[0] for b in bosses}
    out = {}
    for code, (minutes, _, low, high) in VERTERON_WAITS.items():
        assert low * 60 <= minutes and (high is None or minutes <= high * 60), code
        for c in (code, code + 10_000):
            if c in codes:
                out[str(c)] = minutes
    return dict(sorted(out.items(), key=lambda kv: int(kv[0])))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    b = sub.add_parser('build')
    b.add_argument('table', help='WorldMapFieldNamed as {row id: NpcData id}')
    s = sub.add_parser('schedules')
    s.add_argument('field_named', help='WorldMapFieldNamed table dump')
    s.add_argument('period_spawn', help='PeriodSpawn table dump')
    sub.add_parser('waits')
    args = parser.parse_args(argv)
    data = json.loads(DATA.read_text(encoding='utf-8')) if DATA.exists() else {'source': {}}
    if args.command == 'build':
        table = json.loads(Path(args.table).read_text(encoding='utf-8'))
        bosses, missing = build(table)
        data['source']['bosses'] = 'WorldMapFieldNamed (ID, FieldNamedName -> NpcData), Global/EU client of 2026-11-04; map from MapData spawns'
        data['bosses'] = bosses
        print(f'{DATA}: {len(bosses)} field bosses' + (f'; not spawned: {missing}' if missing else ''))
    elif args.command == 'schedules':
        read = lambda path: json.loads(Path(path).read_text(encoding='utf-8'))
        data['schedules'] = schedules(read(args.field_named), read(args.period_spawn))
        data['source']['schedules'] = ('WorldMapFieldNamed PeriodSpawnId -> PeriodSpawn (Time, Cycle, bSunday..bSaturday), '
                                       'Global/EU client of 2026-11-04, unchanged in 2.0.11 of 2026-11-11')
        print(f'{DATA}: {len(data["schedules"])} scheduled field bosses')
    data['waits'] = waits(data['bosses'])
    data['source']['waits'] = ('CANDIDATE fixed waits fitted to the official field boss tables (0x9101) of Verteron captures '
                               '2026-11-02 to 11-05; Altgard mirrors Verteron by index (same grades and levels)')
    print(f'{DATA}: {len(data["waits"])} fixed waits')
    write_data_json(DATA, data)


if __name__ == '__main__':
    main()
