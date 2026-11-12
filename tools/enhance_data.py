"""Mana stone imprinting and soul binding data for the server (tools/data/item_enhance.json).

Derived from offline exports (WorldProbe, research/local/enhance-tables-20261112): SoulBindCost (table-000) and
ImprintMagicStoneProb (table-003), linked from Item.dat (research/local/game-catalog-v2). EStat names map to wire ids
through tools/data/item-stat-wire.json. Rebuild with `python tools/enhance_data.py [research/local directory]`.

- items: equipment ID -> [mana stone slots, imprint Kina, [mana stone groups], soul bind cost name] (Item.dat
  MagicStoneSlotCount, ImprintMagicStoneCostGold, MagicStoneGroups, SoulbindCostName).
- magicstones: mana stone item ID -> [mana stone group, imprint group] (UseEffect ImprintMagicStone values 2 and 1).
- imprint_groups: imprint group -> [[EStat wire id, weight /10000, [[grade 1 Common | 2 Rare | 3 Legend, prob /10000, value], ...]], ...].
  The official 0x562a (2026-11-12) wrote stat 128 Critical / grade 1 = 6, 199 MPMax / 2 = 40, 199 / 1 = 23, 133
  CriticalResist / 2 = 9 from Intermediate Manastone (ms_30_common_normal): those are exactly this table's values.
- soulbind_costs: cost name -> [[Kina, item ID, count] per bind step (step n raises the bind count from n to n + 1)].
- soulbind_reroll: cost name -> [Kina, item ID, count] (Attune).
"""
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/item_enhance.json'
WIRE = ROOT / 'tools/data/item-stat-wire.json'
GRADES = {'EItemGrade::Common': 1, 'EItemGrade::Rare': 2, 'EItemGrade::Legend': 3, 'EItemGrade::Unique': 4}
KINA = 930100001


def _rows(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))['Properties']['Data']


def build(research):
    tables = research / 'enhance-tables-20261112'
    wire = json.loads(WIRE.read_text(encoding='utf-8'))['by_name']
    ids, raws = {}, {}
    with open(research / 'game-catalog-v2/entries.jsonl', encoding='utf-8') as source:
        for line in source:
            entry = json.loads(line)
            if entry.get('kind') != 'items':
                continue
            ids[entry['internal_name']] = entry['id']
            raws[entry['id']] = entry.get('raw') or {}

    def item_id(name):
        return KINA if name == 'GoldCombined' else ids.get(name, 0)

    items, stones = {}, {}
    for item, raw in sorted(raws.items()):
        if raw.get('ItemType') == 'EItemType::Equip':
            slots = int(raw.get('MagicStoneSlotCount') or 0)
            cost = raw.get('SoulbindCostName') or 'None'
            if slots > 0 or cost != 'None':
                items[str(item)] = [slots, int(raw.get('ImprintMagicStoneCostGold') or 0), raw.get('MagicStoneGroups') or [], cost]
        elif raw.get('UseEffect') == 'EItemUseEffect::ImprintMagicStone':
            values = raw.get('UseEffectValues') or []
            if len(values) >= 3:
                stones[str(item)] = [values[2], values[1]]

    groups = {}
    for row in _rows(tables / 'table-003.json'):
        stat = wire.get(row['AddStatType'].removeprefix('EStat::'))
        if not stat:
            continue
        infos = [[GRADES[i['StatGrade']], int(i['StatProb']), int(i['StatValue'])] for i in row['StatInfos'] if i['StatGrade'] in GRADES]
        groups.setdefault(row['ImprintGroupName'], []).append([stat, int(row['AddStatProb']), infos])

    costs, reroll = {}, {}
    for row in _rows(tables / 'table-000.json'):
        costs[row['Name']] = [[int(c['CostA_Amount']) if c['CostA_Id'] == 'GoldCombined' else 0, item_id(c['CostB_Id']), int(c['CostB_Amount'])]
                              for c in row['CostDatas']]
        reroll[row['Name']] = [int(row['RerollCostCount']), item_id(row['RerollCostItem']), int(row['RerollCostItemCount'])]
    return {'items': items, 'magicstones': stones, 'imprint_groups': groups, 'soulbind_costs': costs, 'soulbind_reroll': reroll}


def main():
    research = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / 'research/local'
    data = build(research)
    DATA.write_text(json.dumps(data, separators=(',', ':')) + '\n', encoding='utf-8')
    print(f"item_enhance.json: {len(data['items'])} items, {len(data['magicstones'])} mana stones, "
          f"{len(data['imprint_groups'])} imprint groups, {len(data['soulbind_costs'])} soul bind costs")


if __name__ == '__main__':
    main()
