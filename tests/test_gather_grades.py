import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
from gather_grades import better_grades

DATA = json.loads((ROOT / 'tools/data/gather-grades.json').read_text(encoding='utf-8'))


class GatherGradeTests(unittest.TestCase):
    def test_common_material_pairs_with_its_rare_name(self):
        items = [(1, 'Material_Gather_Od_A_c_01_f'), (2, 'Material_Gather_Od_A_r_01_f'), (3, 'Material_Gather_Food_L_c_01e_f'),
                 (4, 'Equip_Something_c_01')]
        self.assertEqual({1: 2}, better_grades(items))

    def test_odyle_is_found_as_fine_odyle(self):
        # OBSERVED 2026-11-15: a discovered Odyle gather yields Fine Odyle.
        self.assertEqual(610610014, DATA['better']['610610013'])


if __name__ == '__main__':
    unittest.main()
