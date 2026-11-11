import os
from pathlib import Path
import tempfile
import unittest
import uuid
from concurrent.futures import ThreadPoolExecutor

from tools.postgres_dev import (literal, migrate_quna, migration_files, migration_role, migration_script, quna_credit,
                                quna_request, sql, migrate, role_statement, world_database)


class MigrationTests(unittest.TestCase):
    def test_sql_literal(self):
        self.assertEqual(literal("O'Brien"), "'O''Brien'")

    def test_migration_roles(self):
        self.assertEqual(migration_role('092_database_split.realm.sql'), 'realm')
        self.assertEqual(migration_role('092_database_split.world.sql'), 'world')
        self.assertEqual(migration_role('090_worlds.sql'), 'both')
        self.assertEqual(migration_role('001_init.sql'), 'both')

    def test_migration_files_split_by_database(self):
        realm = [f.name for f in migration_files('realm')]
        world = [f.name for f in migration_files('world')]
        self.assertIn('093_realm_without_characters.realm.sql', realm)
        self.assertNotIn('093_realm_without_characters.realm.sql', world)
        self.assertIn('092_database_split.world.sql', world)
        self.assertNotIn('092_database_split.world.sql', realm)
        self.assertEqual(realm, sorted(realm))
        # Shared migrations run on both kinds of database.
        self.assertIn('090_worlds.sql', realm)
        self.assertIn('090_worlds.sql', world)
        # The split happens between 092 and 093: the bounded ranges cover the realm list exactly once.
        before = [f.name for f in migration_files('realm', before='093')]
        after = [f.name for f in migration_files('realm', start='093')]
        self.assertEqual(before + after, realm)
        self.assertTrue(all(name < '093' for name in before))

    def test_world_database_names(self):
        self.assertEqual(world_database(1204), 'aion_world_1204')
        for bad in (0, 65536, -1):
            with self.assertRaises(ValueError):
                world_database(bad)

    def test_migrations_are_checked_and_transactional(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / '001.sql'
            path.write_text('SELECT 1;', encoding='utf-8')
            script = migration_script([path])
        self.assertTrue(script.startswith('BEGIN;'))
        self.assertIn('pg_advisory_xact_lock', script)
        self.assertIn('Applied migration changed', script)
        self.assertIn('\\if :apply', script)
        self.assertTrue(script.endswith('COMMIT;'))


class FakeRealm:
    """game.quna_apply's rules in memory (a request id once; a debit only within the balance), for migrate_quna."""

    def __init__(self, accounts):
        self.accounts, self.balances, self.ledger = set(accounts), {}, {}

    def credit(self, account, request, amount, world_id, details):
        if account not in self.accounts:
            return None
        if request in self.ledger:
            return True, self.ledger[request][2], None, True
        self.balances[account] = self.balances.get(account, 0) + amount
        self.ledger[request] = (account, amount, self.balances[account], world_id, details)
        return True, self.balances[account], None, False


class QunaMigrationTests(unittest.TestCase):
    # Fixed ids: the migration goes through the accounts in order (A before B).
    A, B, LOST = '10000000-0000-4000-8000-000000000001', '20000000-0000-4000-8000-000000000002', '30000000-0000-4000-8000-000000000003'

    def setUp(self):
        # Two world databases: account A has Quna on both, B on one; LOST is not in the realm.
        self.items = {
            'aion_world_1204': {self.A: [('a1', 1_000), ('a2', 500)], self.B: [('b1', 70)], self.LOST: [('x1', 9)]},
            'aion_world_1205': {self.A: [('a3', 2_000)]},
        }
        self.worlds = {1204: 'aion_world_1204', 1205: 'aion_world_1205'}
        self.realm = FakeRealm([self.A, self.B])

    def stacks(self, database):
        return {account: list(rows) for account, rows in self.items[database].items() if rows}

    def delete(self, database, instances):
        for account, rows in self.items[database].items():
            self.items[database][account] = [r for r in rows if r[0] not in instances]

    def run_migration(self, delete=None):
        return migrate_quna(self.worlds, stacks=self.stacks, credit=self.realm.credit, delete=delete or self.delete)

    def test_sums_each_accounts_stacks_over_two_worlds_into_one_wallet(self):
        moved = self.run_migration()
        self.assertEqual(self.realm.balances, {self.A: 3_500, self.B: 70})
        self.assertEqual(sorted((w, a, n) for w, a, n, _ in moved), sorted([(1204, self.A, 1_500), (1204, self.B, 70), (1205, self.A, 2_000)]))
        # One opening entry per world and account, then the stacks are gone; the unknown account keeps its items.
        self.assertEqual(len(self.realm.ledger), 3)
        self.assertTrue(all(request.startswith('migration:') for request in self.realm.ledger))
        self.assertEqual(self.items['aion_world_1204'], {self.A: [], self.B: [], self.LOST: [('x1', 9)]})
        self.assertEqual(self.items['aion_world_1205'], {self.A: []})

    def test_a_rerun_credits_nothing_twice(self):
        self.run_migration()
        self.assertEqual(self.run_migration(), [])
        self.assertEqual(self.realm.balances, {self.A: 3_500, self.B: 70})

    def test_a_rerun_after_a_failed_delete_only_deletes(self):
        def fail(database, instances):
            raise RuntimeError('connection lost')
        with self.assertRaises(RuntimeError):
            self.run_migration(delete=fail)
        self.assertEqual(self.realm.balances, {self.A: 1_500})
        moved = self.run_migration()
        self.assertTrue(moved[0][3], 'the first account was credited before')
        self.assertEqual(self.realm.balances, {self.A: 3_500, self.B: 70})
        self.assertEqual(len(self.realm.ledger), 3)

    def test_stacks_found_later_are_credited_too(self):
        self.run_migration()
        self.items['aion_world_1205'][self.A] = [('a9', 25)]
        self.run_migration()
        self.assertEqual(self.realm.balances[self.A], 3_525)

    def test_request_ids_follow_the_stacks(self):
        same = quna_request(1204, self.A, [('a2', 500), ('a1', 1_000)])
        self.assertEqual(same, quna_request(1204, self.A, [('a1', 1_000), ('a2', 500)]))
        self.assertNotEqual(same, quna_request(1205, self.A, [('a1', 1_000), ('a2', 500)]))
        self.assertNotEqual(same, quna_request(1204, self.A, [('a1', 1_001), ('a2', 500)]))
        self.assertLessEqual(len(same), 200)

    def test_wallet_migration_is_realm_only(self):
        realm = [f.name for f in migration_files('realm')]
        world = [f.name for f in migration_files('world')]
        self.assertIn('095_account_quna.realm.sql', realm)
        self.assertNotIn('095_account_quna.realm.sql', world)


@unittest.skipUnless(os.environ.get('AION_POSTGRES_TESTS') == '1', 'Requires local PostgreSQL (realm migrated past 095)')
class QunaWalletSqlTests(unittest.TestCase):
    def setUp(self):
        self.account = str(uuid.uuid4())
        sql(f"INSERT INTO auth.accounts(account_id,provider,subject) VALUES('{self.account}','integration-test','{self.account}');")

    def apply(self, request, delta, reason):
        return sql(f"SELECT ok, balance, COALESCE(error,''), repeated FROM game.quna_apply('{self.account}','{request}',{delta},'{reason}',1204,NULL,NULL);",
                   role='aion_app').stdout.strip().split('|')

    def test_ledger_is_idempotent_refuses_overdraft_and_is_append_only(self):
        self.assertEqual(quna_credit(self.account, f'm-{self.account}', 1_000, 1204, {'test': True})[:2], (True, 1_000))
        self.assertEqual(quna_credit(self.account, f'm-{self.account}', 1_000, 1204, {'test': True}), (True, 1_000, None, True))
        self.assertEqual(self.apply(f's-{self.account}', -400, 'purchase'), ['t', '600', '', 'f'])
        self.assertEqual(self.apply(f's-{self.account}', -400, 'purchase'), ['t', '600', '', 't'])
        self.assertEqual(self.apply(f'x-{self.account}', -601, 'purchase'), ['f', '600', 'insufficient', 'f'])
        self.assertEqual(self.apply(f's-{self.account}', -1, 'purchase')[2], 'request_conflict')
        refund = sql(f"SELECT ok, balance FROM game.quna_refund('{self.account}','s-{self.account}',NULL);", role='aion_app').stdout.strip()
        self.assertEqual(refund, 't|1000')
        # Only a purchase is refunded, not a staff deduction (105).
        self.assertEqual(self.apply(f'staff-{self.account}', -100, 'staff')[:2], ['t', '900'])
        refused = sql(f"SELECT ok, error FROM game.quna_refund('{self.account}','staff-{self.account}',NULL);", role='aion_app').stdout.strip()
        self.assertEqual(refused, 'f|not_found')
        self.assertEqual(sql(f"SELECT count(*) FROM game.quna_ledger WHERE account_id='{self.account}';").stdout.strip(), '4')
        self.assertNotEqual(sql(f"DELETE FROM game.quna_ledger WHERE account_id='{self.account}';", check=False).returncode, 0)
        self.assertNotEqual(sql(f"UPDATE game.quna_wallets SET balance=-1 WHERE account_id='{self.account}';", check=False).returncode, 0)


@unittest.skipUnless(os.environ.get('AION_POSTGRES_TESTS') == '1', 'Requires local PostgreSQL and imported catalogue')
class PostgreSQLTests(unittest.TestCase):
    def setUp(self):
        self.account, self.character = str(uuid.uuid4()), str(uuid.uuid4())
        self.build = sql('SELECT build_id FROM catalog.builds ORDER BY imported_at LIMIT 1;').stdout.strip()
        self.assertTrue(self.build)
        sql(f"INSERT INTO auth.accounts(account_id,provider,subject) VALUES('{self.account}','integration-test','{self.account}');"
            f"INSERT INTO game.characters(character_id,account_id,world_id,name,creation_id,race,gender) VALUES('{self.character}','{self.account}',-999,'{self.character}',1,1,1);")
        self.addCleanup(self.cleanup_fixture)

    def cleanup_fixture(self):
        sql(f"BEGIN; DELETE FROM game.item_grants WHERE instance_id IN(SELECT instance_id FROM game.item_instances WHERE character_id='{self.character}');"
            f"DELETE FROM audit.events WHERE character_id='{self.character}';"
            f"DELETE FROM game.item_instances WHERE character_id='{self.character}';"
            f"DELETE FROM game.characters WHERE character_id='{self.character}';"
            f"DELETE FROM auth.accounts WHERE account_id='{self.account}'; COMMIT;")

    def grant(self, request, quantity=1):
        return sql(f"SELECT game.grant_item('{self.character}','{self.build}',110160001,{quantity},'{request}');", role='aion_app').stdout.strip()

    def test_concurrent_grant_retry_returns_one_item(self):
        request = str(uuid.uuid4())
        with ThreadPoolExecutor(max_workers=2) as pool:
            results = list(pool.map(lambda _: self.grant(request), range(2)))
        self.assertEqual(results[0], results[1])
        self.assertEqual(sql(f"SELECT count(*) FROM game.item_instances WHERE character_id='{self.character}';").stdout.strip(), '1')
        self.assertEqual(sql(f"SELECT count(*) FROM audit.events WHERE character_id='{self.character}';").stdout.strip(), '1')
        with self.assertRaisesRegex(RuntimeError, 'different arguments'):
            self.grant(request, 2)

    def test_invalid_grant_rolls_back(self):
        request = str(uuid.uuid4())
        result = sql(f"SELECT game.grant_item('{self.character}','{self.build}',-999,1,'{request}');", role='aion_app', check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(sql(f"SELECT count(*) FROM game.item_grants WHERE request_id='{request}';").stdout.strip(), '0')
        with self.assertRaisesRegex(RuntimeError, 'positive'):
            self.grant(request, 0)

    def test_runtime_cannot_assign_roles_change_catalog_or_delete_audit(self):
        statements = [f"INSERT INTO auth.account_roles VALUES('{self.account}','admin');",
                      "UPDATE catalog.items SET name='changed' WHERE false;", 'DELETE FROM audit.events WHERE false;']
        for statement in statements:
            self.assertNotEqual(sql(statement, role='aion_app', check=False).returncode, 0)

    def test_revisions_reject_stale_save_and_equipment_slot_conflict(self):
        result = sql(f"UPDATE game.characters SET level=2 WHERE character_id='{self.character}' AND revision=0 RETURNING revision;", role='aion_app')
        self.assertEqual(result.stdout.strip(), '1')
        stale = sql(f"UPDATE game.characters SET level=3 WHERE character_id='{self.character}' AND revision=0 RETURNING revision;", role='aion_app')
        self.assertEqual(stale.stdout.strip(), '')
        first, second = self.grant(str(uuid.uuid4())), self.grant(str(uuid.uuid4()))
        sql(f"UPDATE game.item_instances SET location='equipment',slot=1 WHERE instance_id='{first}';", role='aion_app')
        conflict = sql(f"UPDATE game.item_instances SET location='equipment',slot=1 WHERE instance_id='{second}';", role='aion_app', check=False)
        self.assertNotEqual(conflict.returncode, 0)

    def test_repeat_migrations_and_checksum_mismatch(self):
        migrate()
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / '001_initial.sql'
            path.write_text('-- changed', encoding='utf-8')
            result = sql(migration_script([path]), check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Applied migration changed', result.stderr)


class CommandLineTests(unittest.TestCase):
    def test_backup_command_calls_the_backup_function(self):
        # update.sh runs "postgres_dev.py backup <file>" before every deploy; a parser variable named "backup" once
        # shadowed the function and the command crashed before anything was backed up.
        import sys
        from unittest import mock
        import tools.postgres_dev as pg
        with tempfile.TemporaryDirectory() as scratch:
            target = Path(scratch) / 'out.dump'
            with mock.patch.object(pg, 'backup', return_value=[target]) as called, \
                 mock.patch.object(sys, 'argv', ['postgres_dev.py', 'backup', str(target)]):
                pg.main()
            called.assert_called_once_with(target)


class TimeoutSettingTests(unittest.TestCase):
    def test_long_timeout_from_the_environment(self):
        from unittest import mock
        import tools.postgres_dev as pg
        for value, expected in (('', pg.LONG_TIMEOUT), ('7200', 7200), (' 60 ', 60), ('0', None)):
            with mock.patch.dict(os.environ, {'AION_PG_TIMEOUT': value}):
                self.assertEqual(pg.long_timeout(), expected)
        with mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual(pg.long_timeout(), pg.LONG_TIMEOUT)
        for bad in ('-5', '1h', '2.5'):
            with mock.patch.dict(os.environ, {'AION_PG_TIMEOUT': bad}), self.assertRaisesRegex(ValueError, 'AION_PG_TIMEOUT'):
                pg.long_timeout()

    def test_long_operations_use_it_and_quick_queries_keep_theirs(self):
        from unittest import mock
        import tools.postgres_dev as pg
        with mock.patch.dict(os.environ, {'AION_PG_TIMEOUT': '9000'}), mock.patch.object(pg, 'run') as run:
            pg.migrate('aion_world_1204', 'world')
            pg.sql('SELECT 1;')
        self.assertEqual(run.call_args_list[0].kwargs['timeout'], 9000)
        self.assertEqual(run.call_args_list[1].kwargs['timeout'], 180)
        with mock.patch.dict(os.environ, {'AION_PG_TIMEOUT': '0'}), mock.patch.object(pg, 'run') as run, \
             mock.patch.object(pg, 'database_exists', return_value=True), \
             mock.patch.object(pg, 'local_world_databases', return_value={}), tempfile.TemporaryDirectory() as scratch:
            pg.backup(Path(scratch) / 'out.dump')
        self.assertIsNone(run.call_args.kwargs['timeout'])

    def test_timeout_flag_sets_the_environment(self):
        import sys
        from unittest import mock
        import tools.postgres_dev as pg
        with mock.patch.dict(os.environ, {}), mock.patch.object(pg, 'verify_backup', return_value='ok') as verify, \
             mock.patch.object(sys, 'argv', ['postgres_dev.py', '--timeout', '5400', 'verify-backup', 'x.dump']), \
             mock.patch('builtins.print'):
            pg.main()
            self.assertEqual(os.environ['AION_PG_TIMEOUT'], '5400')
            self.assertEqual(pg.long_timeout(), 5400)
        verify.assert_called_once()


class BackupRetentionTests(unittest.TestCase):
    def test_keeps_the_newest_per_day_and_per_week(self):
        from datetime import datetime, timedelta
        from tools.postgres_dev import retained
        # Daily at 04:30 for 60 days up to Wednesday 2026-11-11, plus a manual run that day.
        last = datetime(2026, 11, 11, 4, 30)
        times = [last - timedelta(days=d) for d in range(60)] + [datetime(2026, 11, 11, 12, 0)]
        keep = retained(times, daily=7, weekly=4)
        days = sorted({t.date() for t in keep}, reverse=True)
        # 7 days (the manual run replaces that day's 04:30 one), and the Sundays closing the 3 weeks before this one.
        self.assertIn(datetime(2026, 11, 11, 12, 0), keep)
        self.assertNotIn(datetime(2026, 11, 11, 4, 30), keep)
        self.assertEqual(days[:7], [(last - timedelta(days=d)).date() for d in range(7)])
        self.assertEqual({t for t in keep if t.date() < days[6]},
                         {datetime(2026, 11, 1, 4, 30), datetime(2026, 10, 25, 4, 30)})
        self.assertEqual(len(keep), 9)
        self.assertEqual(retained(times, daily=1, weekly=0), {datetime(2026, 11, 11, 12, 0)})
        # The 60 days touch 10 ISO weeks; this week's newest is also a daily one, so 12 weeks keep 3 + 9.
        self.assertEqual(len(retained(times, daily=3, weekly=12)), 3 + 9)
        self.assertEqual(retained([], 7, 4), set())

    def make_backup(self, directory, worlds=(1204, 1300), realm=True):
        """A stand-in for backup(): the realm and world files of a set, in the folder it is asked for."""
        def backup(output):
            output.parent.mkdir(parents=True, exist_ok=True)
            files = ([output] if realm else []) + [output.with_name(f'{output.stem}.world-{w}{output.suffix}') for w in worlds]
            for path in files:
                path.write_bytes(b'PGDMP')
            return files
        return backup

    def test_scheduled_backup_verifies_moves_and_prunes(self):
        from datetime import datetime
        from unittest import mock
        import json
        import tools.postgres_dev as pg
        with tempfile.TemporaryDirectory() as scratch:
            directory = Path(scratch)
            for stamp in ('20261101-043000', '20261105-043000', '20261109-043000', '20261110-043000'):
                for suffix in ('.dump', '.world-1204.dump'):
                    (directory / f'scheduled-{stamp}{suffix}').write_bytes(b'old')
            (directory / 'before-update-20261001-120000.dump').write_bytes(b'kept')
            (directory / '.incoming-20261110-043000').mkdir()  # a crashed run
            verified = []
            with mock.patch.object(pg, 'backup', side_effect=self.make_backup(directory)), \
                 mock.patch.object(pg, 'verify_backup', side_effect=lambda p: verified.append(p.name) or 'items=5\nskills=2\nmigrations=104'), \
                 mock.patch('builtins.print'):
                status = pg.scheduled_backup(directory, daily=2, weekly=1, now=datetime(2026, 11, 11, 4, 30))
            names = sorted(p.name for p in directory.iterdir())
            report = json.loads((directory / 'last-scheduled.json').read_text(encoding='utf-8'))
        self.assertEqual(verified, ['scheduled-20261111-043000.dump', 'scheduled-20261111-043000.world-1204.dump',
                                    'scheduled-20261111-043000.world-1300.dump'])
        self.assertEqual(names, ['before-update-20261001-120000.dump', 'last-scheduled.json',
                                 'scheduled-20261110-043000.dump', 'scheduled-20261110-043000.world-1204.dump',
                                 'scheduled-20261111-043000.dump', 'scheduled-20261111-043000.world-1204.dump',
                                 'scheduled-20261111-043000.world-1300.dump'])
        self.assertTrue(status['ok'] and report['ok'])
        self.assertEqual(report['kept'], ['20261110-043000', '20261111-043000'])
        self.assertEqual(len(report['removed']), 6)
        self.assertEqual(report['files'][0]['verified'], {'items': '5', 'skills': '2', 'migrations': '104'})

    def test_a_failed_verification_keeps_the_older_sets(self):
        from datetime import datetime
        from unittest import mock
        import contextlib
        import io
        import json
        import tools.postgres_dev as pg
        with tempfile.TemporaryDirectory() as scratch:
            directory = Path(scratch)
            old = [directory / f'scheduled-2026100{d}-043000.dump' for d in range(1, 4)]
            for path in old:
                path.write_bytes(b'old')
            with mock.patch.object(pg, 'backup', side_effect=self.make_backup(directory, realm=False)), \
                 mock.patch.object(pg, 'verify_backup', side_effect=RuntimeError('pg_restore failed: broken')), \
                 mock.patch('builtins.print'), self.assertRaisesRegex(RuntimeError, 'broken'):
                pg.scheduled_backup(directory, daily=1, weekly=0, now=datetime(2026, 11, 11, 4, 30))
            self.assertEqual(sorted(p.name for p in directory.iterdir()), ['last-scheduled.json'] + [p.name for p in old])
            report = json.loads((directory / 'last-scheduled.json').read_text(encoding='utf-8'))
            self.assertFalse(report['ok'])
            self.assertIn('broken', report['error'])
            # A dump without migrations does not count as verified either.
            with mock.patch.object(pg, 'backup', side_effect=self.make_backup(directory)), \
                 mock.patch.object(pg, 'verify_backup', return_value='items=0\nskills=0\nmigrations=0'), \
                 mock.patch('builtins.print'), self.assertRaisesRegex(ValueError, 'without migrations'):
                pg.scheduled_backup(directory, daily=1, weekly=0, now=datetime(2026, 11, 11, 4, 31))
            self.assertEqual(len(list(directory.glob('scheduled-*'))), 3)
        with self.assertRaises(ValueError):
            pg.scheduled_backup(Path('unused'), daily=0, weekly=4)

    def test_retention_settings_and_command(self):
        import sys
        from unittest import mock
        import tools.postgres_dev as pg
        with mock.patch.dict(os.environ, {'AION_BACKUP_KEEP_DAILY': '14', 'AION_BACKUP_KEEP_WEEKLY': ''}), \
             mock.patch.object(pg, 'scheduled_backup') as called, \
             mock.patch.object(sys, 'argv', ['postgres_dev.py', 'scheduled-backup', 'backups']):
            pg.main()
        called.assert_called_once_with(Path('backups'), 14, 4)
        with mock.patch.dict(os.environ, {'AION_BACKUP_KEEP_DAILY': 'many'}), self.assertRaisesRegex(ValueError, 'KEEP_DAILY'):
            pg.retention_setting('AION_BACKUP_KEEP_DAILY', 7)

    def test_restore_takes_the_whole_set_and_needs_yes(self):
        import sys
        from unittest import mock
        import tools.postgres_dev as pg
        with tempfile.TemporaryDirectory() as scratch:
            directory = Path(scratch)
            for name in ('set.dump', 'set.world-1204.dump', 'set.world-1300.dump', 'set.world-x.dump', 'other.world-9.dump'):
                (directory / name).write_bytes(b'PGDMP')
            self.assertEqual([(p.name, db) for p, db in pg.backup_set_files(directory / 'set.dump')],
                             [('set.dump', 'aion'), ('set.world-1204.dump', 'aion_world_1204'), ('set.world-1300.dump', 'aion_world_1300')])
            # A world server's set has no realm file.
            self.assertEqual([db for _, db in pg.backup_set_files(directory / 'other.dump')], ['aion_world_9'])
            self.assertEqual(pg.backup_set_files(directory / 'none.dump'), [])
            calls = []
            with mock.patch.object(pg, 'verify_backup', side_effect=lambda p: calls.append(('verify', p.name))), \
                 mock.patch.object(pg, 'database_exists', return_value=True), \
                 mock.patch.object(pg, 'sql', side_effect=lambda text, **_: calls.append(('sql', text))), \
                 mock.patch.object(pg, 'run', side_effect=lambda binary, args, **_: calls.append((binary, args[-1].name))):
                pg.restore(directory / 'set.dump')
            # Every file is verified before the first database is dropped.
            self.assertEqual([c[0] for c in calls[:3]], ['verify'] * 3)
            self.assertIn('DROP DATABASE aion WITH (FORCE)', calls[3][1])
            self.assertIn('GRANT CONNECT ON DATABASE aion TO aion_app,aion_auth', ' '.join(c[1] for c in calls if c[0] == 'sql'))
            self.assertEqual([c[1] for c in calls if c[0] == 'pg_restore'], ['set.dump', 'set.world-1204.dump', 'set.world-1300.dump'])
            with mock.patch.object(pg, 'restore') as restore, \
                 mock.patch.object(sys, 'argv', ['postgres_dev.py', 'restore', str(directory / 'set.dump')]), \
                 self.assertRaises(SystemExit):
                pg.main()
            restore.assert_not_called()


class RoleStatementTests(unittest.TestCase):
    def test_role_statement_targets_one_local_account(self):
        grant = role_statement('staffuser', 'admin')
        self.assertIn("provider='local' AND subject='staffuser'", grant)
        self.assertIn("'admin'", grant)
        self.assertIn('ON CONFLICT DO NOTHING', grant)
        self.assertTrue(role_statement('staffuser', 'gamemaster', revoke=True).startswith('DELETE FROM auth.account_roles'))
        self.assertIn("'moderator'", role_statement('staffuser', 'moderator'))

    def test_role_statement_rejects_unsafe_input(self):
        for username, role in [("x' OR '1'='1", 'admin'), ('staffuser', 'owner'), ('', 'gamemaster'), ('a' * 33, 'gamemaster'), ('staffuser', 'gm')]:
            with self.assertRaises(ValueError):
                role_statement(username, role)
