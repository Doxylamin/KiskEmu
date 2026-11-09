// World Management: the world servers the auth host's login port lists. The auth host runs no world itself: managed
// world servers run as processes it starts and stops; remote ones run on other machines against the same database and
// are listed while they answer (their control port, when set, also carries chat and the staff tools). Admins add, change, start, stop, restart and remove
// worlds; Game Masters see the list. Polls /api/admin/worlds every 5 s while shown.
import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { Badge, Button, ConfirmDialog, Icon, Status, type ConfirmRequest, type StatusMessage } from '../ui/components';
import { describe, type Call } from './AdminApp';
import { duration } from './Overview';
import type { WorldEntry, WorldList } from './types';

const POLL_MS = 5000;

const STATE: Record<string, { label: string; tone?: 'accent' | 'danger' | 'ok' }> = {
  running: { label: 'Running', tone: 'ok' },
  online: { label: 'Online', tone: 'ok' },
  starting: { label: 'Starting', tone: 'accent' },
  stopping: { label: 'Stopping', tone: 'accent' },
  stopped: { label: 'Stopped' },
  offline: { label: 'Unreachable', tone: 'danger' },
  crashed: { label: 'Crashed', tone: 'danger' },
};
const KIND: Record<string, string> = { managed: 'Managed', remote: 'Remote' };

export function Worlds({ call, active }: { call: Call; active: boolean }) {
  const [list, setList] = useState<WorldList | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [editing, setEditing] = useState<WorldEntry | 'new' | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const [busy, setBusy] = useState<number | null>(null);
  const [now, setNow] = useState(() => Date.now());

  const load = useCallback(async () => {
    try { setList(await call<WorldList>('/api/admin/worlds')); setError(null); }
    catch (e) { setError(describe(e)); }
  }, [call]);
  useEffect(() => {
    if (!active) return;
    load();
    const timers = [window.setInterval(() => { if (document.visibilityState === 'visible') load(); }, POLL_MS), window.setInterval(() => setNow(Date.now()), 1000)];
    return () => timers.forEach(clearInterval);
  }, [active, load]);

  async function act(world: WorldEntry, action: 'start' | 'stop' | 'restart' | 'remove') {
    setBusy(world.worldId);
    try {
      if (action === 'remove') await call(`/api/admin/worlds/${world.worldId}`, { method: 'DELETE' });
      else await call(`/api/admin/worlds/${world.worldId}/${action}`, { method: 'POST' });
      const done = { start: 'is starting', stop: 'is stopping; its players are saved', restart: 'is restarting', remove: 'was removed' }[action];
      setMessage({ text: `${world.name} ${done}.`, kind: 'ok' });
      await load();
    } catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
    finally { setBusy(null); }
  }

  const manage = list?.canManage ?? false;
  const worlds = list?.worlds ?? [];
  const listed = worlds.filter(w => w.listed).length;
  return (
    <main className="page">
      <header className="page-head">
        <div>
          <h1>Worlds</h1>
          <p className="muted">{list ? `${worlds.length} world${worlds.length === 1 ? '' : 's'} · ${listed} in the server list` : 'Loading…'}</p>
        </div>
        <div className="row">
          <Button size="sm" onClick={load}><Icon name="refresh" size={16} /> Refresh</Button>
          {manage && <Button size="sm" variant="primary" onClick={() => setEditing('new')}><Icon name="plus" size={16} /> Add world</Button>}
        </div>
      </header>
      <Status message={error ? { text: error, kind: 'error' } : message} />
      <section className="card">
        <div className="table-wrap">
          <table className="data-table worlds-table">
            <thead><tr><th>World</th><th>Kind</th><th>State</th><th>Players</th><th>Characters</th><th>Game port</th><th>Server list rows</th><th>Level cap</th><th /></tr></thead>
            <tbody>
              {worlds.map(w => {
                const state = STATE[w.runtime.state] ?? { label: w.runtime.state };
                const since = w.runtime.since ? ` for ${duration(Math.max(0, now - Date.parse(w.runtime.since)))}` : '';
                const running = w.enabled;
                return (
                  <tr key={w.worldId}>
                    <td data-label="World"><strong>{w.name}</strong> <span className="faint mono small">#{w.worldId}</span></td>
                    <td data-label="Kind">{KIND[w.kind] ?? w.kind}</td>
                    <td data-label="State" title={w.runtime.lastError ?? undefined}>
                      <Badge tone={state.tone}>{state.label}</Badge>
                      {(w.runtime.state === 'running' || w.runtime.state === 'online') && <span className="faint small">{since}</span>}
                      {w.runtime.lastError && w.runtime.state !== 'running' && <span className="block small danger-text">{w.runtime.lastError}</span>}
                      {w.runtime.restarts > 0 && <span className="block faint small">{w.runtime.restarts} restart{w.runtime.restarts === 1 ? '' : 's'}</span>}
                    </td>
                    <td data-label="Players" className="mono">{w.runtime.players ?? <span className="faint">–</span>}</td>
                    <td data-label="Characters" className="mono">{w.characters}</td>
                    <td data-label="Game port" className="mono">{(w.host ?? list?.publicAddress ?? '') + (w.gamePort ? `:${w.gamePort}` : '')}</td>
                    <td data-label="Server list rows" className="mono small">Light {w.lightServerId} · Dark {w.darkServerId}</td>
                    <td data-label="Level cap" className="mono">{w.levelCap ?? <span className="faint">default</span>}</td>
                    <td className="actions nowrap">
                      {manage && (running
                        ? <>
                            {w.kind === 'managed' && <Button size="sm" busy={busy === w.worldId} onClick={() => setConfirm({ title: `Restart ${w.name}?`,
                              body: 'Players in this world are disconnected and saved; the world comes back in about 30 seconds.', confirmLabel: 'Restart', danger: true,
                              onConfirm: () => act(w, 'restart') })}><Icon name="restart" size={16} /> Restart</Button>}
                            <Button size="sm" variant="danger" busy={busy === w.worldId} onClick={() => setConfirm({ title: `Stop ${w.name}?`,
                              body: w.kind === 'remote' ? 'The world leaves the server list. Its host keeps running until it is stopped there.'
                                : 'Players in this world are disconnected and saved, and the world leaves the server list.',
                              confirmLabel: 'Stop world', danger: true, onConfirm: () => act(w, 'stop') })}>Stop</Button>
                          </>
                        : <Button size="sm" variant="primary" busy={busy === w.worldId} onClick={() => act(w, 'start')}>Start</Button>)}
                      {manage && <button type="button" className="icon-btn" title="Settings" aria-label={`Settings of ${w.name}`} onClick={() => setEditing(w)}><Icon name="open" size={16} /></button>}
                      {manage && !w.enabled && <button type="button" className="icon-btn" title="Remove" aria-label={`Remove ${w.name}`}
                        onClick={() => setConfirm({ title: `Remove ${w.name}?`, body: w.characters > 0
                          ? `This world still has ${w.characters} character${w.characters === 1 ? '' : 's'}; it cannot be removed while they exist.`
                          : 'The world is removed from the registry. Its game port is freed.', confirmLabel: 'Remove world', danger: true, onConfirm: () => act(w, 'remove') })}>
                        <Icon name="kick" size={16} /></button>}
                    </td>
                  </tr>
                );
              })}
              {list && worlds.length === 0 && <tr><td colSpan={9} className="empty pad">No worlds are registered.</td></tr>}
            </tbody>
          </table>
        </div>
      </section>
      <p className="faint small">
        Each world shows as two entries in the game's server list, one per race. Characters belong to the world they were created on.
        {list && !list.supervised && ' This host does not run the login port, so it does not start managed worlds.'}
      </p>
      <WorldDialog editing={editing} call={call} onClose={() => setEditing(null)}
        onSaved={(text) => { setMessage({ text, kind: 'ok' }); setEditing(null); load(); }} />
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </main>
  );
}

function WorldDialog({ editing, call, onClose, onSaved }: { editing: WorldEntry | 'new' | null; call: Call; onClose: () => void; onSaved: (text: string) => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [kind, setKind] = useState('managed');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const world = editing && editing !== 'new' ? editing : null;
  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (editing && !dialog.open) { setKind(world?.kind ?? 'managed'); setError(null); dialog.showModal(); }
    if (!editing && dialog.open) dialog.close();
  }, [editing, world]);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    const text = (name: string) => String(data.get(name) ?? '').trim();
    const number = (name: string) => { const v = text(name); return v === '' ? null : Number(v); };
    const body = {
      worldId: world ? null : number('worldId'), name: text('name'), kind,
      host: text('host'), gamePort: number('gamePort'), internalPort: number('internalPort'),
      lightServerId: number('lightServerId'), darkServerId: number('darkServerId'), levelCap: number('levelCap') ?? (world ? 0 : null),
    };
    setBusy(true);
    try {
      if (world) await call(`/api/admin/worlds/${world.worldId}`, { method: 'PUT', body });
      else await call('/api/admin/worlds', { method: 'POST', body });
      onSaved(world ? `${body.name || world.name} was saved.` : `${body.name} was added. Start it to list it in the game.`);
    } catch (e) { setError(describe(e)); }
    finally { setBusy(false); }
  }

  const locked = !!world && world.enabled;
  return (
    <dialog ref={ref} className="dialog" onClose={onClose} aria-labelledby="world-title">
      {editing && (
        <form onSubmit={submit}>
          <div className="dialog-body stack">
            <h2 id="world-title">{world ? `Settings of ${world.name}` : 'Add a world'}</h2>
            {locked && <p className="muted small">Stop the world to change its ports or server list rows.</p>}
            <div className="field">
              <label htmlFor="world-name">Name (shown in the server list)</label>
              <input id="world-name" name="name" className="input" maxLength={32} required defaultValue={world?.name} autoFocus />
            </div>
            <div className="field">
              <label htmlFor="world-kind">Runs</label>
              <select id="world-kind" className="select" value={kind} disabled={locked} onChange={e => setKind(e.target.value)}>
                <option value="managed">On this host (started and stopped here)</option>
                <option value="remote">On another machine (listed while it answers)</option>
              </select>
            </div>
            <div className="form-row">
              {!world && <div className="field"><label htmlFor="world-id">World id</label>
                <input id="world-id" name="worldId" className="input" type="number" min={1} max={65535} placeholder="next free" /></div>}
              <div className="field"><label htmlFor="world-cap">Level cap</label>
                <input id="world-cap" name="levelCap" className="input" type="number" min={1} max={100} placeholder="default" defaultValue={world?.levelCap ?? ''} /></div>
            </div>
            <div className="form-row">
              <div className="field"><label htmlFor="world-host">Address players connect to</label>
                <input id="world-host" name="host" className="input" placeholder={kind === 'remote' ? '203.0.113.7' : 'this host'} disabled={locked}
                  required={kind === 'remote'} defaultValue={world?.host ?? ''} /></div>
              <div className="field"><label htmlFor="world-port">Game port</label>
                <input id="world-port" name="gamePort" className="input" type="number" min={1} max={65535} placeholder="next free" disabled={locked}
                  required={kind === 'remote'} defaultValue={world?.gamePort ?? ''} /></div>
              <div className="field"><label htmlFor="world-internal">{kind === 'managed' ? 'Control port (loopback)' : 'Control port (chat, staff tools)'}</label>
                <input id="world-internal" name="internalPort" className="input" type="number" min={1} max={65535}
                  placeholder={kind === 'managed' ? 'next free' : 'none'} disabled={locked} defaultValue={world?.internalPort ?? ''} /></div>
            </div>
            <div className="form-row">
              <div className="field"><label htmlFor="world-light">Light row id</label>
                <input id="world-light" name="lightServerId" className="input" type="number" min={1} max={65535} placeholder="next free" disabled={locked}
                  defaultValue={world?.lightServerId ?? ''} /></div>
              <div className="field"><label htmlFor="world-dark">Dark row id</label>
                <input id="world-dark" name="darkServerId" className="input" type="number" min={1} max={65535} placeholder="next free" disabled={locked}
                  defaultValue={world?.darkServerId ?? ''} /></div>
            </div>
            <p className="faint small">Open the game port in the firewall. Row ids 1361–1399 and 2361–2399 have no name in the client, so it shows the name above.
              {world && ' The level cap applies at the next start of the world.'}</p>
            <Status message={error ? { text: error, kind: 'error' } : null} />
          </div>
          <div className="dialog-actions">
            <Button variant="ghost" onClick={onClose}>Cancel</Button>
            <Button type="submit" variant="primary" busy={busy}>{world ? 'Save' : 'Add world'}</Button>
          </div>
        </form>
      )}
    </dialog>
  );
}
