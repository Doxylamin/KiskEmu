"""Toggle skills (mantras, auras) for the server: the abnormal a toggle puts on and the System skill it pulses.

From an offline WorldProbe export of Skill.dat and SkillEffect.dat (research/local/skill-tables-YYYYMMDD, table-000.json and
table-001.json, the selection research/local/sel-tables.json):

- a Skill.dat row with SkillSubType Toggle names its abnormal in ToggleOnAbnormalId (Sprint Mantra 18160000: 181600001; its
  SkillEffect row is the Toggle [181600001, 1]);
- the System skill one id up (18160001, same SkillString_Key) is its pulse: its SkillEffect Abnormal_True row
  [duration min, max, abnormal, level] (181600011 for 2400 ms) and its SkillEffectFilterId (180000101, the caster and party).

OBSERVED official 2026-11-15 (game version 3533): the toggle's abnormal with duration -1, then the pulse skill about every
1000 ms (0x3802, 0x3806, 0x3804, then the pulse abnormal added or refreshed). The pulse interval is not in these tables.

    python tools/toggle_skills.py research/local/skill-tables-20261109   # writes tools/data/toggle-skills.json
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from data_json import write_data_json  # noqa: E402

DATA = ROOT / 'tools/data/toggle-skills.json'
PULSE_MS = 1000   # OBSERVED for Sprint Mantra (2026-11-15: 1967 refreshes, 1000 +- 3 ms apart)


def _value(row, key):
    v = row.get(key)
    return v.get('Value') if isinstance(v, dict) else v


def build(skill_rows, effect_rows):
    skills = {_value(r, 'ID'): r for r in skill_rows}
    groups = {}
    for e in effect_rows:
        groups.setdefault(_value(e, 'SkillEffectGroupId'), []).append(e)
    out = {}
    for skill_id, row in sorted(skills.items()):
        if row.get('SkillSubType') != 'ESkillSubType::Toggle':
            continue
        entry = {'abnormal': _value(row, 'ToggleOnAbnormalId') or 0}
        pulse = skills.get(skill_id + 1)
        if pulse and pulse.get('SkillType') == 'ESkillType::System' and pulse.get('SkillString_Key') == row.get('SkillString_Key'):
            for slot in pulse.get('SkillEffectTimeDataList', []):
                for e in groups.get(_value(slot, 'SkillEffectGroupId'), []):
                    if e.get('EffectType') == 'ESkillEffectType::Abnormal_True':
                        values = e.get('EffectValueList', [])
                        entry.update({'pulse_skill': skill_id + 1, 'pulse_abnormal': int(values[2]), 'pulse_duration_ms': int(values[1]),
                                      'pulse_filter': _value(slot, 'SkillEffectFilterId'), 'pulse_every_ms': PULSE_MS})
                        break
                if 'pulse_skill' in entry:
                    break
        out[str(skill_id)] = entry
    return out


def main(argv):
    if len(argv) != 2:
        raise SystemExit(__doc__)
    folder = Path(argv[1])
    skill_rows = json.loads((folder / 'table-000.json').read_text(encoding='utf-8'))['Properties']['Data']
    effect_rows = json.loads((folder / 'table-001.json').read_text(encoding='utf-8'))['Properties']['Data']
    data = {
        'source': 'Skill.dat ToggleOnAbnormalId; the System skill id + 1 and its SkillEffect Abnormal_True row (tools/toggle_skills.py)',
        'skills': build(skill_rows, effect_rows),
    }
    write_data_json(DATA, data)
    print(f"{len(data['skills'])} toggle skills, {sum('pulse_skill' in s for s in data['skills'].values())} with a pulse -> {DATA}")


if __name__ == '__main__':
    main(sys.argv)
