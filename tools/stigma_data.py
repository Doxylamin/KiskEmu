"""Stigma skills for the server: which skills, their level costs, the stigma point pool and the specialized parts.

Derived from an offline WorldProbe export of SkillAcquireData.dat, Exp.dat and SpecializedSkillParts.dat (selection
["AION2/Content/Data/Table/SkillAcquireData.dat", "AION2/Content/Data/Table/Exp.dat",
 "AION2/Content/Data/Table/SpecializedSkillParts.dat"]) plus the game catalogue for the cast rows. Rebuild:

  WorldProbe <Paks> <key> <oodle> <usmap> <selection.json> research/local/stigma-tables-YYYYMMDD
  python tools/stigma_data.py research/local/stigma-tables-YYYYMMDD

Writes tools/data/stigma-skills.json:
- classes: class -> stigma skill ids (SkillAcquireData AcquireType Stigma, SkillLevel 1).
- levels: skill -> [level, CostStigmaPoint, NeedCharacterLevel, NeedAscensionGrade] for levels 1..20 (1/2/4/8 per level).
- points: character level -> Exp.dat BonusStigmaPoint (cumulative, like BonusSkillPoint: 1 at 23, 29 at 45).
- parts: skill -> [child skill, ParentSkillLv, InitEquipSlotType] (SpecializedSkillParts: +10/+20/+30/+40 at 5/10/15/20).
- skills: cast rows (tools/class_skills.cast_row) for every Skill.dat id of a stigma family (17400000..17409999).
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

DATA = ROOT / 'tools/data/stigma-skills.json'
CATALOG = ROOT / 'research/local/game-catalog-v2/entries.jsonl'
CLASS_SKILLS = ROOT / 'tools/data/class-skills.json'


def _short(value):
    return str(value).split('::')[-1]


def _grade(value):
    """EAscensionGrade::AscensionGrade_3 -> 3 (None -> 0)."""
    tail = _short(value)
    return int(tail.rsplit('_', 1)[-1]) if tail.startswith('AscensionGrade_') else 0


def load_tables(tables: Path) -> dict:
    summary = json.loads((tables / 'summary.json').read_text(encoding='utf-8'))
    out = {}
    for package in summary['packages']:
        if package.get('status') == 'table_decoded':
            out[Path(package['path']).stem] = json.loads((tables / package['file']).read_text(encoding='utf-8'))['Properties']['Data']
    return out


def build(tables: Path, catalog: Path = CATALOG) -> dict:
    t = load_tables(tables)
    acquire = [r for r in t['SkillAcquireData'] if _short(r['AcquireType']) == 'Stigma']
    classes, levels = {}, {}
    for row in sorted(acquire, key=lambda r: (r['SkillId']['Value'], r['SkillLevel'])):
        skill = row['SkillId']['Value']
        if row['SkillLevel'] == 1:
            classes.setdefault(_short(row['ClassType']), []).append(skill)
        levels.setdefault(str(skill), []).append(
            [row['SkillLevel'], row['CostStigmaPoint'], row['NeedCharacterLevel'], _grade(row['NeedAscensionGrade'])])
    stigma = {int(s) for s in levels}
    parts = {}
    for row in t['SpecializedSkillParts']:
        parent = row['ParentSkillId']['Value']
        if parent in stigma:
            slot = _short(row['InitEquipSlotType'])
            parts.setdefault(str(parent), []).append(
                [row['ID']['Value'], row['ParentSkillLv'], 1 if slot == 'Slot' else int(slot.rsplit('_', 1)[-1])])
    for rows in parts.values():
        rows.sort(key=lambda r: (r[1], r[0]))
    points = {str(r['Level']): int(r.get('BonusStigmaPoint') or 0) for r in t['Exp']}
    skills = {}
    if catalog.exists():
        from class_skills import cast_row
        families = {s // 10_000 for s in stigma}
        # The class kit already defines a few of these ids (the server reads class-skills.json first).
        known = set(json.loads(CLASS_SKILLS.read_text(encoding='utf-8'))['skills']) if CLASS_SKILLS.exists() else set()
        with open(catalog, encoding='utf-8') as source:
            for line in source:
                entry = json.loads(line)
                if entry['kind'] == 'skills' and entry['id'] // 10_000 in families and str(entry['id']) not in known:
                    skills[str(entry['id'])] = cast_row(entry)
    return {'source': 'SkillAcquireData / Exp / SpecializedSkillParts .dat (tools/stigma_data.py)',
            'classes': {k: sorted(v) for k, v in sorted(classes.items())}, 'levels': levels, 'points': points,
            'parts': parts, 'skills': dict(sorted(skills.items()))}


def main(argv=None):
    argv = sys.argv[1:] if argv is None else argv
    if not argv:
        raise SystemExit(__doc__)
    data = build(Path(argv[0]))
    write_data_json(DATA, data)
    print(f"{sum(len(v) for v in data['classes'].values())} stigma skills, {len(data['skills'])} cast rows, "
          f"{len(data['parts'])} with parts -> {DATA.relative_to(ROOT)}")


if __name__ == '__main__':
    main()
