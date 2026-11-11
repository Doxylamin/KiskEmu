// The staff Dashboard: realm-wide figures (GET /api/admin/stats), the population history of the realm or one world
// (GET /api/admin/population?worldId=), and one row per world from the world registry. Kina is asked of each running
// world server (GET /api/admin/economy?world=N); a world that does not answer shows as not reachable.
import { useCallback, useEffect, useMemo, useState } from 'react';
import { Button, Icon } from '../shared/components';
import { EmptyState, Facts, PageHeader, Section, StatTile, formatNumber } from '../shared/layout';
import { describe, type Call } from './StaffApp';
import { DiagnosticsCard } from './Diagnostics';
import { can, type Economy, type PopulationPoint, type StaffProfile, type WorldList } from './types';

interface NamedCount { name: string; count: number }
interface RealmStats {
  accounts: number; staffAccounts: number; disabledAccounts: number; newAccounts30Days: number; newAccounts: { day: string; count: number }[];
  characters: number; charactersByWorld: NamedCount[]; levelBands: NamedCount[]; races: NamedCount[];
  qunaInCirculation: number; openTickets: number; ticketsWaitingOverADay: number; activeMutes: number; activeBans: number;
}
type Kina = number | 'unreachable' | 'loading';
const RANGES = [{ hours: 24, label: '24 hours' }, { hours: 24 * 7, label: '7 days' }, { hours: 24 * 30, label: '30 days' }];
const isUp = (state: string) => state === 'running' || state === 'online';

export function Home({ call, profile, active }: { call: Call; profile: StaffProfile; active: boolean }) {
  const [stats, setStats] = useState<RealmStats | null>(null);
  const [error, setError] = useState('');
  const [worlds, setWorlds] = useState<WorldList | null>(null);
  const [kina, setKina] = useState<Record<number, Kina>>({});
  const [filter, setFilter] = useState<number | null>(null);
  const [hours, setHours] = useState(24);
  const [points, setPoints] = useState<PopulationPoint[] | null>(null);

  const load = useCallback(() => {
    setError('');
    call<RealmStats>('/api/admin/stats').then(setStats).catch(e => setError(describe(e)));
    call<WorldList>('/api/admin/worlds').then(list => {
      setWorlds(list);
      if (!can(profile, 'viewEconomy')) return;
      const running = list.worlds.filter(w => isUp(w.runtime.state));
      setKina(Object.fromEntries(running.map(w => [w.worldId, 'loading' as Kina])));
      for (const w of running) {
        call<Economy>(`/api/admin/economy?world=${w.worldId}`, { world: 'all' })
          .then(report => setKina(k => ({ ...k, [w.worldId]: report.report.totalKina })))
          .catch(() => setKina(k => ({ ...k, [w.worldId]: 'unreachable' })));
      }
    }).catch(() => setWorlds(null));
  }, [call, profile]);

  useEffect(() => { if (active) load(); }, [active, load]);
  useEffect(() => {
    if (!active) return;
    setPoints(null);
    const query = `/api/admin/population?hours=${hours}` + (filter === null ? '' : `&worldId=${filter}`);
    call<PopulationPoint[]>(query, { world: 'all' }).then(setPoints).catch(() => setPoints([]));
  }, [active, call, filter, hours]);

  const rows = worlds?.worlds ?? [];
  const shown = filter === null ? rows : rows.filter(w => w.worldId === filter);
  const online = shown.reduce((sum, w) => sum + (isUp(w.runtime.state) ? w.runtime.players ?? 0 : 0), 0);
  const running = rows.filter(w => isUp(w.runtime.state)).length;
  const characterCount = filter === null ? stats?.characters
    : stats?.charactersByWorld.find(c => c.name === String(filter))?.count ?? 0;

  return (
    <div className="page">
      <PageHeader eyebrow="Overview" title="Dashboard" sub={filter === null ? 'Every world on this realm.' : `${rows.find(w => w.worldId === filter)?.name ?? 'World ' + filter} only.`}
        actions={<>
          {rows.length > 1 && (
            <select className="select" aria-label="Show worlds" value={filter ?? ''} onChange={e => setFilter(e.target.value ? Number(e.target.value) : null)}>
              <option value="">All worlds</option>
              {rows.map(w => <option key={w.worldId} value={w.worldId}>{w.name}</option>)}
            </select>
          )}
          <Button size="sm" onClick={load}><Icon name="refresh" size={16} /> Refresh</Button>
        </>} />
      {error && <p className="status status-error" role="alert">{error}</p>}

      <div className="stat-grid">
        <StatTile label="Online now" value={formatNumber(online)} note={rows.length ? `${running} of ${rows.length} worlds running` : undefined} />
        <StatTile label="Characters" value={formatNumber(characterCount)} />
        {filter === null && <>
          <StatTile label="Accounts" value={formatNumber(stats?.accounts)} note={stats ? `${formatNumber(stats.newAccounts30Days)} new in 30 days` : undefined} />
          <StatTile label="Open tickets" value={formatNumber(stats?.openTickets)} tone={stats && stats.ticketsWaitingOverADay > 0 ? 'warn' : undefined}
            note={stats ? (stats.ticketsWaitingOverADay ? `${stats.ticketsWaitingOverADay} waiting over a day` : 'None waiting over a day') : undefined} />
          <StatTile label="Active sanctions" value={formatNumber(stats ? stats.activeBans + stats.activeMutes : null)}
            note={stats ? `${stats.activeBans} bans · ${stats.activeMutes} mutes` : undefined} />
          <StatTile label="Quna in circulation" value={formatNumber(stats?.qunaInCirculation)} />
        </>}
      </div>

      <Section title="Players online" actions={
        <div className="segmented-sm" role="group" aria-label="Time range">
          {RANGES.map(r => <button key={r.hours} type="button" aria-pressed={hours === r.hours} onClick={() => setHours(r.hours)}>{r.label}</button>)}
        </div>}>
        <PopulationChart points={points} hours={hours} />
      </Section>

      <Section title="Worlds" flush>
        {rows.length === 0 ? <EmptyState title="No worlds registered">This host runs a single world without a registry.</EmptyState> : (
          <div className="table-wrap">
            <table className="data-table">
              <thead><tr><th>World</th><th>Status</th><th className="num">Online</th><th className="num">Connections</th><th className="num">Characters</th>
                {can(profile, 'viewEconomy') && <th className="num">Kina</th>}</tr></thead>
              <tbody>
                {shown.map(w => {
                  const up = isUp(w.runtime.state);
                  const k = kina[w.worldId];
                  return (
                    <tr key={w.worldId}>
                      <td><span className="strong">{w.name}</span> <span className="muted small mono">#{w.worldId}</span></td>
                      <td><span className={'pill ' + (up ? 'pill-ok' : w.enabled ? 'pill-warn' : '')}>{up ? 'Running' : w.enabled ? 'Not running' : 'Disabled'}</span></td>
                      <td className="num">{up ? formatNumber(w.runtime.players ?? 0) : '—'}</td>
                      <td className="num">{up ? formatNumber(w.runtime.connections ?? 0) : '—'}</td>
                      <td className="num">{formatNumber(w.characters)}</td>
                      {can(profile, 'viewEconomy') && <td className="num">{!up ? '—' : k === 'loading' || k === undefined ? '…' : k === 'unreachable' ? <span className="muted">Not reachable</span> : formatNumber(k)}</td>}
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </Section>

      {can(profile, 'viewDiagnostics') && <DiagnosticsCard call={call} worlds={rows} active={active} />}

      {filter === null && stats && (
        <div className="split-2">
          <Section title="Characters by level"><Bars items={stats.levelBands} /></Section>
          <Section title="Characters by side"><Bars items={stats.races} /></Section>
          <Section title="New accounts, last 30 days"><NewAccounts days={stats.newAccounts} /></Section>
          <Section title="Accounts">
            <Facts items={[
              { label: 'Total', value: formatNumber(stats.accounts) },
              { label: 'Staff', value: formatNumber(stats.staffAccounts) },
              { label: 'Disabled', value: formatNumber(stats.disabledAccounts) },
            ]} />
          </Section>
        </div>
      )}
    </div>
  );
}

function PopulationChart({ points, hours }: { points: PopulationPoint[] | null; hours: number }) {
  const shape = useMemo(() => {
    if (!points || points.length < 2) return null;
    const start = Date.now() - hours * 3600_000;
    const max = Math.max(4, ...points.map(p => p.players));
    const top = Math.ceil(max / 4) * 4;
    const line = points.map(p => {
      const x = ((new Date(p.at).getTime() - start) / (hours * 3600_000)) * 100;
      return `${Math.max(0, x).toFixed(2)},${(40 - (p.players / top) * 40).toFixed(2)}`;
    }).join(' ');
    return { line, top, peak: Math.max(...points.map(p => p.players)) };
  }, [points, hours]);
  if (points === null) return <p className="muted">Loading…</p>;
  if (!shape) return <p className="muted">No samples yet. The realm records the population every five minutes.</p>;
  return (
    <div className="stack-sm">
      <p className="muted small">Peak {formatNumber(shape.peak)} · scale to {formatNumber(shape.top)}</p>
      <svg className="line-chart" viewBox="0 0 100 40" preserveAspectRatio="none" role="img" aria-label={`Players online, peak ${shape.peak}`}>
        {[0, 10, 20, 30, 40].map(y => <line key={y} className="grid-line" x1="0" x2="100" y1={y} y2={y} />)}
        <polyline points={shape.line} style={{ stroke: 'var(--accent)' }} />
      </svg>
      <div className="chart-axis"><span>{hours <= 24 ? `${hours} h ago` : `${hours / 24} days ago`}</span><span>Now</span></div>
    </div>
  );
}

function Bars({ items }: { items: NamedCount[] }) {
  const max = Math.max(1, ...items.map(i => i.count));
  if (items.length === 0) return <p className="muted">No characters yet.</p>;
  return (
    <ul className="bar-list">
      {items.map(i => (
        <li key={i.name}>
          <span className="bar-label">{i.name}</span>
          <span className="bar-track"><span className="bar-fill" style={{ width: `${(i.count / max) * 100}%` }} /></span>
          <span className="num mono">{formatNumber(i.count)}</span>
        </li>
      ))}
    </ul>
  );
}

function NewAccounts({ days }: { days: { day: string; count: number }[] }) {
  const byDay = new Map(days.map(d => [d.day.slice(0, 10), d.count]));
  const list = Array.from({ length: 30 }, (_, i) => {
    const date = new Date(Date.now() - (29 - i) * 86400_000).toISOString().slice(0, 10);
    return { date, count: byDay.get(date) ?? 0 };
  });
  const max = Math.max(1, ...list.map(d => d.count));
  const total = list.reduce((s, d) => s + d.count, 0);
  return (
    <div className="stack-sm">
      <p className="muted small">{formatNumber(total)} in 30 days</p>
      <div className="day-bars" role="img" aria-label={`${total} new accounts in the last 30 days`}>
        {list.map(d => <span key={d.date} title={`${d.date}: ${d.count}`} style={{ height: `${Math.max(2, (d.count / max) * 100)}%` }} />)}
      </div>
    </div>
  );
}
