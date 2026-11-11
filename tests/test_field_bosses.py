import json
import tempfile
import unittest
from pathlib import Path

from tools import field_bosses


class FieldBossTests(unittest.TestCase):
    def test_map_comes_from_the_code_or_the_spawning_map(self):
        with tempfile.TemporaryDirectory() as tmp:
            npcs = Path(tmp) / 'world-npcs.json'
            npcs.write_text(json.dumps({'maps': {
                '1010': [['M_Named', 2100003, 0, 0, 0, 0, 1, 0, -1]],
                '1011': [['M_Region', 2101217, 0, 0, 0, 0, 2, 0, -1]],
            }}), encoding='utf-8')
            bosses, missing = field_bosses.build({'101001': 2100003, '2101217': 2101217, '101002': 999}, npcs)
        self.assertEqual(bosses, [[101001, 1010, 2100003], [2101217, 1011, 2101217]])
        self.assertEqual(missing, [101002])

    def test_period_spawn_rows_become_schedules(self):
        day = {f'b{d}': d in ('Sunday', 'Friday') for d in ('Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday')}
        every = {k: True for k in day}
        named = {'rows': [
            {'ID': {'Value': 2002}, 'SpawnType': 'EWorldMapFieldNamedSpawnType::PeriodSpawn', 'PeriodSpawnId': {'Value': 1006}},
            {'ID': {'Value': 2003}, 'SpawnType': 'EWorldMapFieldNamedSpawnType::PeriodSpawn', 'PeriodSpawnId': {'Value': 1013}},
            {'ID': {'Value': 101001}, 'SpawnType': 'EWorldMapFieldNamedSpawnType::Basic', 'PeriodSpawnId': {'Value': 0}},
        ]}
        periods = {'rows': [
            {'ID': {'Value': 1006}, 'SpawnType': 'EPeriodSpawnType::Cycle', 'Time': {'Ticks': 36_000_000_000}, 'Cycle': 180, **every},
            {'ID': {'Value': 1013}, 'SpawnType': 'EPeriodSpawnType::Day', 'Time': {'Ticks': 756_000_000_000}, 'Cycle': 0, **day},
        ]}
        self.assertEqual(field_bosses.schedules(named, periods), {
            '2002': {'time': 60, 'cycle': 180, 'days': [0, 1, 2, 3, 4, 5, 6]},
            '2003': {'time': 1260, 'cycle': 0, 'days': [0, 5]},
        })

    def test_tracked_table_has_the_client_rows(self):
        data = json.loads(field_bosses.DATA.read_text(encoding='utf-8'))
        self.assertEqual(len(data['bosses']), 85)
        self.assertIn([101022, 1010, 2101120], data['bosses'])
        self.assertEqual(len(data['schedules']), 13)


if __name__ == '__main__':
    unittest.main()
