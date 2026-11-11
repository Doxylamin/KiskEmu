// The Dashboard's diagnostics card (administrators: viewDiagnostics). One host at a time, the realm or a running world:
// GET /api/admin/diagnostics (?world=N is answered by that world server). Process CPU and memory, the database round trip,
// recent warnings and errors, players, and above all the client requests the server left unanswered, with the last bodies
// of each so a staff member can try a feature that is not implemented and read what the client sent. Polls every 10 s.
import { Fragment, useCallback, useEffect, useMemo, useState } from 'react';
import { Button, Icon } from '../shared/components';
import { Section, StatTile, formatNumber } from '../shared/layout';
import { describe, type Call } from './StaffApp';
import type { WorldEntry } from './types';

const POLL_MS = 10_000;

interface Body { at: string; length: number; hex: string }
interface Opcode { opcode: number; count: number; firstSeen: string; lastSeen: string; bodies: Body[] }
interface Recent { at: string; level: string; category: string; message: string }
export interface Diagnostics {
  role: 'realm' | 'world'; serverTime: string;
  process: { startedAt: string; cores: number; cpu: number; memoryMb: number; heapMb: number; threads: number; gc: number[] };
  database: { ok: boolean; ms: number; error: string | null } | null;
  logs: { warnings5m: number; errors5m: number; warnings60m: number; errors60m: number; recent: Recent[] } | null;
  players: { online: number; connections: number } | null;
  unanswered: { since: string; evicted: number; opcodes: Opcode[] } | null;
  limits: { maxOpcodes: number; maxBodies: number; maxBodyBytes: number };
}
type SortKey = 'opcode' | 'count' | 'firstSeen' | 'lastSeen' | 'length';
const hex4 = (n: number) => '0x' + n.toString(16).padStart(4, '0');
const time = (iso: string) => new Date(iso).toLocaleTimeString();
const isUp = (state: string) => state === 'running' || state === 'online';

export function DiagnosticsCard({ call, worlds, active }: { call: Call; worlds: WorldEntry[]; active: boolean }) {
  const running = worlds.filter(w => isUp(w.runtime.state));
  // null: the realm (this host); else a world id.
  const [host, setHost] = useState<number | null>(null);
  const [data, setData] = useState<Diagnostics | null>(null);
  const [error, setError] = useState('');
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'lastSeen', desc: true });
  const [open, setOpen] = useState<number | null>(null);
  const [copied, setCopied] = useState('');
  useEffect(() => { if (host === null && running.length > 0) setHost(running[0].worldId); }, [running.length]);  // eslint-disable-line react-hooks/exhaustive-deps

  const path = host === null ? '/api/admin/diagnostics' : `/api/admin/diagnostics?world=${host}`;
  const load = useCallback(async () => {
    try { setData(await call<Diagnostics>(path, { world: 'all' })); setError(''); }
    catch (e) { setError(describe(e)); }
  }, [call, path]);
  useEffect(() => {
    if (!active) return;
    setData(null);
    load();
    const timer = window.setInterval(() => { if (document.visibilityState === 'visible') load(); }, POLL_MS);
    return () => clearInterval(timer);
  }, [active, load]);

  async function clear() {
    try { await call(path.replace('/api/admin/diagnostics', '/api/admin/diagnostics/unanswered'), { method: 'DELETE', world: 'all' }); setOpen(null); await load(); }
    catch (e) { setError(describe(e)); }
  }
  async function copy(text: string, key: string) {
    try { await navigator.clipboard.writeText(text); setCopied(key); window.setTimeout(() => setCopied(c => (c === key ? '' : c)), 1500); }
    catch { setError('The browser did not allow copying.'); }
  }

  const rows = useMemo(() => {
    const list = [...(data?.unanswered?.opcodes ?? [])];
    const value = (o: Opcode): number | string => sort.key === 'length' ? (o.bodies[0]?.length ?? 0) : o[sort.key];
    list.sort((a, b) => { const x = value(a), y = value(b); const c = x < y ? -1 : x > y ? 1 : 0; return sort.desc ? -c : c; });
    return list;
  }, [data, sort]);
  const header = (key: SortKey, label: string, num = false) => (
    <th className={num ? 'num' : undefined} aria-sort={sort.key === key ? (sort.desc ? 'descending' : 'ascending') : undefined}>
      <button type="button" className="sort-button" onClick={() => setSort(s => ({ key, desc: s.key === key ? !s.desc : key !== 'opcode' }))}>
        {label}{sort.key === key ? (sort.desc ? ' ↓' : ' ↑') : ''}
      </button>
    </th>
  );

  const p = data?.process;
  const cpu = p ? p.cpu / Math.max(1, p.cores) : 0;
  const logs = data?.logs;
  const db = data?.database;
  return (
    <Section title="Diagnostics" actions={<>
      <select className="select" aria-label="Server" value={host ?? ''} onChange={e => { setHost(e.target.value ? Number(e.target.value) : null); setOpen(null); }}>
        <option value="">Realm (this host)</option>
        {running.map(w => <option key={w.worldId} value={w.worldId}>{w.name}</option>)}
      </select>
      <Button size="sm" onClick={load}><Icon name="refresh" size={16} /> Refresh</Button>
    </>}>
      <div className="stack">
        {error && <p className="status status-error" role="alert">{error}</p>}
        <div className="stat-grid">
          <StatTile label="CPU" value={p ? `${Math.round(cpu * 100)}%` : '–'} tone={cpu > 0.85 ? 'danger' : cpu > 0.6 ? 'warn' : undefined}
            note={p ? `${p.cpu.toFixed(2)} of ${p.cores} cores` : undefined} />
          <StatTile label="Memory" value={p ? `${formatNumber(p.memoryMb)} MB` : '–'} note={p ? `heap ${p.heapMb} MB · ${p.threads} threads · GC ${p.gc.join('/')}` : undefined} />
          <StatTile label="Database" value={db === undefined ? '–' : db === null ? 'None' : db.ok ? `${db.ms} ms` : 'Failed'}
            tone={db && !db.ok ? 'danger' : db && db.ms > 50 ? 'warn' : undefined} note={db ? (db.ok ? 'SELECT 1 round trip' : db.error ?? '') : undefined} />
          <StatTile label="Errors / warnings" value={logs ? `${logs.errors5m} / ${logs.warnings5m}` : '–'} tone={logs && logs.errors5m > 0 ? 'danger' : logs && logs.warnings5m > 0 ? 'warn' : undefined}
            note={logs ? `last 5 min · ${logs.errors60m} / ${logs.warnings60m} in the last hour` : undefined} />
          <StatTile label="Players" value={data?.players ? formatNumber(data.players.online) : '–'} note={data?.players ? `${data.players.connections} connections` : data ? 'No game port here' : undefined} />
        </div>

        <div className="row-between wrap">
          <h3>Client requests without a reply</h3>
          {data?.unanswered && <Button size="sm" onClick={clear}>Clear list</Button>}
        </div>
        <p className="muted small">
          Requests this server handled without sending anything back, since {data?.unanswered ? time(data.unanswered.since) : '…'}.
          Kept in memory only: a restart or Clear starts the list empty. Up to {data?.limits.maxOpcodes ?? 200} opcodes, the
          last {data?.limits.maxBodies ?? 8} bodies of each, {data?.limits.maxBodyBytes ?? 128} bytes each (decrypted, opcode included).
          {data?.unanswered && data.unanswered.evicted > 0 && ` ${data.unanswered.evicted} older opcodes were dropped to make room.`}
        </p>
        {!data ? <p className="muted">Loading…</p> : !data.unanswered || rows.length === 0 ? <p className="muted">None yet. Try a feature in the game and refresh.</p> : (
          <div className="table-wrap">
            <table className="data-table diagnostics-table">
              <thead><tr>{header('opcode', 'Opcode')}{header('count', 'Count', true)}{header('length', 'Last length', true)}
                {header('firstSeen', 'First seen')}{header('lastSeen', 'Last seen')}<th>Last body</th></tr></thead>
              <tbody>
                {rows.map(o => {
                  const last = o.bodies[0];
                  const expanded = open === o.opcode;
                  return (
                    <Fragment key={o.opcode}>
                      <tr>
                        <td><button type="button" className="sort-button mono strong" aria-expanded={expanded} onClick={() => setOpen(expanded ? null : o.opcode)}>
                          {expanded ? '▾ ' : '▸ '}{hex4(o.opcode)}</button></td>
                        <td className="num">{formatNumber(o.count)}</td>
                        <td className="num">{last ? `${last.length} B` : '—'}</td>
                        <td className="mono small">{time(o.firstSeen)}</td>
                        <td className="mono small">{time(o.lastSeen)}</td>
                        <td>{last && <div className="row">
                          <code className="hex-cell">{last.hex}</code>
                          <Button size="sm" onClick={() => copy(last.hex, `${o.opcode}`)}>{copied === `${o.opcode}` ? 'Copied' : 'Copy'}</Button>
                        </div>}</td>
                      </tr>
                      {expanded && o.bodies.map((b, i) => (
                        <tr key={i} className="diagnostics-body">
                          <td colSpan={2} className="muted small">{i === 0 ? 'newest' : `${i + 1}.`}</td>
                          <td className="num small">{b.length} B</td>
                          <td colSpan={2} className="mono small">{time(b.at)}</td>
                          <td><div className="row">
                            <code className="hex-full">{b.hex}{b.length > b.hex.length / 2 ? '…' : ''}</code>
                            <Button size="sm" onClick={() => copy(b.hex, `${o.opcode}-${i}`)}>{copied === `${o.opcode}-${i}` ? 'Copied' : 'Copy'}</Button>
                          </div></td>
                        </tr>
                      ))}
                    </Fragment>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}

        {logs && logs.recent.length > 0 && (
          <details>
            <summary>Recent warnings and errors ({logs.recent.length})</summary>
            <ul className="log diagnostics-log">
              {logs.recent.map((r, i) => (
                <li key={i}><time>{time(r.at)}</time><span className={'pill ' + (r.level === 'warning' ? 'pill-warn' : 'pill-danger')}>{r.level}</span>{' '}
                  <span className="muted small mono">{r.category}</span> <span className="pre">{r.message}</span></li>
              ))}
            </ul>
          </details>
        )}
      </div>
    </Section>
  );
}
