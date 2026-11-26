import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))
import projectile_data  # noqa: E402


class ProjectileDataTests(unittest.TestCase):
    def test_convert_keeps_rows_with_an_id(self):
        rows = [{'ID': {'Value': 10002001}, 'ProjectileType': 'EProjectileType::Collide', 'Speed': 4000, 'ArrivalFixTimeMin': 0,
                 'ArrivalFixTimeMax': 0, 'CollideMoveValue01': 2500},
                {'ID': {'Value': 0}, 'ProjectileType': 'EProjectileType::Target', 'Speed': 1}]
        self.assertEqual(projectile_data.convert(rows), {10002001: ['Collide', 4000, 0, 0, 2500]})

    def test_build_reads_a_worldprobe_export(self):
        with tempfile.TemporaryDirectory() as tmp:
            table = Path(tmp) / 'table.json'
            table.write_text(json.dumps({'Properties': {'Data': [
                {'ID': {'Value': 5}, 'ProjectileType': 'EProjectileType::Splash_FixTime_Target', 'Speed': 500,
                 'ArrivalFixTimeMin': 3000, 'ArrivalFixTimeMax': 6000}]}}), encoding='utf-8')
            doc = projectile_data.build(table)
        self.assertEqual(doc['version'], '3533')
        self.assertEqual(doc['projectiles'], {'5': ['Splash_FixTime_Target', 500, 3000, 6000, 0]})

    def test_tracked_file(self):
        doc = json.loads(projectile_data.OUT.read_text(encoding='utf-8'))
        self.assertEqual(doc['version'], '3533')
        self.assertEqual(len(doc['projectiles']), 2468)   # every SkillProjectile.dat row of 3533
        self.assertEqual(doc['projectiles']['170800001'], ['Splash_FixTime_Target', 1200, 550, 550, 0])
        self.assertEqual(doc['projectiles']['10002001'], ['Collide', 4000, 0, 0, 2500])


if __name__ == '__main__':
    unittest.main()
