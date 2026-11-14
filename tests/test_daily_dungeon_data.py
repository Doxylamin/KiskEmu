import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import daily_dungeon_data  # noqa: E402

DATA = json.loads((ROOT / 'tools/data/daily-dungeons.json').read_text(encoding='utf-8'))


class DailyDungeonDataTests(unittest.TestCase):
    def test_build_links_points_waves_and_reward_tiers(self):
        dungeons = [{'ID': {'Value': 900001}, 'Name': 'DungeonWave_0001', 'DungeonType': 'EDungeonType::Daily',
                     'DungeonSubType': 'EDungeonSubType::Wave', 'DungeonPointGroupName': 'G', 'DungeonWaveGroupName': 'W',
                     'RewardLight': 'RL', 'RewardDark': 'RD', 'ShowDungeonPoint': True,
                     'ResurrectionType': 'EResurrectionType::Restart'},
                    {'ID': {'Value': 1}, 'Name': 'Other', 'DungeonType': 'EDungeonType::Party'}]
        points = [{'Group': 'G', 'DungeonPointType': 'EDungeonPointType::KillNpc', 'TableName': 'M_A', 'Point': 40},
                  {'Group': 'G', 'DungeonPointType': 'EDungeonPointType::KillNpcSubType',
                   'NpcSubType': 'ENpcSubType::EliteMonster', 'TableName': 'None', 'Point': 1000},
                  {'Group': 'X', 'DungeonPointType': 'EDungeonPointType::KillNpc', 'TableName': 'M_A', 'Point': 5}]
        rewards = [{'Group': 'RL', 'NeedPoint': 100, 'Items': [{'Item': 'CoinEnchant', 'Count': 3000}]},
                   {'Group': 'RL', 'NeedPoint': 1500, 'Items': [{'Item': 'CoinEnchant', 'Count': 4300}]}]
        waves = [{'ID': {'Value': 85}, 'Group': 'W'}]
        npcs = [{'ID': {'Value': 2701418}, 'Name': 'M_A'}]
        out = daily_dungeon_data.build(dungeons, points, rewards, waves, npcs, {'CoinEnchant': 930100023})
        self.assertEqual(['900001'], list(out))
        entry = out['900001']
        self.assertEqual(85, entry['wave'])
        self.assertEqual(90, entry['seconds'])
        self.assertEqual('Restart', entry['resurrection'])
        self.assertEqual({'2701418': 40}, entry['kill_points'])
        self.assertEqual({'EliteMonster': 1000}, entry['subtype_points'])
        self.assertEqual([1500, 100], [t['need'] for t in entry['reward_light']])
        self.assertEqual([], entry['reward_dark'])

    def test_tracked_data_pays_the_official_reward(self):
        wave = DATA['dungeons']['900001']
        tier = next(t for t in wave['reward_light'] if 1730 >= t['need'])   # sorted highest first
        self.assertEqual([[930100023, 4300]], tier['items'])
        self.assertEqual(50, wave['kill_points']['2701423'])
        self.assertEqual(85, wave['wave'])


if __name__ == '__main__':
    unittest.main()
