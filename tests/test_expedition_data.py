import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tools'))

import expedition_data  # noqa: E402


class ExpeditionDataTests(unittest.TestCase):
    def test_tickets_and_dungeon_uses_from_client_tables(self):
        # Port of the community fork addiboii's test (87afb91, forge #72), plus the cube cost (660fe39).
        tables = {
            'ContentsTicket': [
                {'ID': {'Value': 80000001}, 'Name': 'Contents_Ticket_Awaken', 'TicketType': 'ETicketType::Count',
                 'RechargeMaxCount': 3, 'WeeklyRechargeDay': 'EDayOfWeekType::Wednesday',
                 'WeeklyRechargeTime': {'Ticks': 576000000000}, 'WeeklyRechargeCount': 3,
                 'OwnerType': 'EContentsTicketOwnerType::Character'},
                {'ID': {'Value': 3}, 'Name': 'Contents_Ticket_Abyss', 'TicketType': 'ETicketType::Time',
                 'RechargeMaxTime': 25200, 'DailyRechargeCount': 0, 'OwnerType': 'EContentsTicketOwnerType::Server'},
                {'ID': {'Value': 60000001}, 'Name': 'reward_ticket_PartyDungeon_Common', 'TicketType': 'ETicketType::Count',
                 'RechargeMaxCount': 560, 'TimeRechargeCycle': 10800, 'TimeRechargeCount': 15}],
            'MapEntrance': [
                {'Name': 'Enter_Awaken_01', 'ContentsTicketList': [
                    {'Name': 'Contents_Ticket_Awaken', 'Count': 1, 'DeductType': 'ETicketDeductType::Enter', 'bCheckEnterCondition': True},
                    {'Name': 'Unknown_Ticket', 'Count': 1, 'DeductType': 'ETicketDeductType::Enter'}]},
                {'Name': 'Enter_Quiet', 'ContentsTicketList': []}],
            'Dungeon': [{'ID': {'Value': 910011}, 'Name': 'Awaken_01'}, {'ID': {'Value': 5}, 'Name': 'Quiet'},
                        {'ID': {'Value': 600002}, 'Name': 'KraoCave_Easy', 'RewardOdEnergyCube': 'Cube_KraoCave_Easy'}],
        }
        cubes = [{'Name': 'Cube_KraoCave_Easy', 'TicketName': 'reward_ticket_PartyDungeon_Common', 'ConsumeTicketCount': 20}]
        tickets, dungeons, cube_tickets = expedition_data.tickets_and_uses(tables, cubes)
        self.assertEqual(tickets['80000001'], {'name': 'Contents_Ticket_Awaken', 'type': 'Count', 'max': 3,
                                               'weekly': [3, 57600, 3]})
        self.assertEqual(tickets['3'], {'name': 'Contents_Ticket_Abyss', 'type': 'Time', 'max': 25200, 'owner': 'Server'})
        self.assertEqual(tickets['60000001']['cycle'], [10800, 15])
        self.assertEqual(dungeons, {'910011': [[80000001, 1, 'Enter']]})
        self.assertEqual(cube_tickets, {'600002': [60000001, 20]})

    def test_new_character_ticket_list_parses_exactly(self):
        body = bytes.fromhex(expedition_data.NEW_CHARACTER.read_text(encoding='ascii').strip())
        rows = expedition_data.ticket_list(body)
        self.assertEqual(len(rows), 73)
        self.assertEqual(rows[1], {'count': 1})
        self.assertEqual(rows[3], {'time_ms': 25_200_000})
        self.assertEqual(rows[6], {})
        self.assertEqual(rows[60000001], {'count': 180})

    def test_build_keeps_the_committed_tickets_without_client_tables(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp) / 'expedition.json'
            out.write_text((ROOT / 'tools/data/expedition.json').read_text(encoding='utf-8'), encoding='utf-8')
            expedition_data.build(Path(tmp) / 'no-tables', out)
            data = json.loads(out.read_text(encoding='utf-8'))
        self.assertEqual(len(data['tickets']), 73)
        self.assertEqual(len(data['dungeons']), 127)
        self.assertEqual(data['tickets']['80000001']['start'], 3)
        self.assertEqual(data['tickets']['13']['start'], 10_800_000)
        self.assertEqual(data['dungeons']['910011'], [[80000001, 1, 'Enter']])
        self.assertEqual(data['cube_tickets']['600001'], [60000001, 40])
        self.assertEqual(data['expeditions']['600002']['levels'][0]['dungeon_id'], 600002)


if __name__ == '__main__':
    unittest.main()
