"""Content tickets, their dungeon uses and the expedition catalog -> tools/data/expedition.json.

Tickets and dungeon uses (port of the community fork addiboii 87afb91 / 660fe39, forge #72): read from the client's tables
when a WorldProbe export folder is present (OBSERVED field names and values, Steam build 26262663):

- ContentsTicket.dat: per ticket the type (Count / Time), the cap (RechargeMaxCount, or RechargeMaxTime seconds for a
  Time ticket), the daily and weekly recharges (time of day as .NET ticks, weekday, count or seconds added), the
  TimeRechargeCycle (seconds, count) and the owner.
- MapEntrance.dat: Enter_<dungeon name> rows; ContentsTicketList names the tickets an entry checks
  (bCheckEnterCondition), how many it takes (Count) and when (DeductType: None / Enter / Clear / Unclaimed).
- Dungeon.dat: dungeon id -> name (the MapEntrance link) and its RewardOdEnergyCube row.
- RewardOdEnergyCube.dat: TicketName / ConsumeTicketCount, what opening the expedition reward cube costs.

Without that folder (it is not kept in research/local here) the tickets, dungeon uses and cube costs already in
expedition.json are kept: they came from the fork's export of those tables (build 26262663).

`start`: what a new character holds, from the OBSERVED ContentsTickets_NT 0x610b body of a character with nothing used
(tools/data/contents-tickets-new-character.hex, from the community fork zaraza87): a count, or for a Time ticket the
remaining time in ms.

`expeditions` / `affixes`: the party dungeon difficulty tiers by group (map-events.json, PartyDungeonAffix.dat).

CANDIDATE: the recharge times' time zone (the tools keep the raw seconds of the day; the server reads them as KST, as the
shops' 57600 s reset).

    python tools/expedition_data.py [build] [--tables research/local/contents-ticket-26262663-quests]
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/expedition.json'
MAP_EVENTS = ROOT / 'tools/data/map-events.json'
NEW_CHARACTER = ROOT / 'tools/data/contents-tickets-new-character.hex'
TABLES = ROOT / 'research/local/contents-ticket-26262663-quests'
CUBES = ROOT / 'research/local/dungeon-reward-tables-20261018/table-000.json'   # RewardOdEnergyCube.dat
PARTY_DUNGEON_AFFIX = ROOT / 'research/local/party-dungeon-affix/table-000.json'
WEEKDAYS = {'Sunday': 0, 'Monday': 1, 'Tuesday': 2, 'Wednesday': 3, 'Thursday': 4, 'Friday': 5, 'Saturday': 6}
TICKS_PER_SECOND = 10_000_000
SOURCE = ('ContentsTicket.dat, MapEntrance.dat ContentsTicketList, Dungeon.dat, RewardOdEnergyCube.dat (Steam build '
          '26262663, export of the community fork addiboii); start: OBSERVED new-character 0x610b '
          '(contents-tickets-new-character.hex, community fork zaraza87); expeditions: map-events.json')


def _enum(value):
    return str(value or '').split('::')[-1]


def _id(value):
    return int(value['Value']) if isinstance(value, dict) else int(value or 0)


def _seconds(value):
    return int((value or {}).get('Ticks', 0) // TICKS_PER_SECOND) if isinstance(value, dict) else 0


def _rows(table):
    props = table['Properties']
    if isinstance(props, dict):
        props = props.get('Data', next(iter(props.values())))
    return props


def _optional_rows(path):
    try:
        return _rows(json.loads(Path(path).read_text(encoding='utf-8')))
    except (OSError, ValueError, KeyError, StopIteration):
        return []


def load_tables(folder=TABLES):
    """{'ContentsTicket': rows, 'MapEntrance': rows, 'Dungeon': rows} from a WorldProbe output folder (summary.json); {} without one."""
    folder = Path(folder)
    try:
        summary = json.loads((folder / 'summary.json').read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return {}
    out = {}
    for package in summary['packages']:
        name = Path(package['path']).stem
        if name in ('ContentsTicket', 'MapEntrance', 'Dungeon') and package.get('file'):
            out[name] = _rows(json.loads((folder / package['file']).read_text(encoding='utf-8')))
    return out


def ticket(row):
    """One ContentsTicket row -> compact entry (zero/None fields left out)."""
    kind = _enum(row.get('TicketType'))
    out = {'name': row['Name'], 'type': kind,
           'max': int(row.get('RechargeMaxTime') if kind == 'Time' else row.get('RechargeMaxCount') or 0)}
    daily_count = int(row.get('DailyRechargeCount') or 0)
    daily_add = int(row.get('DailyRechargeAddTime') or 0)
    if daily_count or daily_add:
        out['daily'] = [_seconds(row.get('DailyRechargeTime')), daily_add if kind == 'Time' else daily_count]
    weekly_count = int(row.get('WeeklyRechargeCount') or 0)
    weekly_add = int(row.get('WeeklyRechargeAddTime') or 0)
    if weekly_count or weekly_add:
        out['weekly'] = [WEEKDAYS.get(_enum(row.get('WeeklyRechargeDay')), 0), _seconds(row.get('WeeklyRechargeTime')),
                         weekly_add if kind == 'Time' else weekly_count]
    if int(row.get('TimeRechargeCycle') or 0):
        out['cycle'] = [int(row['TimeRechargeCycle']), int(row.get('TimeRechargeCount') or row.get('TimeRechargeAddTime') or 0)]
    owner = _enum(row.get('OwnerType'))
    if owner and owner != 'Character':
        out['owner'] = owner
    if row.get('bPcDeadPauseTimer'):
        out['dead_pause'] = True
    return out


def tickets_and_uses(tables, cubes=()):
    """(tickets, dungeon uses, cube costs) from the client tables."""
    tickets = {str(_id(r['ID'])): ticket(r) for r in tables.get('ContentsTicket', [])}
    ids = {t['name']: int(k) for k, t in tickets.items()}
    entrances = {r['Name']: r for r in tables.get('MapEntrance', [])}
    cube_costs = {r['Name']: r for r in cubes if r.get('TicketName') and int(r.get('ConsumeTicketCount') or 0) > 0}
    dungeons, cube_tickets = {}, {}
    for row in tables.get('Dungeon', []):
        entry = entrances.get('Enter_' + row['Name'])
        uses = []
        for t in (entry or {}).get('ContentsTicketList') or []:
            if t.get('Name') in ids and t.get('bCheckEnterCondition', True):
                uses.append([ids[t['Name']], int(t.get('Count') or 0), _enum(t.get('DeductType')) or 'None'])
        if uses:
            dungeons[str(_id(row['ID']))] = uses
        cube = cube_costs.get(row.get('RewardOdEnergyCube'))
        if cube and cube['TicketName'] in ids:
            cube_tickets[str(_id(row['ID']))] = [ids[cube['TicketName']], int(cube['ConsumeTicketCount'])]
    return tickets, dungeons, cube_tickets


def read_varint(data, at):
    value = shift = 0
    while True:
        byte = data[at]
        at += 1
        value |= (byte & 0x7F) << shift
        shift += 7
        if byte < 0x80:
            return value, at


def ticket_list(body):
    """A ContentsTickets_NT body (no opcode): varint n, then {mask u8, id i32, bit0 time i64, bit1 charge time i64,
    bit2 count varint, bit3 charge count varint} per ticket -> {id: {'count'?, 'time_ms'?, ...}}."""
    n, at = read_varint(body, 0)
    out = {}
    for _ in range(n):
        mask = body[at]
        tid = int.from_bytes(body[at + 1:at + 5], 'little')
        at += 5
        row = {}
        if mask & 1:
            row['time_ms'] = int.from_bytes(body[at:at + 8], 'little', signed=True)
            at += 8
        if mask & 2:
            row['charge_time_ms'] = int.from_bytes(body[at:at + 8], 'little', signed=True)
            at += 8
        if mask & 4:
            row['count'], at = read_varint(body, at)
        if mask & 8:
            row['charge_count'], at = read_varint(body, at)
        out[tid] = row
    if at != len(body):
        raise ValueError(f'0x610b body: {len(body) - at} bytes left over')
    return out


def with_start(tickets, start_rows):
    """Each ticket's `start`: the new character's count (Count) or remaining ms (Time); 0 when the body leaves it out."""
    for key, row in tickets.items():
        seen = start_rows.get(int(key), {})
        row['start'] = seen.get('time_ms', 0) if row.get('type') == 'Time' else seen.get('count', 0)
    return tickets


def _party_expedition_catalog():
    """Party dungeon difficulty tiers and affix ids from client dat when present, else map-events.json."""
    affixes = {}
    for row in _optional_rows(PARTY_DUNGEON_AFFIX):
        aid = row.get('ID', {}).get('Value')
        if aid is None:
            continue
        affixes[str(aid)] = {'name': str(row.get('Name') or '')}
    try:
        dungeons = json.loads(MAP_EVENTS.read_text(encoding='utf-8')).get('dungeons', {})
    except OSError:
        dungeons = {}
    by_group = {}
    for did, room in dungeons.items():
        if room.get('type') != 'Party':
            continue
        group = str(room.get('group') or 'None')
        by_group.setdefault(group, []).append((int(did), str(room.get('name') or '')))
    expeditions = {}
    for group, rows in sorted(by_group.items()):
        rows.sort(key=lambda r: r[0])
        levels = []
        for index, (dungeon_id, name) in enumerate(rows):
            levels.append({
                'level': index,
                'dungeon_id': dungeon_id,
                'name': name,
                'affixes': sorted(int(k) for k in affixes) if affixes else [],
            })
        anchor = rows[0][0]
        expeditions[str(anchor)] = {'group': group, 'levels': levels}
    return affixes, expeditions


def build(tables_folder=TABLES, out=DATA):
    """Rebuilds expedition.json: tickets / dungeon uses / cube costs from the client tables when present, else kept."""
    try:
        current = json.loads(Path(out).read_text(encoding='utf-8'))
    except (OSError, ValueError):
        current = {}
    tables = load_tables(tables_folder)
    if tables.get('ContentsTicket'):
        tickets, dungeons, cube_tickets = tickets_and_uses(tables, _optional_rows(CUBES))
        if not cube_tickets:
            cube_tickets = current.get('cube_tickets', {})
    else:
        tickets = {k: {f: v for f, v in t.items() if f != 'start'} for k, t in current.get('tickets', {}).items()}
        dungeons, cube_tickets = current.get('dungeons', {}), current.get('cube_tickets', {})
    start = ticket_list(bytes.fromhex(NEW_CHARACTER.read_text(encoding='ascii').strip()))
    affixes, expeditions = _party_expedition_catalog()
    write_data_json(Path(out), {
        'source': SOURCE,
        'tickets': with_start(tickets, start),
        'dungeons': dungeons,
        'cube_tickets': cube_tickets,
        'affixes': affixes,
        'expeditions': expeditions,
    })
    return tickets, dungeons


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('command', nargs='?', default='build', choices=['build'])
    parser.add_argument('--tables', default=str(TABLES))
    args = parser.parse_args()
    tickets, dungeons = build(args.tables)
    print(len(tickets), 'tickets,', len(dungeons), 'dungeons with tickets ->', DATA.relative_to(ROOT))


if __name__ == '__main__':
    main()
