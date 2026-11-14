"""Skill specialisation (parts of mastery skills) -> tools/data/specialized-skills.json.

Ported from addiboii's community fork (tools/specialized_skills.py, fork commits d49f701 and bea457b), reading our own
WorldProbe exports instead of the fork's layout:

  WorldProbe <Paks> <key> <oodle> <usmap> <selection.json> research/local/stigma-tables-YYYYMMDD        (SpecializedSkillSlot.dat)
  WorldProbe ... research/local/stigma-tables-YYYYMMDD/spec                                                (SpecializedSkill.dat, Parts)
  python tools/specialized_skills.py research/local/stigma-tables-20261112 research/local/stigma-tables-20261112/spec

Every directory's summary.json names its tables; the first one found of each name is used.
- SpecializedSkillParts.dat: per part its parent skill, the parent level it needs and InitEquipSlotType (None for the parts a
  player chooses; Slot_1..4 for a stigma's parts, which follow the stigma's level: tools/data/stigma-skills.json).
- SpecializedSkillSlot.dat: per SkillAcquireType the slots and the parent level each opens at (Mastery: Slot_1-3 at 8, 12, 20,
  editable; Stigma: Slot_1-5, not editable).
- SpecializedSkill.dat: every child skill of a mastery parent (5 single parts, 10 pairs and 10 triples).
- tools/data/class-skills.json (Skill.dat cast rows, already carrying every child): the chain steps that follow a child
  (chain_next: 11020340 -> 11030340 -> 11040340), written per step under chain_children.

Labels: every number is OBSERVED (read from the named table). OBSERVED in the community recordings of 2026-11-04 to 06 (fork
notes): the child the client casts is the parent + the equipped parts' digits ascending (parts 30 and 40 of 11020000 ->
11020340), and SpecializedSkill.dat lists exactly those ids; the chain steps' children appear in 0x5100 (11030340, 11040340).
Cast rows: class-skills.json already carries the children that chain (11020340 ...); the others are written here from the game
catalogue (research/local/game-catalog-v2, tools/class_skills.cast_row), as the fork does; missing_skills lists any in neither.
"""
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

DATA = ROOT / 'tools/data/specialized-skills.json'
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'
# A worktree points at the main checkout's research/local with AION_RESEARCH_LOCAL.
CATALOG = Path(os.environ.get('AION_RESEARCH_LOCAL', ROOT / 'research/local')) / 'game-catalog-v2/entries.jsonl'


def _short(value):
    return str(value).split('::')[-1]


def _value(v):
    return v.get('Value') if isinstance(v, dict) else v


def load_tables(dirs) -> dict:
    """Table name (SpecializedSkill, ...) -> rows, from the WorldProbe summary.json of each directory."""
    out = {}
    for tables in map(Path, dirs):
        summary = json.loads((tables / 'summary.json').read_text(encoding='utf-8'))
        for package in summary['packages']:
            name = Path(package['path']).stem
            if package.get('status') == 'table_decoded' and name not in out:
                out[name] = json.loads((tables / package['file']).read_text(encoding='utf-8'))['Properties']['Data']
    return out


def slot_levels(rows, acquire_type):
    """[parent level that opens Slot_1, Slot_2, ...] of one SkillAcquireType."""
    found = sorted((int(_short(r['SlotType']).split('_')[1]), int(r['UnlockSkillLv'])) for r in rows
                   if _short(r['SkillAcquireType']) == acquire_type)
    if [n for n, _ in found] != list(range(1, len(found) + 1)):
        raise ValueError(f'{acquire_type} slots are not Slot_1..N: {found}')
    return [level for _, level in found]


def child_id(parent, parts):
    """The skill the client casts with these parts equipped (parent + digits ascending, OBSERVED rule)."""
    digits = sorted((p - parent) // 10 for p in parts)
    return parent + sum(d * 10 ** (len(digits) - i) for i, d in enumerate(digits))


def chain_children(children, skills):
    """Each child's chain steps (class-skills.json chain_next, recursively), per step id (the id with the parts digits cleared)."""
    out = {}
    todo = [c for kids in children.values() for c in kids]
    seen = set(todo)
    while todo:
        for step in skills.get(str(todo.pop()), {}).get('chain_next') or []:
            if step in seen:
                continue
            seen.add(step)
            todo.append(step)
            out.setdefault(str(step // 10_000 * 10_000), []).append(step)
    return {p: sorted(k) for p, k in sorted(out.items())}


def cast_rows(ids, skills, catalog):
    """Skill.dat cast rows (tools/class_skills.cast_row) of the children class-skills.json does not carry, named after their parent."""
    from class_skills import cast_row
    wanted = {int(i) for i in ids}
    out = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'skills' and entry['id'] in wanted:
                row = cast_row(entry)
                base = skills.get(str(entry['id'] // 10_000 * 10_000))
                if base:
                    # As class_skills.merge_chain_skills: table rows carry a string key and no linked effects; the parent names them.
                    if not row.get('name') or str(row['name']).startswith('STR_SKILL'):
                        row['name'] = base.get('name')
                    if not row['damage'] and base.get('damage'):
                        row['damage'] = base['damage']
                out[str(entry['id'])] = row
    return dict(sorted(out.items()))


def build(dirs, class_skills=CLASS_SKILLS, catalog=CATALOG) -> dict:
    t = load_tables(dirs)
    mastery = {}
    for r in t['SpecializedSkillParts']:
        if _short(r['InitEquipSlotType']) != 'None':
            continue
        mastery.setdefault(str(_value(r['ParentSkillId'])), []).append([_value(r['ID']), int(r['ParentSkillLv'])])
    for entries in mastery.values():
        entries.sort()
    children = {}
    for r in t['SpecializedSkill']:
        parent = str(_value(r['ParentSkillId']))
        if parent in mastery:
            children.setdefault(parent, []).append(_value(r['ChildSkillId']))
    children = {p: sorted(k) for p, k in sorted(children.items())}
    skills = json.loads(Path(class_skills).read_text(encoding='utf-8'))['skills']
    chains = chain_children(children, skills)
    wanted = {c for kids in list(children.values()) + list(chains.values()) for c in kids}
    cast = cast_rows([c for c in wanted if str(c) not in skills], skills, catalog) if Path(catalog).exists() else {}
    return {
        'source': 'SpecializedSkill / SpecializedSkillParts / SpecializedSkillSlot .dat and class-skills.json chain links '
                  '(tools/specialized_skills.py, ported from addiboii)',
        'slot_levels': slot_levels(t['SpecializedSkillSlot'], 'Mastery'),
        'stigma_slots': len(slot_levels(t['SpecializedSkillSlot'], 'Stigma')),
        'parts': dict(sorted(mastery.items())),
        'children': children,
        'chain_children': chains,
        'skills': cast,
        'missing_skills': sorted(c for c in wanted if str(c) not in skills and str(c) not in cast),
    }


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    if not argv:
        raise SystemExit(__doc__)
    data = build(argv)
    write_data_json(DATA, data)
    print(f"{len(data['parts'])} specialisable skills, slots open at {data['slot_levels']}, "
          f"{sum(map(len, data['children'].values()))} children, {sum(map(len, data['chain_children'].values()))} chain children, "
          f"{len(data['skills'])} cast rows, missing {len(data['missing_skills'])} -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
