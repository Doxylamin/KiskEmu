"""Subzone areas of every map and the world-map fog group each one uncovers.

Sources:
- research/local/all-mapdata-full/*/table-*.json (MapData.dat of every map, as tools/map_points.py):
  SubzoneVolumeInfoMap: a 2D polygon (Points), HeightMin/HeightMax and the Subzone table id.
- Subzone.dat (research/local/ascension-flight-tables/table-005.json): per subzone its map,
  Priority and WorldMapFogGroupId (ESubzoneGroupID_<n>; the world map's clouds).

OBSERVED (official capture 20261013-144836): the server sends 0x56ac OpenSubzoneGroupIDs_NT at world
entry (message 202, `01 3f`: a count and one byte per group) and 0x56ad AddSubzoneGroupID_NT (one byte)
when a group is uncovered (messages 2396 and 9344: 65, 64). The 0x9501 list of message 375 (36 i32s,
2..54), earlier taken for the fog, is another message.

    python tools/subzones.py build    # writes tools/data/subzones.json
    python tools/subzones.py defaults <Map.dat export table-NNN.json>   # adds Map.dat DefaultSubzoneID per map

Map.dat DefaultSubzoneID is the subzone of a map outside its volumes; layers and most dungeons have no volume of their own
(OBSERVED: the official appearance on layer 101001 carries 2001, on 101006 2013 = their DefaultSubzoneID).
"""
import argparse
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

MAPDATA = ROOT / 'research/local/all-mapdata-full'
SUBZONE_TABLE = ROOT / 'research/local/ascension-flight-tables/table-005.json'
DATA = ROOT / 'tools/data/subzones.json'


def fog_group(value):
    """ESubzoneGroupID::ESubzoneGroupID_64 -> 64; None/unset -> 0."""
    if not isinstance(value, str) or '_' not in value:
        return 0
    tail = value.rsplit('_', 1)[1]
    return int(tail) if tail.isdigit() else 0


def subzone_rows(table):
    """Subzone id -> (fog group or 0, priority, UseRide) for every row."""
    rows = table['Properties']['Data']
    return {row['ID']['Value']: (fog_group(row.get('WorldMapFogGroupId')), row.get('Priority', 0), bool(row.get('UseRide', True)))
            for row in rows}


def volumes(mapdata, rows):
    """(map id, [subzone areas: name, fog group (0 none), priority, heights, polygon]) of one decoded MapData table."""
    data = mapdata['Properties']['Data']
    areas = []
    for entry in data.get('SubzoneVolumeInfoMap', []):
        value = entry['Value']
        subzone = value.get('SubzoneTableId')
        if subzone not in rows or not value.get('Points'):
            continue
        group, priority, use_ride = rows[subzone]
        areas.append({'subzone': subzone, 'name': value.get('LabelName') or entry['Key'], 'group': group, 'priority': priority,
                      'use_ride': use_ride,
                      'z': [round(value.get('HeightMin', -1e9), 1), round(value.get('HeightMax', 1e9), 1)],
                      'points': [[round(p['X'], 1), round(p['Y'], 1)] for p in value['Points']]})
    return data['MapId'], areas


def build(source=MAPDATA, table=SUBZONE_TABLE):
    rows = subzone_rows(json.loads(table.read_text(encoding='utf-8')))
    maps = {}
    for path in sorted(source.glob('*/table-*.json')):
        if not path.stem.split('-')[-1].isdigit():
            continue
        map_id, areas = volumes(json.loads(path.read_text(encoding='utf-8')), rows)
        if areas:
            maps.setdefault(str(map_id), []).extend(areas)
    groups = sorted({g for g, _, _ in rows.values() if g})
    return {'source': 'MapData.dat SubzoneVolumeInfoMap + Subzone.dat WorldMapFogGroupId', 'groups': groups, 'maps': maps}


def map_defaults(map_table):
    """Map id -> Map.dat DefaultSubzoneID (only maps that name one)."""
    rows = json.loads(Path(map_table).read_text(encoding='utf-8'))['Properties']['Data']
    return {str(row['ID']['Value']): row['DefaultSubzoneID'] for row in rows if row.get('DefaultSubzoneID')}


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', choices=['build', 'defaults'])
    parser.add_argument('map_table', nargs='?', type=Path, help='Map.dat export (defaults)')
    args = parser.parse_args()
    if args.command == 'defaults':
        if not args.map_table:
            parser.error('defaults needs the Map.dat export table')
        out = json.loads(DATA.read_text(encoding='utf-8'))
        out['defaults'] = map_defaults(args.map_table)
        write_data_json(DATA, out)
        print(len(out['defaults']), 'map default subzones ->', DATA.relative_to(ROOT))
        return
    out = build()
    if 'defaults' in (old := json.loads(DATA.read_text(encoding='utf-8')) if DATA.exists() else {}):
        out['defaults'] = old['defaults']   # a volume rebuild keeps the Map.dat defaults
    write_data_json(DATA, out)
    print(len(out['maps']), 'maps,', sum(len(v) for v in out['maps'].values()), 'areas,', len(out['groups']), 'fog groups ->', DATA.relative_to(ROOT))


if __name__ == '__main__':
    main()
