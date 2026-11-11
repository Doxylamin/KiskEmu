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

    def test_tracked_table_has_the_client_rows(self):
        data = json.loads(field_bosses.DATA.read_text(encoding='utf-8'))
        self.assertEqual(len(data['bosses']), 85)
        self.assertIn([101022, 1010, 2101120], data['bosses'])


if __name__ == '__main__':
    unittest.main()
