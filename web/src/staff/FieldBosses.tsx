// Field bosses of the picked world (docs/FIELD_BOSSES.md): each boss of the client's field boss list with its state, last
// kill, next respawn and respawn range. Moderators respawn a boss now; administrators set a boss's range in hours or reset
// it to the default. The world server stores the timers, so a restart keeps a dead boss dead until its time.
import { useCallback, useEffect, useState } from 'react';
import { Badge, Button, ConfirmDialog, PromptDialog, Status, type ConfirmRequest, type PromptRequest, type StatusMessage } from '../shared/components';
import { npcName } from './Monsters';
import { describe, type Call } from './StaffApp';
import type { FieldBoss, FieldBossList } from './types';

const hours = (minutes: number) => `${Math.round(minutes / 6) / 10} h`;
const range = (b: FieldBoss) => b.minMinutes === b.maxMinutes ? hours(b.minMinutes) : `${hours(b.minMinutes)} – ${hours(b.maxMinutes)}`;
const defaultTimer = (b: FieldBoss) => b.schedule ? `the official schedule, ${b.schedule} (server time)` : `${hours(b.defaultMinMinutes)} – ${hours(b.defaultMaxMinutes)}`;

function until(ms: number, now: number) {
  const s = Math.max(0, Math.round((ms - now) / 1000));
  const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60);
  return h > 0 ? `in ${h} h ${m} min` : m > 0 ? `in ${m} min` : 'in under a minute';
}

export function FieldBosses({ call, active, canRespawn, canEditTimers }: { call: Call; active: boolean; canRespawn: boolean; canEditTimers: boolean }) {
  const [list, setList] = useState<FieldBossList | null>(null);
  const [filter, setFilter] = useState('');
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const [prompt, setPrompt] = useState<PromptRequest | null>(null);
  const [now, setNow] = useState(() => Date.now());

  const load = useCallback(async () => {
    try { setList(await call<FieldBossList>('/api/admin/field-bosses')); }
    catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
  }, [call]);
  useEffect(() => {
    if (!active) return;
    load();
    const timer = window.setInterval(() => { if (document.visibilityState === 'visible') load(); }, 15000);
    const tick = window.setInterval(() => setNow(Date.now()), 1000);
    return () => { clearInterval(timer); clearInterval(tick); };
  }, [active, load]);

  async function act(path: string, method: string, done: string, body?: unknown) {
    try {
      await call(`/api/admin/field-bosses/${path}`, { method, body });
      setMessage({ text: done, kind: 'ok' });
      load();
    } catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
  }

  const respawn = (b: FieldBoss) => setConfirm({
    title: `Respawn ${npcName(b.name)} now?`, confirmLabel: 'Respawn now',
    body: 'The boss appears at once for players on its map (or as soon as someone is there). Its last kill stays on record.',
    onConfirm: () => act(`${b.code}/respawn`, 'POST', `${npcName(b.name)} respawns now.`),
  });
  const editTimer = (b: FieldBoss) => setPrompt({
    title: `Respawn timer of ${npcName(b.name)}`, confirmLabel: 'Save',
    body: `After each kill the boss returns after a random time in this range. Default: ${defaultTimer(b)}. The current wait is not changed.`,
    fields: [
      { name: 'min', label: 'Shortest (hours)', type: 'number', required: true, value: String(b.minMinutes / 60) },
      { name: 'max', label: 'Longest (hours)', type: 'number', required: true, value: String(b.maxMinutes / 60) },
    ],
    onSubmit: v => act(`${b.code}/timer`, 'PUT', `Timer of ${npcName(b.name)} saved.`,
      { minMinutes: Math.round(Number(v.min) * 60), maxMinutes: Math.round(Number(v.max) * 60) }),
  });
  const resetTimer = (b: FieldBoss) => setConfirm({
    title: `Reset the timer of ${npcName(b.name)}?`, confirmLabel: 'Reset',
    body: `Back to the default, ${defaultTimer(b)}, from its next kill on.`,
    onConfirm: () => act(`${b.code}/timer`, 'DELETE', `Timer of ${npcName(b.name)} reset.`),
  });

  const q = filter.trim().toLowerCase();
  const bosses = (list?.bosses ?? []).filter(b => !q || npcName(b.name).toLowerCase().includes(q) || String(b.mapId) === q || String(b.code) === q);
  const dead = (list?.bosses ?? []).filter(b => b.status !== 'alive').length;
  return (
    <main className="page stack">
      <div className="row-between wrap">
        <div>
          <h1>Field bosses</h1>
          <p className="muted small">{list ? `${list.bosses.length} bosses, ${dead} down.` : 'Loading…'}{list && !list.loaded && ' Stored timers are not read yet: the world database is not reachable.'}</p>
        </div>
        <input className="input" placeholder="Filter by name, map or code" value={filter} onChange={e => setFilter(e.target.value)} aria-label="Filter" />
      </div>
      <Status message={message} />
      <section className="card">
        <div className="table-wrap">
          <table className="data-table">
            <thead><tr><th>Boss</th><th>Map</th><th>State</th><th>Last kill</th><th>Next respawn</th><th>Timer</th><th /></tr></thead>
            <tbody>
              {bosses.map(b => (
                <tr key={b.code}>
                  <td data-label="Boss"><strong>{npcName(b.name)}</strong> <span className="faint small">Lv {b.level} · {b.kind} · {b.code}</span></td>
                  <td data-label="Map">{b.mapId}</td>
                  <td data-label="State"><Badge tone={b.status === 'alive' ? 'ok' : b.status === 'dead' ? 'danger' : 'accent'}>{b.status}</Badge></td>
                  <td data-label="Last kill" className="nowrap">{b.killedAt ? new Date(b.killedAt).toLocaleString() : <span className="faint">–</span>}</td>
                  <td data-label="Next respawn" className="nowrap" title={b.respawnAt ? new Date(b.respawnAt).toLocaleString() : undefined}>
                    {b.status === 'dead' && b.respawnAt ? until(b.respawnAt, now) : b.status === 'due' ? 'when a player is on the map' : <span className="faint">–</span>}
                  </td>
                  <td data-label="Timer" className="nowrap">
                    {b.customTimer ? <>{range(b)} <Badge>custom</Badge></>
                      : b.schedule ? <>{b.schedule} <span className="faint small">official schedule</span></>
                      : <>{range(b)} <span className="faint small">default</span></>}
                  </td>
                  <td className="nowrap">
                    {canRespawn && b.status === 'dead' && <Button size="sm" onClick={() => respawn(b)}>Respawn now</Button>}{' '}
                    {canEditTimers && <Button size="sm" variant="ghost" onClick={() => editTimer(b)}>Timer…</Button>}{' '}
                    {canEditTimers && b.customTimer && <Button size="sm" variant="ghost" onClick={() => resetTimer(b)}>Reset</Button>}
                  </td>
                </tr>
              ))}
              {list && bosses.length === 0 && <tr><td colSpan={7} className="empty pad">No field boss matches.</td></tr>}
            </tbody>
          </table>
        </div>
      </section>
      <p className="faint small">The Abyss bosses follow their official schedule (server time). Verteron's waits are fitted to official captures, Altgard's mirror them, and the level-80 regions' ranges are estimates; set your own per boss.</p>
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
      <PromptDialog request={prompt} onClose={() => setPrompt(null)} />
    </main>
  );
}
