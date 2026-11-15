"""The better grade of each gathered material, for a gather's discovery (GatherSourceStart_RS _is_discovered).

OBSERVED (official recording 2026-11-15, 15 gathers): a discovered Odyle gather ends with GatherSourceCompleted_NT
result 1 and Fine Odyle (610610014) instead of Odyle (610610013). The client's item names pair them: the common
material is Material_Gather_<kind>_c_<n>_f and its better grade Material_Gather_<kind>_r_<n>_f (Odyle -> Fine Odyle,
Aria -> Fresh Aria, Orichalcum Ore -> Fine Orichalcum Ore). Only the Odyle pair is OBSERVED; the others follow the
names (CANDIDATE).

Source: research/local/game-catalog-v2/entries.jsonl (the item catalog, internal names).

    python tools/gather_grades.py build    # writes tools/data/gather-grades.json
"""
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
DATA = ROOT / 'tools/data/gather-grades.json'


def better_grades(items):
    """{common material id: better grade id} from (id, internal name) pairs."""
    by_name = {name: item_id for item_id, name in items}
    out = {}
    for item_id, name in items:
        if not name.startswith('Material_Gather_') or '_c_' not in name:
            continue
        better = by_name.get(name.replace('_c_', '_r_', 1))
        if better:
            out[item_id] = better
    return dict(sorted(out.items()))


def read_catalog(path):
    items = []
    with open(path, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'items' and entry.get('internal_name'):
                items.append((entry['id'], entry['internal_name']))
    return items


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument('command', choices=['build'])
    parser.add_argument('--catalog', type=Path, default=CATALOG)
    parser.add_argument('--out', type=Path, default=DATA)
    args = parser.parse_args()
    grades = better_grades(read_catalog(args.catalog))
    write_data_json(args.out, {
        'source': 'tools/gather_grades.py: the item catalog, Material_Gather_*_c_* -> *_r_* (Odyle -> Fine Odyle OBSERVED 2026-11-15)',
        'better': {str(k): v for k, v in grades.items()},
    })
    print(f'{len(grades)} materials -> {args.out}')


if __name__ == '__main__':
    main()
