"""Field bosses (the client's WorldMapFieldNamed table) for the server's respawn timers.

The client's WorldMapFieldNamed rows name every field boss the official world map lists ("named" monsters): the row id is
the code the official field boss table (0x9101, DevPacketData_Common_WorldMapFieldNamedInfo) sends, map x 100 + n on the
starter field maps and the Abyss, the NPC code itself on the level-80 regions; FieldNamedName is the boss's NpcData row.
The input is that table reduced to {row id: NpcData id} (for example extracted with the AION2 client table tools into a
JSON file kept outside the repository). The map of each boss is the field map whose MapData spawns that NPC
(tools/data/world-npcs.json), preferring map = code // 100.

    python tools/field_bosses.py build <world-map-field-named.json>   # writes tools/data/field-bosses.json
    python tools/field_bosses.py schedules <WorldMapFieldNamed dump> <PeriodSpawn dump>   # adds the fixed schedules

File format: {"source": ..., "bosses": [[code, map_id, npc_id], ...] sorted by code,
"schedules": {code: {"time": minutes after midnight, "cycle": minutes (0: once a day), "days": [weekday, 0 = Sunday]}}}.

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


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    b = sub.add_parser('build')
    b.add_argument('table', help='WorldMapFieldNamed as {row id: NpcData id}')
    s = sub.add_parser('schedules')
    s.add_argument('field_named', help='WorldMapFieldNamed table dump')
    s.add_argument('period_spawn', help='PeriodSpawn table dump')
    args = parser.parse_args(argv)
    data = json.loads(DATA.read_text(encoding='utf-8')) if DATA.exists() else {'source': {}}
    if args.command == 'build':
        table = json.loads(Path(args.table).read_text(encoding='utf-8'))
        bosses, missing = build(table)
        data['source']['bosses'] = 'WorldMapFieldNamed (ID, FieldNamedName -> NpcData), Global/EU client of 2026-11-04; map from MapData spawns'
        data['bosses'] = bosses
        print(f'{DATA}: {len(bosses)} field bosses' + (f'; not spawned: {missing}' if missing else ''))
    else:
        read = lambda path: json.loads(Path(path).read_text(encoding='utf-8'))
        data['schedules'] = schedules(read(args.field_named), read(args.period_spawn))
        data['source']['schedules'] = ('WorldMapFieldNamed PeriodSpawnId -> PeriodSpawn (Time, Cycle, bSunday..bSaturday), '
                                       'Global/EU client of 2026-11-04, unchanged in 2.0.11 of 2026-11-11')
        print(f'{DATA}: {len(data["schedules"])} scheduled field bosses')
    write_data_json(DATA, data)


if __name__ == '__main__':
    main()
