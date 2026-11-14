"""Skill projectile flights from the client's SkillProjectile.dat (tools/data/projectiles.json).

Writes, per SkillProjectile.dat row id, [EProjectileType, Speed, ArrivalFixTimeMin, ArrivalFixTimeMax, CollideMoveValue01].
The server times each 0x3803 flight by the projectile's type (server/Aion.World/Projectiles.cs).

Ported from the A2Core fork (Addiboii, commit caa5f3f, tools/projectile_data.py); the tracked file is that fork's export
of game version 3533 (Steam build 26262663). Its findings over its recordings (every 0x3803 SkillFireProjectile_NT and
its announced flight), OBSERVED there:
- Splash_FixTime and Splash_FixTime_Target: within ArrivalFixTimeMin..Max (1045 of 1045 and 23 of 23);
- Collide: CollideMoveValue01 / Speed (597 of 597, within 50 ms);
- every flight is a multiple of 50 ms, and the hit lands 0-99 ms after the announced flight.
CANDIDATE: Target and the VariableTime types fly the distance / Speed.

Input (WorldProbe export, research/local; never committed): the SkillProjectile.dat table that tools/skill_areas.py
reads (research/local/skillfilter-20261030/table-002.json), or --table.

    python tools/projectile_data.py [--table <SkillProjectile.dat export>] [--version 3533] [--check]
"""
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'tools/data/projectiles.json'


def _default_table():
    from skill_areas import PROJECTILES
    return PROJECTILES


def _short(value):
    return str(value).split('::')[-1]


def _value(v):
    return v.get('Value') if isinstance(v, dict) else v


def convert(rows):
    """{id: [type, speed, fix min, fix max, collide travel]} for every projectile row with an id."""
    out = {}
    for row in rows:
        pid = int(_value(row.get('ID')) or 0)
        if pid:
            out[pid] = [_short(row.get('ProjectileType', 'None')), int(row.get('Speed') or 0), int(row.get('ArrivalFixTimeMin') or 0),
                        int(row.get('ArrivalFixTimeMax') or 0), int(row.get('CollideMoveValue01') or 0)]
    return out


def _rows(table):
    doc = json.loads(Path(table).read_text(encoding='utf-8'))
    return doc['Properties']['Data'] if isinstance(doc, dict) and 'Properties' in doc else doc


def build(table, version='3533'):
    return {'version': version, 'source': 'client SkillProjectile.dat',
            'projectiles': {str(k): v for k, v in sorted(convert(_rows(table)).items())}}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--table', type=Path, default=None, help='SkillProjectile.dat export (WorldProbe JSON)')
    parser.add_argument('--version', default='3533', help='game version of the export')
    parser.add_argument('--check', action='store_true', help='exit 1 when the tracked file differs')
    args = parser.parse_args(argv)
    table = args.table or _default_table()
    if not Path(table).is_file():
        raise SystemExit(f'a SkillProjectile.dat export is needed: {table} (use --table or AION_RESEARCH_LOCAL)')
    doc = build(table, args.version)
    if args.check:
        same = OUT.is_file() and json.loads(OUT.read_text(encoding='utf-8'))['projectiles'] == doc['projectiles']
        print('up to date' if same else f'{OUT.name} needs tools/projectile_data.py')
        raise SystemExit(0 if same else 1)
    write_data_json(OUT, doc)
    print(len(doc['projectiles']), 'projectiles ->', OUT.relative_to(ROOT))


if __name__ == '__main__':
    main()
