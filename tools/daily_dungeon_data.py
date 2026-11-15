"""Daily dungeons (Dungeon.dat DungeonType Daily) for the server: the countdown wave, the kill points and the point rewards.

Sources (WorldProbe table exports under research/local, 3533 client tables):
- Dungeon.dat: DungeonPointGroupName, DungeonWaveGroupName, RewardLight / RewardDark (a RewardDungeon.dat *group*).
- DungeonPoint.dat: points per kill, KillNpc by NpcData name or KillNpcSubType by ENpcSubType (UseEnvObj rows are kept
  apart: not counted by the server yet).
- WaveData.dat: the wave of the dungeon's DungeonWaveGroupName (one row per group); its id goes on the countdown packet.
- Dungeon.dat ResurrectionType: Restart for the three dailies (Continue for almost every other dungeon).
- RewardDungeon.dat: the group's tiers {NeedPoint, Items}; a run pays the highest tier whose NeedPoint the points reach.
  OBSERVED 2026-11-12 (official, 900001): 1730 points paid 4300 CoinEnchant, the 1500-point tier.
- NpcData.dat (name -> id) and the game catalog (item name -> id).
- DungeonBuff.dat / DungeonBuffGroup.dat: the buff choice at a daily's entry (0x6105 {slot, buff id}); each slot of the
  dungeon's DungeonBuffGroupName may draw any of the group's buffs. Names from the client localisation (STR_DungeonWave_*).
  OBSERVED 2026-11-15 (official, 900001, three runs): the offered ids are DungeonBuff.dat ids, and their names match the
  choice screen (10 Elite Hunter, 6 Ignoring the Weak, 4 Footwork, 1004 Poisoned by Mana, 9 Ignoring the Elite, 1 Slayer,
  7 Worm Carnage, 1006 Targeting Weakness).

The countdown length is not in the client tables (Dungeon.dat ProcessStateHoldingTime is 0); it is OBSERVED in the
official captures (0x6014 ends 90 s after the Process state starts in 900001, 60 s in 900002), see SECONDS.

    python tools/daily_dungeon_data.py [--research research/local]
"""
import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/daily-dungeons.json'
DUNGEONS = 'daily-tables-20261114/table-005.json'
POINTS = 'daily-tables-20261114/table-000.json'
REWARDS = 'daily-reward-tables-20261114/table-000.json'
WAVES = 'wave-tables-20261114/table-000.json'
NPCS = 'daily-npc-20261114/table-000.json'
CATALOG = 'game-catalog-v2/entries.jsonl'
BUFFS = 'daily-tables-20261114/table-002.json'
BUFF_GROUPS = 'daily-tables-20261114/table-003.json'
NAMES = 'l10n-chat-20261112/localization-000.json'

# OBSERVED (official 2026-11-12, 900001): the countdown runs 90 s from the Process state. Not in the client data.
SECONDS = {'DungeonWave_0001': 90}
READY = {}
CLEAR_BONUS = {}
EXTRA_ITEMS = {}


def _enum(value):
    return str(value or '').split('::')[-1]


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def _id(row):
    value = row.get('ID')
    return value.get('Value') if isinstance(value, dict) else value


def _item_ids(catalog):
    ids = {}
    with open(catalog, encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') == 'items':
                ids[entry['internal_name']] = entry['id']
    return ids


def _names(path):
    """The localisation export's strings (String_STR_..._body -> text); empty when the export is missing."""
    try:
        return json.loads(Path(path).read_text(encoding='utf-8')).get('Entries', {})
    except FileNotFoundError:
        return {}


def _buff_slots(group, buffs, buff_groups, names):
    """The buffs a DungeonBuffGroupName offers, per slot: [{slot, buffs: [{id, name, grade, abnormal}]}]."""
    by_name = {b['Name']: b for b in buffs}
    slots = {}
    for row in buff_groups:
        if row.get('Group') != group or row.get('BuffName') not in by_name:
            continue
        b = by_name[row['BuffName']]
        abnormal = int(b.get('BuffValue1') or 0) if _enum(b.get('BuffType')) == 'Abnormal' else 0
        slots.setdefault(int(row.get('BuffSlot') or 0), []).append({
            'id': _id(b), 'name': names.get(f"String_STR_{b['Name']}_body", b['Name']),
            'grade': _enum(b.get('BuffGrade')), 'abnormal': abnormal})
    return [{'slot': slot, 'buffs': sorted(choices, key=lambda c: c['id'])} for slot, choices in sorted(slots.items())]


def build(dungeons, points, rewards, waves, npcs, items, buffs=(), buff_groups=(), names=None):
    """Rows (lists of dicts as WorldProbe writes them) -> {dungeon id: entry}."""
    items = {**EXTRA_ITEMS, **items}
    names = names or {}
    npc_ids = {row['Name']: _id(row) for row in npcs}
    wave_ids = {}
    for row in waves:
        wave_ids.setdefault(row.get('Group'), _id(row))
    tiers = {}
    for row in rewards:
        paid = [[items[i['Item']], int(i.get('Count') or 1)] for i in row.get('Items') or [] if i.get('Item') in items]
        tiers.setdefault(row.get('Group'), []).append({'need': int(row.get('NeedPoint') or 0), 'items': paid})
    out = {}
    for row in dungeons:
        if _enum(row.get('DungeonType')) != 'Daily':
            continue
        group = row.get('DungeonPointGroupName')
        kill, subtype, env = {}, {}, {}
        for p in points:
            if p.get('Group') != group:
                continue
            kind = _enum(p.get('DungeonPointType'))
            if kind == 'KillNpc' and p.get('TableName') in npc_ids:
                kill[str(npc_ids[p['TableName']])] = int(p['Point'])
            elif kind == 'KillNpcSubType':
                subtype[_enum(p.get('NpcSubType'))] = int(p['Point'])
            elif kind == 'UseEnvObj':
                env[p.get('TableName')] = int(p['Point'])
        entry = {'name': row['Name'], 'subtype': _enum(row.get('DungeonSubType')),
                 'wave': wave_ids.get(row.get('DungeonWaveGroupName'), 0),
                 'show_points': bool(row.get('ShowDungeonPoint')),
                 'resurrection': _enum(row.get('ResurrectionType')),
                 'kill_points': dict(sorted(kill.items())), 'subtype_points': subtype, 'env_points': env,
                 'reward_light': sorted(tiers.get(row.get('RewardLight'), []), key=lambda t: -t['need']),
                 'reward_dark': sorted(tiers.get(row.get('RewardDark'), []), key=lambda t: -t['need'])}
        if row['Name'] in SECONDS:
            entry['seconds'] = SECONDS[row['Name']]
        if row['Name'] in READY:
            entry['ready'] = READY[row['Name']]
        if row['Name'] in CLEAR_BONUS:
            entry['clear_bonus'] = CLEAR_BONUS[row['Name']]
        if row.get('DungeonBuffGroupName') not in (None, '', 'None'):
            entry['buff_slots'] = _buff_slots(row['DungeonBuffGroupName'], buffs, buff_groups, names)
        out[str(_id(row))] = entry
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--research', default=str(ROOT / 'research/local'))
    args = parser.parse_args()
    research = Path(args.research)
    dungeons = build(_rows(research / DUNGEONS), _rows(research / POINTS), _rows(research / REWARDS), _rows(research / WAVES),
                     _rows(research / NPCS), _item_ids(research / CATALOG), _rows(research / BUFFS),
                     _rows(research / BUFF_GROUPS), _names(research / NAMES))
    data = {'source': 'Dungeon.dat, DungeonPoint.dat, WaveData.dat, RewardDungeon.dat, NpcData.dat, DungeonBuff.dat, '
                      'DungeonBuffGroup.dat (3533 tables; tools/daily_dungeon_data.py). seconds, ready, clear_bonus: OBSERVED '
                      'in the official captures of 2026-11-12 and 2026-11-15, not in the tables.',
            'dungeons': dungeons}
    DATA.write_text(json.dumps(data, indent=1, separators=(',', ': ')) + '\n', encoding='utf-8')
    print(f'Wrote {DATA.relative_to(ROOT)} ({len(dungeons)} dungeons)')


if __name__ == '__main__':
    main()
