// Staff dashboard: live overview with the game's maps, characters (level/class/roles, items), announcements,
// the activity log and server health, through the audited /api/admin endpoints. The server checks roles on every
// request; this page only hides controls. "Keep me signed in" keeps the session token in localStorage (a 30-day
// session); otherwise it lives in sessionStorage until the tab closes. The world picker chooses the world server the
// world tools act on (live map, players, characters, rewards, maintenance, announcements, server health, restart): the auth host forwards
// those requests (?world=…) to that world server; everything else is the realm's.
import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { api, ApiError } from '../api';
import { Button, Icon, Status, type StatusMessage } from '../ui/components';
import { Activity } from './Activity';
import { Broadcast } from './Broadcast';
import { NewAccountDialog } from './NewAccount';
import { Dashboard } from './Dashboard';
import { Economy } from './Economy';
import { Events } from './Events';
import { Moderation } from './Moderation';
import { Overview } from './Overview';
import { Rewards } from './Rewards';
import { Shop } from './Shop';
import { ServerView } from './Server';
import { Worlds } from './Worlds';
import { ERRORS, ROLE_LABEL, type StaffProfile, type WorldList } from './types';

export function describe(error: unknown): string {
  if (!(error instanceof ApiError)) return 'Something went wrong.';
  if (error.status === 0) return 'The server could not be reached.';
  if (error.status === 429) return 'Too many requests. Wait a minute and try again.';
  return (error.code && ERRORS[error.code]) || 'The server could not complete this request.';
}

export interface Session { token: string; username: string; profile: StaffProfile }
/** world: 'all' sends a world tool to the auth host instead (every world, e.g. an announcement). */
export type Call = <T>(path: string, options?: { method?: string; body?: unknown; world?: 'all' }) => Promise<T>;

// The staff endpoints a world server answers for its own world: its live state and the data in its own database
// (characters, inventories, GM commands, rewards, gameplay audit). Accounts, sanctions, tickets and the catalogue stay
// the realm's (server/DATABASES.md).
const WORLD_TOOLS = /^\/api\/admin\/(live|server|restart|maintenance|announce|economy|players\/|maps\/\d+\/monsters|characters|commands|rewards|audit|shop\/orders)/;
const WORLD_KEY = 'aion-staff-world';
function withWorld(path: string, world: number | null): string {
  if (world === null || !WORLD_TOOLS.test(path)) return path;
  return path + (path.includes('?') ? '&' : '?') + 'world=' + world;
}
function readWorld(): number | null {
  try { const v = Number(window.localStorage.getItem(WORLD_KEY)); return Number.isInteger(v) && v > 0 ? v : null; } catch { return null; }
}
type View = 'overview' | 'characters' | 'moderation' | 'broadcast' | 'rewards' | 'shop' | 'economy' | 'activity' | 'worlds' | 'server';

const STORE_KEY = 'aion-staff-session';
interface Saved { token: string; username: string }

function readSaved(): Saved | null {
  for (const store of [() => window.localStorage, () => window.sessionStorage]) {
    try {
      const raw = store().getItem(STORE_KEY);
      if (raw) {
        const saved = JSON.parse(raw) as Saved;
        if (typeof saved.token === 'string' && typeof saved.username === 'string') return saved;
      }
    } catch { /* storage blocked: sign in each time */ }
  }
  return null;
}
function writeSaved(saved: Saved | null, remember = false) {
  for (const [store, keep] of [[() => window.localStorage, remember], [() => window.sessionStorage, !remember]] as const) {
    try {
      if (saved && keep) store().setItem(STORE_KEY, JSON.stringify(saved));
      else store().removeItem(STORE_KEY);
    } catch { /* ignore */ }
  }
}

const NAV: { id: View; label: string; icon: string }[] = [
  { id: 'overview', label: 'Live map', icon: 'map' },
  { id: 'characters', label: 'Characters', icon: 'players' },
  { id: 'moderation', label: 'Moderation', icon: 'shield' },
  { id: 'broadcast', label: 'Announcements', icon: 'broadcast' },
  { id: 'rewards', label: 'Rewards', icon: 'gift' },
  { id: 'shop', label: 'Shop', icon: 'shop' },
  { id: 'economy', label: 'Economy', icon: 'coins' },
  { id: 'activity', label: 'Activity log', icon: 'activity' },
  { id: 'worlds', label: 'Worlds', icon: 'globe' },
  { id: 'server', label: 'Server', icon: 'server' },
];

export function AdminApp() {
  const [session, setSession] = useState<Session | null>(null);
  const [restoring, setRestoring] = useState(() => readSaved() !== null);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [creating, setCreating] = useState(false);
  const [view, setView] = useState<View>('overview');
  const [focus, setFocus] = useState<{ id: string; n: number } | null>(null);
  const [worlds, setWorlds] = useState<{ worldId: number; name: string; up: boolean }[]>([]);
  const [world, setWorld] = useState<number | null>(readWorld);

  // A remembered session: still valid and still staff?
  useEffect(() => {
    const saved = readSaved();
    if (!saved) return;
    api<StaffProfile>('/api/admin/me', { token: saved.token })
      .then(profile => setSession({ ...saved, profile }))
      .catch(() => writeSaved(null))
      .finally(() => setRestoring(false));
  }, []);

  const signedOut = useCallback((text = '') => { writeSaved(null); setSession(null); setMessage(text ? { text, kind: '' } : null); }, []);
  const call: Call = useCallback(async (path, options = {}) => {
    const { world: scope, ...rest } = options;
    try { return await api(scope === 'all' ? path : withWorld(path, world), { ...rest, token: session?.token }); }
    catch (error) {
      if (error instanceof ApiError && error.status === 401) signedOut('Your session ended. Sign in again.');
      throw error;
    }
  }, [session, signedOut, world]);

  // The world servers for the picker (refreshed every 15 s); the remembered choice, else the first one that is up.
  useEffect(() => {
    if (!session) return;
    let live = true;
    const load = () => api<WorldList>('/api/admin/worlds', { token: session.token }).then(list => {
      if (!live) return;
      const entries = list.worlds.map(w => ({ worldId: w.worldId, name: w.name, up: w.runtime.state === 'running' || w.runtime.state === 'online' }));
      setWorlds(entries);
      setWorld(current => current !== null && entries.some(w => w.worldId === current) ? current
        : (entries.find(w => w.up) ?? entries[0])?.worldId ?? null);
    }).catch(() => { /* single-world host: no registry */ });
    load();
    const timer = window.setInterval(load, 15000);
    return () => { live = false; window.clearInterval(timer); };
  }, [session]);
  useEffect(() => { try { if (world !== null) window.localStorage.setItem(WORLD_KEY, String(world)); } catch { /* ignore */ } }, [world]);

  async function logout() {
    await api('/api/admin/wardrobe-access', { method: 'DELETE' }).catch(() => {});
    if (session) await api('/api/accounts/logout', { method: 'POST', token: session.token }).catch(() => {});
    signedOut('Signed out.');
  }

  const openCharacter = (id: string) => { setFocus(f => ({ id, n: (f?.n ?? 0) + 1 })); setView('characters'); };

  if (!session) {
    return (
      <div className="app app-signin">
        {restoring ? <main className="signin"><p className="muted">Signing in…</p></main>
          : <SignIn message={message} onSignedIn={(s, remember) => { writeSaved({ token: s.token, username: s.username }, remember); setSession(s); }} onMessage={setMessage} />}
      </div>
    );
  }

  const isAdmin = session.profile.roles.includes('admin');
  return (
    <div className="app app-shell">
      <aside className="sidebar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true" />
          <div>
            <strong>Staff</strong>
            <span className="muted small block">Game administration</span>
          </div>
        </div>
        {worlds.length > 0 && (
          <div className="world-picker field">
            <label htmlFor="staff-world" className="small muted">World server</label>
            <select id="staff-world" className="select" value={world ?? ''} onChange={e => setWorld(Number(e.target.value))}>
              {worlds.map(w => <option key={w.worldId} value={w.worldId}>{w.name}{w.up ? '' : ' (offline)'}</option>)}
            </select>
          </div>
        )}
        <nav className="nav" aria-label="Dashboard">
          {NAV.map(item => (
            <button key={item.id} type="button" className="nav-item" aria-current={view === item.id ? 'page' : undefined} onClick={() => setView(item.id)}>
              <Icon name={item.icon} /> <span>{item.label}</span>
            </button>
          ))}
        </nav>
        <div className="sidebar-foot">
          {isAdmin && <Button size="sm" block onClick={() => setCreating(true)}><Icon name="plus" size={16} /> New account</Button>}
          <div className="me">
            <span className="avatar" aria-hidden="true">{session.username.slice(0, 1).toUpperCase()}</span>
            <div className="grow">
              <span className="truncate block">{session.username}</span>
              <span className="small accent">{session.profile.roles.map(r => ROLE_LABEL[r] ?? r).join(' · ')}</span>
            </div>
            <button type="button" className="icon-btn" onClick={logout} title="Sign out" aria-label="Sign out"><Icon name="logout" /></button>
          </div>
        </div>
      </aside>
      <div className="content">
        <div className="view" hidden={view !== 'overview'}>
          <Overview call={call} token={session.token} active={view === 'overview'} onOpen={openCharacter} />
        </div>
        <div className="view" hidden={view !== 'characters'}><Dashboard session={session} call={call} focus={focus} /></div>
        <div className="view" hidden={view !== 'moderation'}><Moderation call={call} active={view === 'moderation'} /></div>
        <div className="view" hidden={view !== 'broadcast'}>
          <div className="page-stack"><Broadcast call={call} active={view === 'broadcast'} worldName={worlds.find(w => w.worldId === world)?.name ?? null} /><div className="page page-tail"><Events call={call} active={view === 'broadcast'} /></div></div>
        </div>
        <div className="view" hidden={view !== 'rewards'}><Rewards call={call} isAdmin={isAdmin} /></div>
        <div className="view" hidden={view !== 'shop'}>
          <Shop call={call} active={view === 'shop'} isAdmin={isAdmin} worldName={worlds.find(w => w.worldId === world)?.name ?? null} />
        </div>
        <div className="view" hidden={view !== 'economy'}><Economy call={call} active={view === 'economy'} onOpen={openCharacter} /></div>
        <div className="view" hidden={view !== 'activity'}><Activity call={call} active={view === 'activity'} onOpen={openCharacter} /></div>
        <div className="view" hidden={view !== 'worlds'}><Worlds call={call} active={view === 'worlds'} /></div>
        <div className="view" hidden={view !== 'server'}><ServerView call={call} token={session.token} active={view === 'server'} isAdmin={isAdmin} /></div>
      </div>
      <NewAccountDialog open={creating} call={call} onClose={() => setCreating(false)} />
    </div>
  );
}

function SignIn({ message, onSignedIn, onMessage }:
  { message: StatusMessage | null; onSignedIn: (s: Session, remember: boolean) => void; onMessage: (m: StatusMessage) => void }) {
  const [busy, setBusy] = useState(false);
  // Accounts with an authenticator answer the password with a challenge; the code finishes the sign-in.
  const [challenge, setChallenge] = useState<{ token: string; username: string; remember: boolean } | null>(null);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    const username = challenge?.username ?? String(data.get('username'));
    const remember = challenge?.remember ?? data.get('remember') === 'on';
    setBusy(true);
    try {
      let token: string;
      if (challenge) {
        const code = String(data.get('code') ?? '').trim();
        const isCode = /^[0-9]{6}$/.test(code);
        ({ token } = await api<{ token: string }>('/api/accounts/login/totp', {
          method: 'POST', body: { challengeToken: challenge.token, code: isCode ? code : null, recoveryCode: isCode ? null : code.toUpperCase() },
        }));
        setChallenge(null);
      } else {
        const reply = await api<{ token?: string; requiresTotp?: boolean; challengeToken?: string }>('/api/accounts/login',
          { method: 'POST', body: { username, password: data.get('password'), remember } });
        if (reply.requiresTotp && reply.challengeToken) { setChallenge({ token: reply.challengeToken, username, remember }); return; }
        if (!reply.token) throw new Error('The server sent no session.');
        token = reply.token;
      }
      try {
        const profile = await api<StaffProfile>('/api/admin/me', { token });
        onSignedIn({ token, username, profile }, remember);
      } catch (error) {
        await api('/api/accounts/logout', { method: 'POST', token }).catch(() => {});
        onMessage({ text: error instanceof ApiError && error.code === 'forbidden' ? ERRORS.forbidden : describe(error), kind: 'error' });
      }
    } catch (error) {
      if (challenge) setChallenge(null);
      onMessage({ text: error instanceof ApiError && error.status === 401 ? 'Sign-in failed. Check your details.' : describe(error), kind: 'error' });
    } finally {
      setBusy(false);
      const password = form.elements.namedItem('password');
      if (password instanceof HTMLInputElement) password.value = '';
    }
  }
  return (
    <main className="signin">
      <form className="card card-body stack signin-card" onSubmit={submit}>
        <div className="brand brand-center">
          <span className="brand-mark" aria-hidden="true" />
        </div>
        <div className="stack-sm center">
          <h2>Staff sign-in</h2>
          <p className="muted">Use an account with the GameMaster or Admin role.</p>
        </div>
        {challenge ? (
          <div className="field">
            <label htmlFor="code">Authenticator code or recovery code</label>
            <input id="code" name="code" className="input" autoComplete="one-time-code" required maxLength={32} autoFocus />
          </div>
        ) : (
          <>
            <div className="field">
              <label htmlFor="username">Username</label>
              <input id="username" name="username" className="input" autoComplete="username" required maxLength={32} />
            </div>
            <div className="field">
              <label htmlFor="password">Password</label>
              <input id="password" name="password" type="password" className="input" autoComplete="current-password" required maxLength={256} />
            </div>
            <label className="check">
              <input type="checkbox" name="remember" defaultChecked />
              <span>Keep me signed in on this computer (30 days)</span>
            </label>
          </>
        )}
        <Button type="submit" variant="primary" block busy={busy}>{challenge ? 'Verify' : 'Sign in'}</Button>
        <Status message={message} />
      </form>
    </main>
  );
}
