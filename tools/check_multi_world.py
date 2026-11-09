"""End-to-end multi-world check in a disposable database: auth host, two world servers, chat, per-world staff tools.

Starts the auth host (login port, account site, chat port; no world of its own) on free loopback ports. It starts the
registered world 1204 as a world server, and a second world added through the staff API. The check then drives the
native login and game ports like the client: both worlds are listed, picking a row routes the login to that world's
game port, a character created there is stored on that world only and enters it, the auth host's chat server accepts
that character's chat login for its world (another world's rooms refused, a "//report" command answered by its world
server), maintenance of one world turns players away from that world only, and stopping a world ends its process and
drops it from the list.

Databases as on a server (server/DATABASES.md): a disposable realm database aion_multiworld_<x>, split into one world
database per world (<realm>_world_<id>) by the same migration path an update takes; the second world's database is made
by the auth host's world supervisor (postgres_dev.py create-world) when it is first started. The world servers reach the
realm over the realm link only.

Needs a project cluster (tools/postgres_dev.py setup) and a built host (dotnet build server/Aion.Host). Beside a
server's own cluster, use a throwaway one: AION_PG_HOME=<dir> AION_PG_PORT=<port> python3 tools/postgres_dev.py setup.
"""
import argparse
import json
import os
import random
import secrets
import socket
import struct
import subprocess
import sys
import time
import uuid
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools.aion_protocol import encode_varint, split_frames  # noqa: E402
from tools.load_test import _der, _oaep_decode, _strings, keypair  # noqa: E402
import tools.postgres_dev as postgres_dev  # noqa: E402
from tools.postgres_dev import HOME, ROOT, create_world, import_catalog, migrate_realm, sql  # noqa: E402
from tools.probe_login_reply import RC4  # noqa: E402
from tools.run_account_server import connection_string  # noqa: E402

LOGIN_GREETING, GAME_GREETING = 0x3900, 0x3610


def free_port():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]


class Native:
    """One native connection (login or game port): client bodies RC4-encrypted, server bodies plaintext."""

    def __init__(self, port, game, keys):
        self.sock = socket.create_connection(('127.0.0.1', port), timeout=15)
        self.buffer = b''
        n, e, d = keys
        der = _der(n, e)
        greeting = struct.pack('<H', GAME_GREETING if game else LOGIN_GREETING) + encode_varint(len(der)) + der + bytes(11)
        self.sock.sendall(encode_varint(len(greeting) + 4) + greeting)
        reply = self._raw()
        offset = 8 if game else 6   # 0x3611: u16 result, u16 parameter, u16 extra; 0x3901: u16 result, u16 parameter
        size = reply[offset]
        if size & 0x80:
            size, offset = (size & 0x7f) | (reply[offset + 1] << 7), offset + 1
        cipher = reply[offset + 1:offset + 1 + size]
        self.rc4 = RC4(_oaep_decode(pow(int.from_bytes(cipher, 'big'), d, n).to_bytes(128, 'big'), 128))

    def _raw(self, seconds=15):
        """The next frame's body as sent (not decrypted); None when the server closed the connection."""
        deadline = time.time() + seconds
        while True:
            frames, _ = split_frames(self.buffer)
            if frames:
                body = frames[0].body
                self.buffer = self.buffer[len(encode_varint(len(body) + 4)) + len(body):]
                return body
            if time.time() >= deadline:
                raise TimeoutError('no frame')
            self.sock.settimeout(max(0.1, deadline - time.time()))
            try:
                chunk = self.sock.recv(65536)
            except socket.timeout:
                continue
            if not chunk:
                return None
            self.buffer += chunk

    def send(self, body):
        self.sock.sendall(encode_varint(len(body) + 4) + self.rc4.crypt(body))

    def recv(self, opcode, seconds=15):
        """The next body with this opcode (others are skipped); None when the server closes the connection."""
        deadline = time.time() + seconds
        while True:
            raw = self._raw(max(0.1, deadline - time.time()))
            if raw is None:
                return None
            body = raw   # the server answers in plaintext; only client bodies are encrypted
            if len(body) >= 2 and body[0] | body[1] << 8 == opcode:
                return body

    def close(self):
        self.sock.close()


def account_session(body):
    """0x3906: (wire account, grant) from the "account:grant" string."""
    first = body[5]
    at = 6 + first + 3
    combined = body[at + 1:at + 1 + body[at]].decode()
    wire, grant = combined.split(':')
    return wire, grant


def parse_directory(body):
    """0x3909: (row id, race, name) per row."""
    count, offset, rows = body[4], 5, []
    for index in range(count):
        row, race = struct.unpack_from('<HB', body, offset)
        offset += 7
        size = body[offset]
        name = body[offset + 1:offset + 1 + size].decode()
        offset += 1 + size + (1 if index % 4 == 0 else 0) + 5
        rows.append((row, race, name))
    return rows


def parse_characters(body):
    """0x390b: (row id, name, level) per character."""
    count, offset, rows = body[4], 5, []
    for index in range(count):
        row = struct.unpack_from('<H', body, offset)[0]
        size = body[offset + 2]
        name = body[offset + 3:offset + 3 + size].decode()
        level = struct.unpack_from('<I', body, offset + 3 + size)[0]
        offset += 3 + size + 4 + 4 + 4 + 4 + 1 + 8 + 8 + (1 if index % 4 == 0 else 0)
        rows.append((row, name, level))
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__.split('\n')[0])
    parser.add_argument('--host-dll', default=str(ROOT / 'server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll'))
    parser.add_argument('--keep', action='store_true', help='keep the database and the host log')
    parser.add_argument('--catalog', help='game-catalog-v2 export to import (character creation needs the starter items)')
    args = parser.parse_args()
    credentials = json.loads((HOME / 'credentials.json').read_text())
    name = 'aion_multiworld_' + uuid.uuid4().hex[:12]
    http_port, login_port, chat_port, alpha_port, alpha_control, beta_port, beta_control = (free_port() for _ in range(7))
    env = os.environ.copy()
    for key in [k for k in env if k.startswith('Aion__') or k.startswith('ConnectionStrings__')]:
        del env[key]
    env.update({
        'ConnectionStrings__Aion': connection_string(name, 'aion_app', credentials['aion_app']),
        'ConnectionStrings__Auth': connection_string(name, 'aion_auth', credentials['aion_auth']),
        'Aion__LocalPort': str(http_port), 'Aion__BridgeSecret': secrets.token_hex(32), 'Aion__ChatPort': str(chat_port),
        'Aion__Network__BindAddress': '127.0.0.1', 'Aion__Network__PublicAddress': '127.0.0.1',
        # A game port setting on the auth host is ignored: it runs no world.
        'Aion__Native__LoginPort': str(login_port), 'Aion__Native__GamePort': str(alpha_port),
        'Aion__Native__Logins': 'accounts', 'Aion__Native__PreloadMaps__0': '0', 'Aion__ServerLog__Enabled': 'false',
        'ASPNETCORE_ENVIRONMENT': 'Production',
        # The world supervisor makes a new world's database with postgres_dev.py create-world, named after this realm.
        'AION_PG_REALM': name, 'Aion__Backups__Tool': str(ROOT / 'tools/postgres_dev.py'), 'Aion__Backups__PgHome': str(HOME),
    })
    if args.catalog:
        env['Aion__Worlds__Catalog'] = str(Path(args.catalog).resolve())
    # This process's postgres_dev calls work on the disposable realm too.
    postgres_dev.REALM = os.environ['AION_PG_REALM'] = name

    def world_db(world):
        return f'{name}_world_{world}'
    checks = 0

    def check(condition, label):
        nonlocal checks
        if not condition:
            raise AssertionError(label)
        checks += 1
        print('ok', label)

    def request(path, method='GET', body=None, token=None, port=None):
        headers = {'Content-Type': 'application/json'} if body is not None else {}
        if token:
            headers['Authorization'] = ('Bearer ' + token) if port is None else token
        data = json.dumps(body).encode() if body is not None else None
        try:
            response = urlopen(Request(f'http://127.0.0.1:{port or http_port}' + path, data=data, headers=headers, method=method), timeout=15)
        except HTTPError as error:
            response = error
        with response:
            raw = response.read()
            return response.status, (json.loads(raw) if raw and 'json' in response.headers.get('Content-Type', '') else raw)

    def wait(condition, seconds, label):
        deadline = time.time() + seconds
        while time.time() < deadline:
            value = condition()
            if value:
                return value
            time.sleep(0.5)
        raise AssertionError('timed out: ' + label)

    def count(query, database=None):
        return int(sql(query, database=database or name).stdout.split()[0])

    keys = keypair(random.randrange(1 << 30))
    log_path = HOME / 'multi-world-check-host.log'
    host = None
    sql(f'CREATE DATABASE {name} OWNER aion_owner;', database='postgres')
    try:
        sql(f'REVOKE ALL ON DATABASE {name} FROM PUBLIC; GRANT CONNECT ON DATABASE {name} TO aion_app,aion_auth;', database=name)
        # The update path: migrations up to 092, the split (world 1204's database cut from the realm), then 093.
        check(migrate_realm() == [world_db(1204)], 'the realm migration splits world 1204 into its own database')
        check(sql("SELECT relkind FROM pg_class WHERE oid='game.characters'::regclass;", database=name).stdout.strip() == 'v',
              "the realm's game.characters is the character index's view")
        # Migration 091 made the former login-host world (1204) a managed world server on 13328; this check uses free ports.
        row = sql("SELECT kind||':'||enabled||':'||game_port FROM game.worlds;", database=name).stdout.split()
        check(row == ['managed:true:13328'], 'the former primary world is a managed world server (migration 091)')
        sql(f'UPDATE game.worlds SET game_port={alpha_port}, internal_port={alpha_control} WHERE world_id=1204;', database=name)
        if args.catalog:
            # Character creation needs the catalogue and the starter profiles (as install.sh sets them up): the realm
            # keeps the catalogue for the dashboard, the world database is seeded.
            import_catalog(args.catalog, database=name)
        create_world(1204, args.catalog)
        with log_path.open('w', encoding='utf-8') as log:
            host = subprocess.Popen(['dotnet', args.host_dll], cwd=ROOT / 'server/Aion.Host', env=env, stdout=log, stderr=subprocess.STDOUT)

            def alive():
                if host.poll() is not None:
                    raise RuntimeError(f'host exited; see {log_path}')
                try:
                    return request('/health/live')[0] == 200
                except (URLError, OSError):
                    return False
            wait(alive, 60, 'host start')

            # An admin account (role granted in the database, as tools/postgres_dev.py role does).
            password = secrets.token_urlsafe(18)
            check(request('/api/accounts/register', 'POST', {'username': 'mw_admin', 'password': password})[0] == 201, 'account registered')
            sql("INSERT INTO auth.account_roles SELECT account_id,'admin' FROM auth.accounts WHERE subject='mw_admin';", database=name)
            token = request('/api/accounts/login', 'POST', {'username': 'mw_admin', 'password': password})[1]['token']

            def runtime(world):
                worlds = request('/api/admin/worlds', token=token)[1]['worlds']
                return next(w for w in worlds if w['worldId'] == world)

            def state_is(world, state):
                entry = runtime(world)
                return entry if entry['runtime']['state'] == state else None
            alpha = wait(lambda: state_is(1204, 'running'), 90, 'world 1204 running')
            check(alpha['listed'] and alpha['runtime']['processId'] not in (None, host.pid), 'the auth host starts world 1204 as its own world server process')
            check(request('/api/admin/server', token=token)[0] == 404, 'the auth host has no world of its own (no server health without ?world)')
            check(request('/api/admin/server?world=1204', token=token)[0] == 200, 'server health of world 1204 comes from its world server')

            status, beta = request('/api/admin/worlds', 'POST', {'name': 'Beta', 'kind': 'managed', 'gamePort': beta_port, 'internalPort': beta_control}, token)
            check(status == 200 and beta['worldId'] == 1205 and (beta['lightServerId'], beta['darkServerId']) == (1362, 2362) and not beta['enabled'],
                  'world added with the next free id and server list rows')
            check(request('/api/admin/worlds', 'POST', {'name': 'beta', 'kind': 'managed'}, token)[1] == {'error': 'name_in_use'}, 'duplicate name refused')
            check(request('/api/admin/server?world=1205', token=token)[1] == {'error': 'world_offline'}, 'a stopped world server has no world tools')
            check(request('/api/admin/worlds/1205/start', 'POST', token=token)[0] == 200, 'start requested')
            running = wait(lambda: state_is(1205, 'running'), 90, 'world 1205 running')
            check(running['listed'] and running['runtime']['processId'], 'second world runs as its own process and is listed')

            def login():
                ticket = request('/api/game-tickets', 'POST', {'worldId': 1204}, token)[1]['token']
                connection = Native(login_port, False, keys)
                connection.send(_strings(0x3905, '', ticket, '', '', '', ''))
                account = connection.recv(0x3906)
                return connection, ticket, account

            def pick(row):
                """A login that picks the row: (redirect (address, port), or None when refused; ticket; wire; grant)."""
                connection, ticket, account = login()
                connection.send(b'\x0e\x39' + struct.pack('<H', row))
                try:
                    redirect = connection.recv(0x390f, 10)
                except (TimeoutError, ConnectionError, OSError):
                    redirect = None
                connection.close()
                wire, grant = account_session(account)
                if redirect is None:
                    return None, ticket, wire, grant
                size = redirect[6]
                return (redirect[7:7 + size].decode(), struct.unpack_from('<H', redirect, 7 + size)[0]), ticket, wire, grant

            # Login port: both world servers listed; pick Beta's Light row.
            connection, ticket, account = login()
            connection.send(b'\x08\x39')
            rows = parse_directory(connection.recv(0x3909))
            check(rows == [(1361, 1, 'PotatoeWorld'), (2361, 2, 'PotatoeWorld'), (1362, 1, 'Beta'), (2362, 2, 'Beta')], 'server list shows both worlds, one row per race')
            connection.send(b'\x0a\x39')
            check(parse_characters(connection.recv(0x390b)) == [], 'new account has no characters')
            connection.send(b'\x0c\x39' + struct.pack('<H', 1362))
            connection.recv(0x390d)
            connection.send(b'\x0e\x39' + struct.pack('<H', 1362))
            redirect = connection.recv(0x390f)
            size = redirect[6]
            address, port = redirect[7:7 + size].decode(), struct.unpack_from('<H', redirect, 7 + size)[0]
            check((address, port) == ('127.0.0.1', beta_port), "picking Beta redirects to Beta's game port")
            wire, grant = account_session(account)
            connection.close()

            # Beta's game port: the routed grant is accepted; create a character there and enter the world.
            game = Native(beta_port, True, keys)
            game.send(_strings(0x3613, f'{wire}:{grant}', '', ticket, '') + bytes(9))
            check(game.recv(0xffff, 20) is not None, "Beta's game port accepts the routed login")
            game.send(_strings(0x3619, 'Betaling') + b'\x01')
            game.recv(0x361a)
            game.send(_strings(0x361b, 'Betaling') + b'\x01' + struct.pack('<I', 1) + b'\x02\x0c\x64' + b'\x00\x00\x00')
            created = game.recv(0x361c)
            stored = sql("SELECT world_id||':'||name FROM game.characters;", database=world_db(1205)).stdout.split()
            check(stored == ['1205:Betaling'], f"character stored in world 1205's database (create reply {created.hex()[:24]})")
            check(count('SELECT count(*) FROM game.characters;', world_db(1204)) == 0, "world 1204's database has none")
            character_wire = count("SELECT wire_id FROM game.characters WHERE name='Betaling';", world_db(1205))
            wait(lambda: count("SELECT count(*) FROM game.character_index WHERE world_id=1205 AND name='Betaling';") == 1, 30, 'index')
            check(True, "the realm's character index lists it (synced over the realm link)")
            check(count("SELECT count(*) FROM auth.accounts WHERE subject='mw_admin';", world_db(1205)) == 1,
                  "world 1205's database mirrors the account")
            game.send(b'\x1f\x36' + encode_varint(character_wire) + bytes(16))
            check(game.recv(0x3621, 30) is not None, 'the character is selected on Beta')
            game.send(b'\x22\x36' + struct.pack('<i', 1))

            # Chat: one chat server (the auth host's) for every world server; a login is bound to the world it names.
            def chat_login(server_key):
                return request('/gameLogin/loginWithToken', 'POST', {'authnToken': ticket, 'characterId': str(character_wire),
                                                                      'serverKey': server_key, 'locale': 'en_US'}, port=chat_port)[1]
            chat = wait(lambda: (lambda reply: reply if reply.get('mtalkToken') else None)(chat_login('1205')), 30, 'chat login on world 1205')
            mtalk = chat['mtalkToken']
            check(chat['subscriptionInfo']['worldTopic'] == '/topic/world.1205', 'chat login for the in-world character on world 1205')
            check(not chat_login('1204').get('mtalkToken'), 'the character is not in world 1204: its chat login there is refused')

            def chat_call(route, body):
                return request('/gameClient/' + route, 'POST', body, token=mtalk, port=chat_port)[1]

            def say(content):
                return chat_call('sendMessage', {'gameRoomKeyInfo': room, 'gameMessageInfo': {
                    'userName': 'x', 'type': 'PUBLISH', 'subType': 'NORMAL', 'content': content, 'optional': ''}})
            room = {'type': 'REGION', 'serverKey': '1205', 'roomKey': 'RG'}
            check(chat_call('joinRoom', {'gameRoomKeyInfo': room}).get('error') == 0, "joins its world's region room")
            check(chat_call('joinRoom', {'gameRoomKeyInfo': dict(room, serverKey='1204')}).get('error') != 0, "another world's room is refused")
            check(say('hello from 1205').get('error') == 0, 'a room message is relayed')
            say('//report multi-world check')
            wait(lambda: count("SELECT count(*) FROM game.tickets WHERE content LIKE '%multi-world check%';") == 1, 20, 'report ticket')
            check(True, "a chat command is answered by the sender's world server (//report opened a ticket)")
            wait(lambda: count("SELECT count(*) FROM game.chat_log WHERE content='hello from 1205';") == 1, 20, 'chat log')
            check(True, 'the room message is in the chat log')
            live = request('/api/admin/live?world=1205', token=token)[1]['players']
            check([p['name'] for p in live] == ['Betaling'], 'the live list of world 1205 shows the character')
            check(request('/api/admin/live?world=1204', token=token)[1]['players'] == [], 'the live list of world 1204 is its own')
            game.close()

            # Maintenance per world: on 1205, only that world turns players away (staff still enter).
            status, state = request('/api/admin/maintenance?world=1205', 'POST', {'enabled': True, 'message': 'Beta upkeep'}, token)
            check(status == 200 and state['state']['enabled'], 'maintenance on for world 1205')
            check(not request('/api/admin/maintenance?world=1204', token=token)[1]['enabled'], 'world 1204 is not in maintenance')
            sql("DELETE FROM auth.account_roles WHERE role='admin';", database=name)
            check(pick(1362)[0] is None, 'a player picking world 1205 in maintenance is refused')
            check(pick(1361)[0] == ('127.0.0.1', alpha_port), 'world 1204 still lets players in')
            sql("INSERT INTO auth.account_roles SELECT account_id,'admin' FROM auth.accounts WHERE subject='mw_admin';", database=name)
            check(request('/api/admin/maintenance?world=1205', 'POST', {'enabled': False}, token)[0] == 200, 'maintenance off for world 1205')
            check(pick(1362)[0] == ('127.0.0.1', beta_port), 'world 1205 lets players in again')

            # World 1204's game port does not accept a grant routed to Beta (each world server serves its own world).
            _, ticket2, wire2, grant2 = pick(1362)
            other = Native(alpha_port, True, keys)
            other.send(_strings(0x3613, f'{wire2}:{grant2}', '', ticket2, '') + bytes(9))
            refused = False
            try:
                refused = other.recv(0xffff, 5) is None
            except (TimeoutError, ConnectionError, OSError):
                refused = True
            other.close()
            check(refused, "world 1204's game port refuses a login routed to Beta")

            # Stop from the staff API: process ends, Beta and its characters leave the login list.
            check(request('/api/admin/worlds/1205/stop', 'POST', token=token)[0] == 200, 'stop requested')
            wait(lambda: state_is(1205, 'stopped'), 60, 'world 1205 stopped')
            pid = running['runtime']['processId']
            if os.name != 'nt':
                check(not Path(f'/proc/{pid}').exists(), 'the world process exited')
            connection, _, _ = login()
            connection.send(b'\x08\x39')
            check([r[0] for r in parse_directory(connection.recv(0x3909))] == [1361, 2361], 'stopped world leaves the server list')
            connection.send(b'\x0a\x39')
            check(parse_characters(connection.recv(0x390b)) == [], 'its characters are hidden while it is stopped')
            connection.close()
            check(request('/api/admin/worlds/1205', 'DELETE', token=token)[1] == {'error': 'world_has_characters'}, 'a world with characters is not removed')
            check(request('/api/admin/worlds/1204/stop', 'POST', token=token)[0] == 200, 'world 1204 stops like any world server')
            wait(lambda: state_is(1204, 'stopped'), 60, 'world 1204 stopped')
            check(alive(), 'the auth host keeps running without any world server')
        print(f'{checks} multi-world checks passed.')
    finally:
        if host is not None and host.poll() is None:
            host.terminate()
            try:
                host.wait(timeout=60)
            except subprocess.TimeoutExpired:
                host.kill()
        if not args.keep:
            for world in postgres_dev.local_world_databases().values():
                sql(f'DROP DATABASE {world} WITH (FORCE);', database='postgres')
            sql(f'DROP DATABASE {name} WITH (FORCE);', database='postgres')


if __name__ == '__main__':
    main()
