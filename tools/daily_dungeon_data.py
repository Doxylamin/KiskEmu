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

The countdown length is not in the client tables (Dungeon.dat ProcessStateHoldingTime is 0); it is OBSERVED in the
official capture of 2026-11-12 for 900001 only (0x6014 ends 90 s after the Process state starts), see SECONDS.

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

# OBSERVED (official 2026-11-12, 900001): the countdown runs 90 s from the Process state. Not in the client data.
SECONDS = {'DungeonWave_0001': 90}


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


def build(dungeons, points, rewards, waves, npcs, items):
    """Rows (lists of dicts as WorldProbe writes them) -> {dungeon id: entry}."""
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
        out[str(_id(row))] = entry
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--research', default=str(ROOT / 'research/local'))
    args = parser.parse_args()
    research = Path(args.research)
    dungeons = build(_rows(research / DUNGEONS), _rows(research / POINTS), _rows(research / REWARDS), _rows(research / WAVES),
                     _rows(research / NPCS), _item_ids(research / CATALOG))
    data = {'source': 'Dungeon.dat, DungeonPoint.dat, WaveData.dat, RewardDungeon.dat, NpcData.dat (3533 tables; '
                      'tools/daily_dungeon_data.py). seconds: OBSERVED in the official capture of 2026-11-12, not in the tables.',
            'dungeons': dungeons}
    DATA.write_text(json.dumps(data, indent=1, separators=(',', ': ')) + '\n', encoding='utf-8')
    print(f'Wrote {DATA.relative_to(ROOT)} ({len(dungeons)} dungeons)')


if __name__ == '__main__':
    main()
