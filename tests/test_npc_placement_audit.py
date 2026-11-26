import struct
import unittest

from tools import npc_placement_audit as audit_mod
from tools import official_npc_placements as official_mod


def quest(start=None, finish=None, objectives=(), map_id=1010, **extra):
    return dict({'race': 'Light', 'level': 12, 'map': map_id, 'start_npc': start, 'finish_npc': finish, 'finish_map': map_id,
                 'steps': [{'objectives': list(objectives)}]}, **extra)


def spawn(name, npc_id, x, y, z, yaw, key, route=-1):
    return [name, npc_id, x, y, z, yaw, key, 0, route]


def official(npcs=None, objects=None):
    return {'captures': ['c1'], 'npcs': npcs or {}, 'objects': objects or {}}


class CompareTests(unittest.TestCase):
    server = {'x': 0.0, 'y': 0.0, 'z': 100.0, 'yaw': 90.0}

    def sample(self, **kw):
        return dict({'x': 0.0, 'y': 0.0, 'z': 80.0, 'heading': 90.0, 'samples': 1, 'spread': 0.0, 'walking': 0}, **kw)

    def test_grounded_official_sample_is_ok(self):
        self.assertEqual(audit_mod.compare(self.server, self.sample())['flags'], [])

    def test_offsets_beyond_the_thresholds_are_flagged(self):
        c = audit_mod.compare(self.server, self.sample(x=150.0, z=-50.0, heading=120.0))
        self.assertEqual(c['flags'], ['pos_off', 'z_off', 'heading_off'])
        self.assertEqual((c['dxy'], c['dz'], c['dh']), (150.0, -150.0, 30.0))

    def test_heading_wraps_around_north(self):
        c = audit_mod.compare(dict(self.server, yaw=359.0), self.sample(heading=4.0))
        self.assertEqual(c['dh'], 5.0)
        self.assertEqual(c['flags'], [])

    def test_walking_npcs_are_not_judged(self):
        self.assertEqual(audit_mod.compare(self.server, self.sample(x=900.0, walking=2))['flags'], ['moving'])
        self.assertEqual(audit_mod.compare(self.server, self.sample(spread=400.0))['flags'], ['moving'])

    def test_no_official_sample_is_unknown(self):
        self.assertEqual(audit_mod.compare(self.server, None)['flags'], ['no_sample'])

    def test_level_bands(self):
        self.assertEqual([audit_mod.band(l) for l in (1, 9, 10, 39, 45, 46)],
                         ['1-9', '1-9', '10-19', '30-39', '40-45', '46+ (above cap)'])


class AuditTests(unittest.TestCase):
    def run_audit(self, quests, maps, official_data, objects=None, bases=None):
        world = {'maps': maps, 'templates': {'1': {'type': 'Citizen'}, '2': {'type': 'Citizen'}, '3': {'type': 'Monster'}}}
        return audit_mod.audit(quests, world, objects or {'maps': {}}, official_data, bases or {})

    def test_quest_npc_offset_against_official(self):
        result = self.run_audit({'Q1': quest(start=1)}, {'1010': [spawn('N_Giver', 1, 0, 0, 100, 90, 7)]},
                                official({'1010': {'7': [1, 300.0, 0.0, 80.0, 90.0, 2, 0.0, 0]}}))
        row = result['targets'][('npc', 1, 1010)]['rows'][0]
        self.assertEqual(row['matched_by'], 'key')
        self.assertEqual(row['compare']['flags'], ['pos_off'])

    def test_missing_spawn_wrong_map_and_layer(self):
        quests = {'Q1': quest(start=1, finish=2, objectives=[{'type': 'KillNpc', 'npcs': [9], 'map': 1010}]),
                  'Q2': quest(start=3)}
        maps = {'1110': [spawn('N_Elsewhere', 1, 0, 0, 0, 0, 5)], '101001': [spawn('N_Layer', 2, 0, 0, 0, 0, 6)],
                '1010': [spawn('M_Kill', 3, 0, 0, 0, 0, 8)]}
        result = self.run_audit(quests, maps, official(), bases={'101001': 1010})
        flags = {k: t['spawn_flags'] for k, t in result['targets'].items()}
        self.assertEqual(flags[('npc', 9, 1010)], ['missing_spawn'])
        self.assertEqual(flags[('npc', 1, 1010)], ['wrong_map'])
        self.assertEqual(flags[('npc', 2, 1010)], ['other_layer'])
        self.assertEqual(flags[('npc', 3, 1010)], [])
        self.assertFalse(result['targets'][('npc', 9, 1010)]['placement'])

    def test_supplement_spawn_is_matched_by_id_and_marked_ours(self):
        result = self.run_audit({'Q1': quest(objectives=[{'type': 'AskNpc', 'npc': 1, 'map': 1010}])},
                                {'1010': [spawn('N_Event_QuestSup_1', 1, 0, 0, 100, 0, 0)]},
                                official({'1010': {'44': [1, 50.0, 0.0, 90.0, 0.0, 1, 0.0, 0], '45': [1, 9000.0, 0.0, 0.0, 0.0, 1, 0.0, 0]}}))
        row = result['targets'][('npc', 1, 1010)]['rows'][0]
        self.assertEqual((row['matched_by'], row['official']['unique_key']), ('id', 44))
        self.assertEqual(row['compare']['flags'], ['improvised'])

    def test_official_spawn_at_an_unknown_key_is_reported(self):
        result = self.run_audit({'Q1': quest(start=1)}, {'1010': [spawn('N_Giver', 1, 0, 0, 0, 0, 7)]},
                                official({'1010': {'99': [1, 0.0, 0.0, 0.0, 0.0, 1, 0.0, 0]}}))
        self.assertEqual(result['targets'][('npc', 1, 1010)]['spawn_flags'], ['official_unmatched'])

    def test_object_keys_stored_signed_match_the_unsigned_wire_key(self):
        objects = {'maps': {'1010': {'objects': [{'env': 5, 'name': 'E_Box', 'unique_key': -2, 'position': [0, 0, 0], 'yaw': 10.0}]}}}
        result = self.run_audit({'Q1': quest(objectives=[{'type': 'UseEnvObj', 'envs': [5], 'map': 1010}])}, {},
                                official(objects={'1010': {str(2 ** 32 - 2): [5, 0.0, 0.0, 0.0, 40.0, 1, 0.0]}}), objects)
        row = result['targets'][('object', 5, 1010)]['rows'][0]
        self.assertEqual(row['matched_by'], 'key')
        self.assertEqual(row['compare']['flags'], ['heading_off'])

    def test_unavailable_quests_are_skipped(self):
        result = self.run_audit({'Q1': quest(start=1, unavailable=True)}, {}, official())
        self.assertEqual(result['targets'], {})

    def test_render_lists_the_worst_offset(self):
        result = self.run_audit({'Q1': quest(start=1)}, {'1010': [spawn('N_Giver', 1, 0, 0, 100, 90, 7)]},
                                official({'1010': {'7': [1, 300.0, 0.0, 80.0, 90.0, 2, 0.0, 0]}}))
        text = audit_mod.render(result)
        self.assertIn('| npc 1 | 1010 | N_Giver |', text)
        self.assertIn('top 40 of 1', text)


class OfficialParserTests(unittest.TestCase):
    @staticmethod
    def npc_body(npc_id=2930494, pos=(1.5, -2.0, 300.0), direction=270.0, unique_key=0xB1351E59, mask=0x2004):
        # Same layout as GamePackets.NpcAppear: key varint, mask, u8, npc u32, u8, u8 2, pos, direction, ..., unique key, u8 0.
        return (b'\x82\xc5\x02' + struct.pack('<HBIBB', mask, 0, npc_id, 0, 2) + struct.pack('<4f', *pos, direction)
                + bytes(40) + struct.pack('<IB', unique_key, 0))

    @staticmethod
    def env_body(env_id=1500012, pos=(10.0, 20.0, 30.0), yaw=45.0, unique_key=0xFBDCCF9B):
        return (b'\xa9\xb9\x01' + struct.pack('<BBI3fif', 4, 0, env_id, *pos, 0, yaw) + b'\x01\x01\x01' + bytes(16)
                + struct.pack('<IBBB', unique_key, 8, 0, 0))

    def test_npc_appear(self):
        self.assertEqual(official_mod.parse_npc_appear(self.npc_body()), (2930494, 1.5, -2.0, 300.0, 270.0, 0xB1351E59, False))
        self.assertTrue(official_mod.parse_npc_appear(self.npc_body(mask=0x2005))[-1])

    def test_env_spawn(self):
        self.assertEqual(official_mod.parse_env_spawn(self.env_body()), (1500012, 10.0, 20.0, 30.0, 45.0, 0xFBDCCF9B))
        trigger = bytearray(self.env_body())
        trigger[3] = 1
        self.assertIsNone(official_mod.parse_env_spawn(bytes(trigger)))

    def test_dump_lines_follow_the_map_load_and_merge(self):
        def line(op, body):
            return f'10:00:00.000 d0 {op[:2]} {op[2:]} len {len(body) + 2:5}  {body.hex()}'
        map_load = lambda m: struct.pack('<II', 1, m) + bytes(40)
        lines = [line('4136', self.npc_body()), line('2136', map_load(1010)), line('4136', self.npc_body()),
                 line('4136', self.npc_body(pos=(301.5, -2.0, 300.0))), line('2136', map_load(600031)), line('3436', self.env_body()),
                 'garbage']
        samples = list(official_mod.parse_dump(lines))
        self.assertEqual([(k, m) for k, m, _r in samples], [('npc', 1010), ('npc', 1010), ('object', 600031)])
        data = official_mod.add_samples({}, samples)
        self.assertEqual(data['npcs']['1010'][str(0xB1351E59)], [2930494, 1.5, -2.0, 300.0, 270.0, 2, 300.0, 0])
        self.assertEqual(data['objects']['600031'][str(0xFBDCCF9B)][:6], [1500012, 10.0, 20.0, 30.0, 45.0, 1])


class CommittedReportTests(unittest.TestCase):
    def test_committed_report_is_current(self):
        self.assertEqual(audit_mod.main(['--check']), 0)


if __name__ == '__main__':
    unittest.main()
