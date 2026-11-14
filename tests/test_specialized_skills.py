"""tools/data/specialized-skills.json (tools/specialized_skills.py, ported from addiboii's fork)."""
import itertools
import json
import unittest
from pathlib import Path

from tools import specialized_skills

ROOT = Path(__file__).resolve().parents[1]
DATA = json.loads((ROOT / 'tools/data/specialized-skills.json').read_text(encoding='utf-8'))
STIGMAS = json.loads((ROOT / 'tools/data/stigma-skills.json').read_text(encoding='utf-8'))
CLASS_SKILLS = json.loads((ROOT / 'tools/data/class-skills.json').read_text(encoding='utf-8'))['skills']


def cast(skill):
    return DATA['skills'].get(str(skill)) or CLASS_SKILLS.get(str(skill))


class SpecializedSkillTests(unittest.TestCase):
    def test_child_ids_are_the_parts_digits_ascending(self):
        # The recorded casts (community recordings 2026-11-04 to 06, fork notes): parts 30+40 of 11020000 -> 11020340, 50+30
        # of 11010000 -> 11010350, 10+40 of 11100000 -> 11100140; one part -> 11360030.
        self.assertEqual(specialized_skills.child_id(11020000, [11020030, 11020040]), 11020340)
        self.assertEqual(specialized_skills.child_id(11010000, [11010050, 11010030]), 11010350)
        self.assertEqual(specialized_skills.child_id(11100000, [11100010, 11100040]), 11100140)
        self.assertEqual(specialized_skills.child_id(11360000, [11360030]), 11360030)
        self.assertEqual(specialized_skills.child_id(11020000, [11020050, 11020010, 11020030]), 11021350)

    def test_every_child_of_the_table_is_a_parts_combination_with_cast_data(self):
        for parent, kids in DATA['children'].items():
            parts = [p for p, _ in DATA['parts'][parent]]
            made = {specialized_skills.child_id(int(parent), c) for n in (1, 2, 3) for c in itertools.combinations(parts, n)}
            self.assertEqual(set(kids), made, parent)
            self.assertTrue(all(cast(k) for k in kids), parent)
        self.assertEqual(DATA['missing_skills'], [])

    def test_cast_rows_are_only_those_class_skills_lacks(self):
        self.assertFalse(set(DATA['skills']) & set(CLASS_SKILLS))

    def test_slots_and_part_levels(self):
        self.assertEqual(DATA['slot_levels'], [8, 12, 20])
        self.assertEqual(DATA['stigma_slots'], 5)
        self.assertEqual(DATA['parts']['11020000'], [[11020010, 8], [11020020, 8], [11020030, 8], [11020040, 12], [11020050, 16]])

    def test_chain_steps_children_follow_the_childs_chain(self):
        # Recorded in 0x5100 #643 with 11020000 parts 30+40 (fork notes): 11030340 and 11040340 follow 11020340.
        self.assertIn(11030340, DATA['chain_children']['11030000'])
        self.assertIn(11040340, DATA['chain_children']['11040000'])
        self.assertEqual(cast(11030340)['chain_prev'], 11020340)
        self.assertEqual(cast(11020340)['chain_next'], [11030340])

    def test_stigmas_are_not_mastery_parents(self):
        # A stigma's parts (InitEquipSlotType Slot_1..4) stay in stigma-skills.json, not here.
        self.assertFalse(set(DATA['parts']) & set(STIGMAS['parts']))


if __name__ == '__main__':
    unittest.main()
