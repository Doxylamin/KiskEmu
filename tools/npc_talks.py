"""NPC talk play times (the client's NpcTalk table) for the map events' talk directives -> tools/data/npc-talks.json.

An NpcTalk row is one line of a talk: Name (the talk the map events' PlayNpcTalk / PlayHurryNpcTalk / PlayChatbubble
directives name), Order (the line), Type (Caption, ChatBubble, FullScreen, ...) and PlayTime (ms the line stays up).
A talk lasts the sum of its lines' PlayTime. The server holds a map-event directive sequence until a PlayNpcTalk is
over, so the scripted NPCs do not walk off before their speech is done.

The input is the NpcTalk table dumped by the client table tools (kept outside the repository), for example:

    dotnet run --project editor/AionClientEditor.Cli -- dump-schema --table NpcTalk --rows 30000 --paks <Paks> --key <key> --out <npctalk.json>
    python tools/npc_talks.py build <npctalk.json>

Only the talks the map events name are kept. File format: {"source": ..., "talks": {name: total PlayTime ms}} sorted by name.
"""
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from data_json import write_data_json

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'tools/data/npc-talks.json'
MAP_EVENTS = ROOT / 'tools/data/map-events.json'
TALK_DIRECTIVES = {'PlayNpcTalk': 0, 'PlayHurryNpcTalk': 0, 'PlayChatbubble': 1}


def _rows(dump):
    """The rows of a dump-schema file ({"rows": [{"values": {...}}]}, or a list holding one) or a plain row list."""
    if isinstance(dump, list) and dump and isinstance(dump[0], dict) and 'rows' in dump[0]:
        dump = dump[0]
    rows = dump['rows'] if isinstance(dump, dict) else dump
    return [r.get('values', r) for r in rows]


def play_times(rows):
    """Talk name -> the sum of its lines' PlayTime (ms)."""
    times = {}
    for row in rows:
        name = row.get('Name')
        if not name or name == 'None':
            continue
        times[name] = times.get(name, 0) + int(row.get('PlayTime') or 0)
    return times


def used_talks(map_events=MAP_EVENTS):
    """The talk names the map events' talk directives name (comma lists split)."""
    names = set()

    def walk(node):
        if isinstance(node, dict):
            index = TALK_DIRECTIVES.get(node.get('t'))
            if index is not None and isinstance(node.get('v'), list) and len(node['v']) > index:
                names.update(n.strip() for n in (node['v'][index] or '').split(',') if n.strip())
            for value in node.values():
                walk(value)
        elif isinstance(node, list):
            for value in node:
                walk(value)

    walk(json.loads(Path(map_events).read_text(encoding='utf-8'))['events'])
    return names


def build(dump_path, map_events=MAP_EVENTS, out=DATA):
    times = play_times(_rows(json.loads(Path(dump_path).read_text(encoding='utf-8'))))
    used = used_talks(map_events)
    talks = {name: times[name] for name in sorted(used) if name in times}
    write_data_json(Path(out), {'source': 'client NpcTalk table (Name, sum of PlayTime ms), talks the map events name', 'talks': talks})
    print(f'{len(talks)} talks of {len(used)} named by the map events -> {out}')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest='command', required=True)
    b = sub.add_parser('build')
    b.add_argument('dump')
    args = parser.parse_args()
    if args.command == 'build':
        build(args.dump)


if __name__ == '__main__':
    main()
