"""Skill.dat NeedSkillUseRange (cm) of the player skills, for the server's cast-range check.

Writes tools/data/skill-cast-ranges.json from a decoded Skill.dat table export (the cast range check in
server/Aion.World/GameSession.SkillTargeting.cs). Skills without a range (0: self, toggles) are left out, so the
server does not check them.

usage: python tools/skill_cast_ranges.py [path/to/Skill.dat table json]
Default input: research/local/skill-tables-20261109/table-000.json (Skill.dat, version 13).

Idea and the server check: A2Core-ChanAge (Artezianec), which guessed the ranges; these are the table's values.
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_TABLE = ROOT / 'research/local/skill-tables-20261109/table-000.json'
OUT = ROOT / 'tools/data/skill-cast-ranges.json'
PLAYER_SKILLS = range(11_000_000, 19_000_000)   # class prefixes 11-18 (Gladiator .. Chanter)


def _find(node, key):
    """The first list under `key` anywhere in a decoded table (the exports nest it differently)."""
    if isinstance(node, dict):
        if isinstance(node.get(key), list):
            return node[key]
        for value in node.values():
            found = _find(value, key)
            if found is not None:
                return found
    elif isinstance(node, list):
        for value in node:
            found = _find(value, key)
            if found is not None:
                return found
    return None


def ranges(rows):
    out = {}
    for row in rows:
        skill_id = row['ID']['Value']
        use = row.get('NeedSkillUseRange')
        if skill_id in PLAYER_SKILLS and use:
            out[str(skill_id)] = round(float(use), 1)
    return dict(sorted(out.items(), key=lambda kv: int(kv[0])))


def main(argv):
    table = Path(argv[1]) if len(argv) > 1 else DEFAULT_TABLE
    rows = _find(json.loads(table.read_text(encoding='utf-8')), 'Data') or []
    skills = ranges(rows)
    OUT.write_text(json.dumps({'note': 'Skill.dat NeedSkillUseRange (cm) of player skills; 0 (no range) left out',
                               'skills': skills}, indent=1) + '\n', encoding='utf-8')
    print(f'{len(skills)} player skills with a cast range -> {OUT.relative_to(ROOT)}')


if __name__ == '__main__':
    main(sys.argv)
