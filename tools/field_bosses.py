"""Field bosses (the client's WorldMapFieldNamed table) for the server's respawn timers.

The client's WorldMapFieldNamed rows name every field boss the official world map lists ("named" monsters): the row id is
the code the official field boss table (0x9101, DevPacketData_Common_WorldMapFieldNamedInfo) sends, map x 100 + n on the
starter field maps and the Abyss, the NPC code itself on the level-80 regions; FieldNamedName is the boss's NpcData row.
The input is that table reduced to {row id: NpcData id} (for example extracted with the AION2 client table tools into a
JSON file kept outside the repository). The map of each boss is the field map whose MapData spawns that NPC
(tools/data/world-npcs.json), preferring map = code // 100.

    python tools/field_bosses.py build <world-map-field-named.json>   # writes tools/data/field-bosses.json

File format: {"source": ..., "bosses": [[code, map_id, npc_id], ...]} sorted by code.

Respawn periods are not in the client data (PeriodSpawnId points at a server table): the server's defaults are CANDIDATE
(docs/FIELD_BOSSES.md).
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


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    b = sub.add_parser('build')
    b.add_argument('table', help='WorldMapFieldNamed as {row id: NpcData id}')
    args = parser.parse_args(argv)
    table = json.loads(Path(args.table).read_text(encoding='utf-8'))
    bosses, missing = build(table)
    write_data_json(DATA, {
        'source': {'bosses': 'WorldMapFieldNamed (ID, FieldNamedName -> NpcData), Global/EU client of 2026-11-04; map from MapData spawns'},
        'bosses': bosses,
    })
    print(f'{DATA}: {len(bosses)} field bosses' + (f'; not spawned: {missing}' if missing else ''))


if __name__ == '__main__':
    main()
