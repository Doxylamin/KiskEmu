"""Local PostgreSQL lifecycle, checked migrations, catalogue import and backups.

Uses vendor psql/pg_ctl tools, not a per-request game-server database driver.
Credentials and all generated data stay under ignored research/local/postgres.

Linux servers (deploy/linux/README.md): the same project-owned cluster, with the
distribution's PostgreSQL binaries (AION_PG_BIN, else the newest
/usr/lib/postgresql/<n>/bin) and AION_PG_HOME for the cluster and credentials.
"""
import argparse
import csv
import glob
import hashlib
import json
import locale
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
HOME = Path(os.environ['AION_PG_HOME']) if os.environ.get('AION_PG_HOME') else ROOT / 'research/local/postgres'
# AION_PG_PORT: a second, throwaway cluster beside the server's own (tools/check_multi_world.py on a host).
PORT = int(os.environ.get('AION_PG_PORT') or 55432)
WINDOWS = os.name == 'nt'
# The realm database (accounts, staff tools, world registry); world databases are <realm>_world_<id>. AION_PG_REALM: a
# disposable realm beside the real one (tools/check_multi_world.py; the host's world supervisor passes it on).
REALM = os.environ.get('AION_PG_REALM') or 'aion'


def default_database():
    """AION_PG_DATABASE (the seeding tools run against a world database through it), else the realm database."""
    return os.environ.get('AION_PG_DATABASE') or REALM


def binaries():
    """Windows: the project's extracted binaries. Elsewhere: the distribution's server binaries."""
    if os.environ.get('AION_PG_BIN'):
        return Path(os.environ['AION_PG_BIN'])
    if WINDOWS:
        return HOME / 'pgsql/bin'
    versions = sorted(glob.glob('/usr/lib/postgresql/*/bin'), key=lambda p: int(Path(p).parent.name) if Path(p).parent.name.isdigit() else 0)
    return Path(versions[-1]) if versions else Path('/usr/bin')


BIN = binaries()


def literal(text):
    return "'" + str(text).replace("'", "''") + "'"


def tool_encoding(binary):
    """psql is asked for UTF-8. initdb and pg_ctl print in the Windows ANSI code page."""
    if not WINDOWS or binary in {'psql', 'pg_dump', 'pg_restore'}:
        return 'utf-8'
    return locale.getpreferredencoding(False)


def run(binary, args=(), *, text=None, role='aion_admin', database=None, check=True, timeout=180):
    database = database or default_database()
    env = os.environ.copy()
    if (HOME / 'credentials.json').exists():
        credentials = json.loads((HOME / 'credentials.json').read_text())
        env.update(PGHOST='127.0.0.1', PGPORT=str(PORT), PGUSER=role,
                   PGPASSWORD=credentials[role], PGDATABASE=database, PGCONNECT_TIMEOUT='5')
    # Query results stay UTF-8. Connection errors on Windows may still be ANSI; those are decoded with replacement.
    env['PGCLIENTENCODING'] = 'UTF8'
    command = [str(BIN / (binary + ('.exe' if WINDOWS else ''))), *map(str, args)]
    flags = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
    if binary == 'pg_ctl':
        # A detached Windows postgres may inherit pipes and keep communicate()
        # waiting after pg_ctl exits. Use a file and DEVNULL for lifecycle calls.
        # The child writes its console code page straight to the handle, so read the bytes back.
        with (HOME / 'lifecycle.log').open('w+b') as log:
            process = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=log,
                                     stderr=subprocess.STDOUT, env=env,
                                     creationflags=flags, timeout=45)
            log.seek(0)
            message = log.read().decode(tool_encoding(binary), errors='replace')
            result = subprocess.CompletedProcess(command, process.returncode, '', message)
    else:
        result = subprocess.run(command, input=text, encoding=tool_encoding(binary), errors='replace',
                                capture_output=True, env=env, creationflags=flags, timeout=timeout)
    if check and result.returncode:
        # SQL errors can contain input; redact generated credentials before printing.
        message = result.stderr
        if (HOME / 'credentials.json').exists():
            for password in json.loads((HOME / 'credentials.json').read_text()).values():
                message = message.replace(password, '[redacted]')
        raise RuntimeError(f'{binary} failed: {message}')
    return result


def sql(text, *, role='aion_admin', database=None, check=True):
    return run('psql', ['-X', '-q', '-A', '-t', '-v', 'ON_ERROR_STOP=1'], text=text,
               role=role, database=database, check=check)


def setup(realm=True):
    """The cluster, its roles and (realm=True) the realm database with its migrations; a world-only machine
    (deploy/linux/install.sh --world) has no realm database, only its world's (create-world)."""
    HOME.mkdir(parents=True, exist_ok=True)
    archive = HOME / 'postgresql-18.6-1-windows-x64-binaries.zip'
    if not (BIN / ('initdb' + ('.exe' if WINDOWS else ''))).exists() and not WINDOWS:
        raise ValueError(f'No PostgreSQL server binaries in {BIN}; install postgresql or set AION_PG_BIN')
    if not BIN.exists():
        with zipfile.ZipFile(archive) as bundle:
            for member in bundle.infolist():
                relative = Path(member.filename)
                if len(relative.parts) < 2 or relative.parts[:2] not in (
                        ('pgsql', 'bin'), ('pgsql', 'lib'), ('pgsql', 'share')):
                    continue
                destination = (HOME / relative).resolve()
                if not destination.is_relative_to(HOME.resolve()):
                    raise ValueError('Unsafe archive path')
                bundle.extract(member, HOME)
        with archive.open('rb') as downloaded:
            archive_digest = hashlib.file_digest(downloaded, 'sha256').hexdigest()
        (HOME / 'binary-source.json').write_text(json.dumps({
            'url': 'https://get.enterprisedb.com/postgresql/' + archive.name,
            'sha256': archive_digest
        }, indent=2), encoding='utf-8')
    credentials_path = HOME / 'credentials.json'
    if not credentials_path.exists():
        if (HOME / 'data').exists():
            raise ValueError('Existing cluster has no credentials file; refusing to replace credentials')
        credentials_path.write_text(json.dumps({name: secrets.token_urlsafe(36)
            for name in ('aion_admin', 'aion_app', 'aion_auth')}), encoding='utf-8')
    credentials = json.loads(credentials_path.read_text())
    if 'aion_auth' not in credentials:
        credentials['aion_auth'] = secrets.token_urlsafe(36)
        credentials_path.write_text(json.dumps(credentials), encoding='utf-8')
    if not (HOME / 'data/PG_VERSION').exists():
        password_file = HOME / 'init-password.txt'
        password_file.write_text(credentials['aion_admin'], encoding='ascii')
        try:
            run('initdb', ['-D', HOME / 'data', '-U', 'aion_admin', '--encoding=UTF8',
                          '--locale=C', '--auth=scram-sha-256', '--pwfile', password_file])
        finally:
            password_file.unlink(missing_ok=True)
        with (HOME / 'data/postgresql.conf').open('a', encoding='utf-8') as config:
            # TCP on loopback only. On Linux, drop the Unix socket so this cluster does not
            # take the distribution's socket directory. Windows builds reject that parameter.
            config.write("\n# Project-owned cluster: loopback only\nlisten_addresses='127.0.0.1'\n"
                         f"port={PORT}\npassword_encryption='scram-sha-256'\n")
            if not WINDOWS:
                config.write("unix_socket_directories=''\n")
    start()
    sql("SELECT 'CREATE ROLE aion_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='aion_owner')\\gexec\n"
        "SELECT 'CREATE ROLE aion_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='aion_app')\\gexec\n"
        "SELECT 'CREATE ROLE aion_auth LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='aion_auth')\\gexec\n"
        'ALTER ROLE aion_app PASSWORD ' + literal(credentials['aion_app']) + ';\n'
        'ALTER ROLE aion_auth PASSWORD ' + literal(credentials['aion_auth']) + ';\n',
        database='postgres')
    if not realm:
        return
    if not database_exists(REALM):
        sql(f'CREATE DATABASE {REALM} OWNER aion_owner;', database='postgres')
    sql(f'REVOKE ALL ON DATABASE {REALM} FROM PUBLIC; GRANT CONNECT ON DATABASE {REALM} TO aion_app,aion_auth;')
    migrate_realm()


def start():
    # Windows sandbox process inspection can report a running server as stopped.
    # Prefer an authenticated check and verify this is our own cluster first.
    probe = sql("SELECT current_setting('data_directory');", database='postgres', check=False)
    if probe.returncode == 0:
        if os.path.normcase(str(Path(probe.stdout.strip()).resolve())) != os.path.normcase(str((HOME / 'data').resolve())):
            raise ValueError('Port is serving a different PostgreSQL data directory')
        return
    status = run('pg_ctl', ['status', '-D', HOME / 'data'], check=False)
    if status.returncode:
        run('pg_ctl', ['start', '-D', HOME / 'data', '-l', HOME / 'server.log', '-w', '-t', '30'])


def migration_script(files):
    out = ["BEGIN; SELECT pg_advisory_xact_lock(731924001); SET LOCAL ROLE aion_owner;",
           'CREATE TABLE IF NOT EXISTS public.schema_migrations(version text PRIMARY KEY, sha256 text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now());']
    for path in files:
        # Hash the committed LF form. A Windows checkout may rewrite the same file as CRLF.
        raw = path.read_bytes().replace(b'\r\n', b'\n')
        body = raw.decode('utf-8')
        version, digest = literal(path.name), literal(hashlib.sha256(raw).hexdigest())
        out.extend([f"DO $check$ BEGIN IF EXISTS(SELECT FROM public.schema_migrations WHERE version={version} AND sha256<>{digest}) THEN RAISE EXCEPTION 'Applied migration changed'; END IF; END $check$;",
                    f'SELECT NOT EXISTS(SELECT FROM public.schema_migrations WHERE version={version}) AS apply \\gset',
                    '\\if :apply', body,
                    f'INSERT INTO public.schema_migrations(version,sha256) VALUES({version},{digest});', '\\endif'])
    return '\n'.join(out + ['COMMIT;'])


def migration_role(path):
    """realm (NNN_name.realm.sql), world (NNN_name.world.sql) or both (NNN_name.sql)."""
    suffixes = Path(path).name.split('.')[1:-1]
    return suffixes[-1] if suffixes and suffixes[-1] in ('realm', 'world') else 'both'


def migration_files(role, before=None, start=None):
    """The migrations of one database kind, in order; before/start: version prefixes ('093') bounding the range."""
    files = sorted((ROOT / 'database/migrations').glob('*.sql'))
    return [f for f in files if migration_role(f) in ('both', role)
            and (before is None or f.name < before) and (start is None or f.name >= start)]


def migrate(database=None, role='realm', before=None, start=None):
    sql(migration_script(migration_files(role, before, start)), database=database or REALM)


def database_exists(name):
    return sql(f'SELECT 1 FROM pg_database WHERE datname={literal(name)};', database='postgres').stdout.strip() == '1'


def world_database(world_id):
    world_id = int(world_id)
    if not 1 <= world_id <= 65535:
        raise ValueError('World id must be 1-65535')
    return f'{REALM}_world_{world_id}'


def local_world_databases():
    """World id -> database name of every world database in this cluster."""
    prefix = f'{REALM}_world_'
    names = sql(f"SELECT datname FROM pg_database WHERE datname LIKE {literal(prefix + '%')} ORDER BY datname;",
                database='postgres').stdout.split()
    return {int(n[len(prefix):]): n for n in names if n[len(prefix):].isdigit()}


def realm_is_split():
    """Whether the realm database no longer holds character data (migration 093 ran)."""
    return sql("SELECT EXISTS(SELECT FROM public.schema_migrations WHERE version LIKE '093\\_%');").stdout.strip() == 't'


def realm_worlds(managed_only=False):
    where = " WHERE kind='managed'" if managed_only else ''
    return [int(v) for v in sql(f'SELECT world_id FROM game.worlds{where} ORDER BY world_id;').stdout.split()]


def create_database(name):
    sql(f'CREATE DATABASE {name} OWNER aion_owner;', database='postgres')
    sql(f'REVOKE ALL ON DATABASE {name} FROM PUBLIC; GRANT CONNECT ON DATABASE {name} TO aion_app;', database=name)


# The split (migrations 092/093): one realm database (accounts, staff tools, world registry, character index) and one
# database per world (its characters, guilds, market, world audit; a mirror of the accounts that play there).
SPLIT_VERSION = '093'
# Prunes a clone of the pre-split realm database down to one world: other worlds' characters and everything that
# hangs off them (single-column foreign keys into game.*, followed until nothing changes), guilds left without
# members, and the account-level audit events (those stay in the realm). Runs as the cluster owner without triggers.
PRUNE_WORLD = """
BEGIN;
SET LOCAL session_replication_role = replica;
DELETE FROM game.characters WHERE world_id <> {world};
DO $prune$
DECLARE r record; n bigint; changed bigint;
BEGIN
    FOR r IN SELECT c.table_schema, c.table_name FROM information_schema.columns c
             JOIN information_schema.tables t USING (table_schema, table_name)
             WHERE c.column_name = 'world_id' AND t.table_type = 'BASE TABLE' AND c.table_schema = 'game'
               AND c.table_name NOT IN ('characters', 'worlds')
    LOOP
        EXECUTE format('DELETE FROM %I.%I WHERE world_id IS DISTINCT FROM {world}', r.table_schema, r.table_name);
    END LOOP;
    LOOP
        changed := 0;
        FOR r IN SELECT c.conrelid::regclass AS child, a.attname AS col, c.confrelid::regclass AS parent, p.attname AS pcol
                 FROM pg_constraint c
                 JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
                 JOIN pg_attribute p ON p.attrelid = c.confrelid AND p.attnum = c.confkey[1]
                 JOIN pg_class pc ON pc.oid = c.confrelid JOIN pg_namespace pn ON pn.oid = pc.relnamespace
                 WHERE c.contype = 'f' AND array_length(c.conkey, 1) = 1 AND pn.nspname = 'game'
        LOOP
            EXECUTE format('DELETE FROM %s x WHERE x.%I IS NOT NULL AND NOT EXISTS (SELECT FROM %s y WHERE y.%I = x.%I)',
                           r.child, r.col, r.parent, r.pcol, r.col);
            GET DIAGNOSTICS n = ROW_COUNT;
            changed := changed + n;
        END LOOP;
        DELETE FROM game.guilds g WHERE NOT EXISTS (SELECT FROM game.guild_members m WHERE m.guild_id = g.guild_id);
        GET DIAGNOSTICS n = ROW_COUNT;
        changed := changed + n;
        EXIT WHEN changed = 0;
    END LOOP;
END $prune$;
DELETE FROM audit.events WHERE character_id IS NULL;
COMMIT;
"""


def clone_world(world_id):
    """A world database cut from the pre-split realm database: a full copy, pruned to this world, then migrated."""
    name = world_database(world_id)
    create_database(name)
    with tempfile.TemporaryDirectory(dir=HOME) as scratch:
        dump = Path(scratch) / 'realm.dump'
        run('pg_dump', ['-Fc', '-f', dump], timeout=1800)
        run('pg_restore', ['--exit-on-error', '--dbname', name, dump], timeout=1800)
    sql(PRUNE_WORLD.format(world=int(world_id)), database=name)
    migrate(name, 'world')
    return name


def split_realm():
    """Before migration 093 drops the character data from the realm: one world database per registered world."""
    split = []
    for world_id in realm_worlds():
        if not database_exists(world_database(world_id)):
            split.append(clone_world(world_id))
        sql(f'INSERT INTO public.database_split(world_id,database) VALUES({world_id},{literal(world_database(world_id))}) '
            'ON CONFLICT (world_id) DO NOTHING;')
    return split


def migrate_realm():
    """Realm migrations; on the first run past 092, the split into world databases happens before 093."""
    if realm_is_split():
        migrate(REALM, 'realm')
        return []
    migrate(REALM, 'realm', before=SPLIT_VERSION)
    split = split_realm()
    migrate(REALM, 'realm', start=SPLIT_VERSION)
    return split


def seed_world(name, catalog=None):
    """Catalogue, starter profiles, class skills and equipment rules of a world database (each step is idempotent)."""
    if catalog:
        import_catalog(catalog, database=name)
    if sql('SELECT count(*) FROM catalog.builds;', database=name).stdout.strip() == '0':
        return False
    env = {**os.environ, 'AION_PG_DATABASE': name}
    for script in (['configure_starters.py'], ['class_skills.py', 'install'], ['equipment_rules.py']):
        subprocess.run([sys.executable, str(ROOT / 'tools' / script[0]), *script[1:]], env=env, check=True,
                       creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    return True


def create_world(world_id, catalog=None):
    """The world's database: created, migrated and seeded when missing, else migrated. Returns its name."""
    name = world_database(world_id)
    if database_exists(name):
        migrate(name, 'world')
        if catalog and sql('SELECT count(*) FROM catalog.builds;', database=name).stdout.strip() == '0':
            seed_world(name, catalog)
        return name
    create_database(name)
    migrate(name, 'world')
    seed_world(name, catalog)
    return name


def ensure_worlds(catalog=None):
    """Every managed world of the realm gets its database; every world database here is migrated."""
    names = [create_world(world_id, catalog) for world_id in realm_worlds(managed_only=True)]
    for world_id, name in local_world_databases().items():
        if name not in names:
            names.append(create_world(world_id, catalog))
    return names


def import_catalog(directory, database=None):
    directory = Path(directory)
    manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8'))
    canonical = json.dumps(manifest, sort_keys=True, separators=(',', ':'))
    build_id = hashlib.sha256(canonical.encode()).hexdigest()
    with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', newline='', suffix='.sql',
                                     dir=HOME, delete=False) as stream:
        path = Path(stream.name)
        stream.write('BEGIN; SELECT pg_advisory_xact_lock(731924002); SET LOCAL ROLE aion_owner;\n')
        stream.write('INSERT INTO catalog.builds(build_id,manifest) VALUES(' + literal(build_id) + ',' + literal(canonical) + ') ON CONFLICT DO NOTHING;\n')
        stream.write('CREATE TEMP TABLE incoming(document jsonb);\nCOPY incoming FROM STDIN WITH (FORMAT csv);\n')
        writer = csv.writer(stream, lineterminator='\n')
        counts = {'items': 0, 'skills': 0}
        with (directory / 'entries.jsonl').open(encoding='utf-8') as source:
            for line in source:
                document = json.loads(line)
                counts[document['kind']] += 1
                writer.writerow([json.dumps(document, ensure_ascii=False)])
        if counts != manifest['counts']:
            raise ValueError('Export counts differ from manifest')
        stream.write('\\.\n')
        for kind in ('items', 'skills'):
            # Immutable snapshots: the same build/ID may be reimported only unchanged.
            stream.write(f"DO $verify$ BEGIN IF EXISTS(SELECT FROM incoming i JOIN catalog.{kind} c ON c.build_id={literal(build_id)} AND c.template_id=(i.document->>'id')::integer WHERE i.document->>'kind'='{kind}' AND c.definition<>i.document) THEN RAISE EXCEPTION 'Catalogue snapshot changed'; END IF; END $verify$;\n")
            stream.write(f"INSERT INTO catalog.{kind}(build_id,template_id,name,category,definition) SELECT {literal(build_id)},(document->>'id')::integer,document->>'name',document->>'category',document FROM incoming WHERE document->>'kind'='{kind}' ON CONFLICT DO NOTHING;\n")
        stream.write('COMMIT;\n')
    try:
        run('psql', ['-X', '-q', '-v', 'ON_ERROR_STOP=1', '-f', path], database=database, timeout=1800)
    finally:
        path.unlink(missing_ok=True)
    return {'build_id': build_id, 'counts': counts}


def backup(output):
    """The realm database to OUTPUT and every world database in this cluster beside it (<stem>.world-<id>.dump);
    a world-only machine has no realm database, so only its world's."""
    output = Path(output)
    targets = ([(output, REALM)] if database_exists(REALM) else []) + [(output.with_name(f'{output.stem}.world-{world_id}{output.suffix}'), name)
                                   for world_id, name in sorted(local_world_databases().items())]
    if any(path.exists() for path, _ in targets):
        raise ValueError('Backup destination already exists')
    output.parent.mkdir(parents=True, exist_ok=True)
    for path, name in targets:
        run('pg_dump', ['-Fc', '-f', path], database=name, timeout=1800)
    return [path for path, _ in targets]


def verify_backup(path):
    """Restore into a uniquely named disposable DB; never restore over aion."""
    name = 'aion_restore_' + secrets.token_hex(8)
    sql(f'CREATE DATABASE {name};', database='postgres')
    try:
        run('pg_restore', ['--no-owner', '--no-privileges', '--exit-on-error',
                           '--dbname', name, Path(path).resolve()])
        return sql("SELECT 'items='||count(*) FROM catalog.items;"
                   "SELECT 'skills='||count(*) FROM catalog.skills;"
                   "SELECT 'migrations='||count(*) FROM public.schema_migrations;", database=name).stdout.strip()
    finally:
        # Name consists only of this fixed prefix and freshly generated hex.
        sql(f'DROP DATABASE {name};', database='postgres')


ROLES = ('gamemaster', 'admin')


def role_statement(username, role, revoke=False):
    """Operator-only staff role change for a local account; returns the SQL to run."""
    if role not in ROLES or not username or len(username) > 32 or not all(c.isalnum() or c == '_' for c in username):
        raise ValueError('Use a local username and a role of gamemaster or admin')
    account = f"SELECT account_id FROM auth.accounts WHERE provider='local' AND subject={literal(username)}"
    if revoke:
        return f"DELETE FROM auth.account_roles WHERE role={literal(role)} AND account_id=({account}) RETURNING account_id;"
    return (f"INSERT INTO auth.account_roles SELECT account_id,{literal(role)} FROM auth.accounts "
            f"WHERE provider='local' AND subject={literal(username)} ON CONFLICT DO NOTHING RETURNING account_id;")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    for command in ('start', 'stop', 'status', 'migrate'):
        sub.add_parser(command)
    setup_parser = sub.add_parser('setup', help='the cluster, its roles and the realm database')
    setup_parser.add_argument('--world-only', action='store_true', help='no realm database (a remote world server; then create-world)')
    world = sub.add_parser('create-world', help="create, migrate and seed a world's database (idempotent)")
    world.add_argument('world_id', type=int)
    world.add_argument('--catalog', type=Path, help='game-catalog-v2 export to import into a new world database')
    worlds = sub.add_parser('ensure-worlds', help='a database for every managed world; migrate every world database')
    worlds.add_argument('--catalog', type=Path)
    load = sub.add_parser('import-catalog')
    load.add_argument('--directory', type=Path, default=ROOT / 'research/local/game-catalog-v2')
    backup = sub.add_parser('backup')
    backup.add_argument('output', type=Path)
    verify = sub.add_parser('verify-backup')
    verify.add_argument('path', type=Path)
    staff = sub.add_parser('role', help='grant or revoke a gamemaster/admin role (operator only)')
    staff.add_argument('username')
    staff.add_argument('role', choices=ROLES)
    staff.add_argument('--revoke', action='store_true')
    args = parser.parse_args()
    if args.command == 'setup':
        setup(realm=not args.world_only)
        print(f'PostgreSQL configured at 127.0.0.1:{PORT}' + ('' if args.world_only else ', database aion; migrations applied.'))
    elif args.command == 'start':
        start()
    elif args.command == 'stop':
        run('pg_ctl', ['stop', '-D', HOME / 'data', '-m', 'fast', '-w'])
    elif args.command == 'status':
        print(sql("SELECT version(); SELECT 'items='||count(*) FROM catalog.items; SELECT 'skills='||count(*) FROM catalog.skills;").stdout)
    elif args.command == 'migrate':
        for name in migrate_realm():
            print('Split: world database', name, 'cut from the realm database')
    elif args.command == 'create-world':
        print(create_world(args.world_id, args.catalog))
    elif args.command == 'ensure-worlds':
        for name in ensure_worlds(args.catalog):
            print('World database ready:', name)
    elif args.command == 'import-catalog':
        print(json.dumps(import_catalog(args.directory), indent=2))
    elif args.command == 'backup':
        if args.output.exists():
            raise ValueError('Backup destination already exists')
        for path in backup(args.output):
            print('Backup saved:', path)
    elif args.command == 'verify-backup':
        print(verify_backup(args.path))
    elif args.command == 'role':
        changed = sql(role_statement(args.username, args.role, args.revoke)).stdout.strip()
        exists = sql(f"SELECT count(*) FROM auth.accounts WHERE provider='local' AND subject={literal(args.username)};").stdout.strip()
        if exists == '0':
            raise SystemExit(f'No local account named {args.username}.')
        action = 'Revoked' if args.revoke else 'Granted'
        print(f'{action} {args.role} for {args.username}.' if changed else f'No change: {args.username} already {"lacks" if args.revoke else "has"} {args.role}.')


if __name__ == '__main__':
    main()
