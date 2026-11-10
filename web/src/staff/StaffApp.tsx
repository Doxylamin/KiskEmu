// Staff dashboard: the realm-wide Dashboard, accounts, the live map with the game's maps, characters, moderation,
// announcements, rewards, the shop, the economy, worlds, server health and the activity log, through the audited
// /api/admin endpoints. The server checks the rank on every request; this page only hides controls. Pages have hash
// URLs (#/accounts/<id>) so a refresh or Back keeps them. "Keep me signed in" keeps the staff session in localStorage;
// otherwise it lives in sessionStorage until the tab closes. The world picker chooses the world server the world tools
// act on: the realm forwards those requests (?world=…) to that world server; everything else is the realm's.
import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { api, ApiError } from '../shared/api';
import { pageTitle, siteMark, useSiteName } from '../shared/site';
import { Button, Icon, type StatusMessage } from '../shared/components';
import { hashOf, useRoute } from '../shared/router';
import { readSession, writeSession, type SavedSession } from '../shared/session';
import { AccountList, AccountView } from './Accounts';
import { Activity } from './Activity';
import { Broadcast } from './Broadcast';
import { NewAccountDialog } from './NewAccount';
import { Dashboard } from './Dashboard';
import { Economy } from './Economy';
import { Events } from './Events';
import { Home } from './Home';
import { Moderation } from './Moderation';
import { Overview } from './Overview';
import { Rewards } from './Rewards';
import { Shop } from './Shop';
import { ServerView } from './Server';
import { Worlds } from './Worlds';
import { SignIn } from './SignIn';
import { ERRORS, can, type StaffProfile, type WorldList } from './types';

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
const WORLD_TOOLS = /^\/api\/admin\/(live|server|restart|maintenance|announce|economy|players\/|maps\/\d+\/monsters|characters|commands|rewards|audit|debug|shop\/orders)/;
const WORLD_KEY = 'aion-staff-world';
function withWorld(path: string, world: number | null): string {
  if (world === null || !WORLD_TOOLS.test(path)) return path;
  return path + (path.includes('?') ? '&' : '?') + 'world=' + world;
}
function readWorld(): number | null {
  try { const v = Number(window.localStorage.getItem(WORLD_KEY)); return Number.isInteger(v) && v > 0 ? v : null; } catch { return null; }
}
type View = 'dashboard' | 'overview' | 'accounts' | 'characters' | 'moderation' | 'broadcast' | 'rewards' | 'shop' | 'economy' | 'activity' | 'worlds' | 'server' | 'staff';
/** The pages whose data is one world server's: the world picker shows on these. */
const WORLD_VIEWS: View[] = ['overview', 'characters', 'broadcast', 'rewards', 'shop', 'economy', 'activity', 'server'];

const STORE_KEY = 'aion-staff-session';
const readSaved = () => readSession(STORE_KEY);
const writeSaved = (saved: SavedSession | null, remember = false) => writeSession(STORE_KEY, saved, remember);

interface NavEntry { id: View; label: string; icon: string; permission?: string }
const NAV: { name: string; items: NavEntry[] }[] = [
  { name: 'Overview', items: [
    { id: 'dashboard', label: 'Dashboard', icon: 'overview', permission: 'viewDashboard' },
    { id: 'overview', label: 'Live map', icon: 'map', permission: 'viewDashboard' },
  ] },
  { name: 'Players', items: [
    { id: 'accounts', label: 'Accounts', icon: 'players', permission: 'viewAccounts' },
    { id: 'characters', label: 'Characters', icon: 'players' },
    { id: 'moderation', label: 'Moderation', icon: 'shield', permission: 'handleTickets' },
  ] },
  { name: 'Game', items: [
    { id: 'broadcast', label: 'Announcements & events', icon: 'broadcast', permission: 'announce' },
    { id: 'rewards', label: 'Rewards', icon: 'gift', permission: 'rewardCharacters' },
    { id: 'shop', label: 'Shop', icon: 'shop', permission: 'viewShop' },
    { id: 'economy', label: 'Economy', icon: 'coins', permission: 'viewEconomy' },
  ] },
  { name: 'System', items: [
    { id: 'worlds', label: 'Worlds', icon: 'globe' },
    { id: 'server', label: 'Server', icon: 'server' },
    { id: 'staff', label: 'Staff', icon: 'shield', permission: 'viewAccounts' },
    { id: 'activity', label: 'Activity log', icon: 'activity', permission: 'viewAudit' },
  ] },
];
const LABELS = Object.fromEntries(NAV.flatMap(g => g.items.map(i => [i.id, [g.name, i.label]]))) as Record<View, [string, string]>;

export function StaffApp() {
  const [session, setSession] = useState<Session | null>(null);
  const [restoring, setRestoring] = useState(() => readSaved() !== null);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [creating, setCreating] = useState(false);
  const [route, navigate] = useRoute();
  const [focus, setFocus] = useState<{ id: string; n: number } | null>(null);
  const [worlds, setWorlds] = useState<{ worldId: number; name: string; up: boolean }[]>([]);
  const [world, setWorld] = useState<number | null>(readWorld);
  // The world pages wait for the first world list: before it, their requests would go to the realm without ?world=.
  const [worldsReady, setWorldsReady] = useState(false);
  const site = useSiteName();
  useEffect(() => { document.title = pageTitle('Staff', site); }, [site]);

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
    }).catch(() => { /* single-world host: no registry */ }).finally(() => { if (live) setWorldsReady(true); });
    load();
    const timer = window.setInterval(load, 15000);
    return () => { live = false; window.clearInterval(timer); };
  }, [session]);
  useEffect(() => { try { if (world !== null) window.localStorage.setItem(WORLD_KEY, String(world)); } catch { /* ignore */ } }, [world]);

  async function logout() {
    await api('/api/admin/wardrobe-access', { method: 'DELETE' }).catch(() => {});
    if (session) await api('/api/admin/session', { method: 'DELETE', token: session.token }).catch(() => {});
    signedOut('Signed out.');
  }

  /** A character page; on another world than the picked one, the picker moves to that world first. */
  const openCharacter = (id: string, worldId?: number) => {
    if (worldId !== undefined && worlds.some(w => w.worldId === worldId)) setWorld(worldId);
    setFocus(f => ({ id, n: (f?.n ?? 0) + 1 }));
    navigate(['characters']);
  };

  if (!session) {
    return (
      <div className="app app-signin">
        {restoring ? <main className="signin"><p className="muted">Signing in…</p></main>
          : <SignIn message={message} onSignedIn={(s, remember) => { writeSaved({ token: s.token, username: s.username }, remember); setSession(s); }} onMessage={setMessage} />}
      </div>
    );
  }

  const allowed = (permission: string) => can(session.profile, permission);
  const visible = NAV.map(g => ({ ...g, items: g.items.filter(i => !i.permission || allowed(i.permission)) })).filter(g => g.items.length > 0);
  const asked = (route[0] ?? '') as View;
  const view: View = visible.some(g => g.items.some(i => i.id === asked)) ? asked : visible[0]?.items[0]?.id ?? 'characters';
  const accountId = view === 'accounts' ? route[1] ?? null : null;
  const worldName = worlds.find(w => w.worldId === world)?.name ?? null;
  const [group, title] = LABELS[view];
  const page = (id: View, content: ReactNode) => worldsReady ? <div className="view" hidden={view !== id}>{content}</div>
    : view === id ? <div className="view" key={id}><p className="muted page">Loading…</p></div> : null;
  return (
    <div className="app app-shell">
      <aside className="sidebar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">{siteMark(site)}</span>
          <div>
            <strong>{site}</strong>
            <span className="muted small block">Staff dashboard</span>
          </div>
        </div>
        <nav className="nav" aria-label="Staff pages">
          {visible.map(g => (
            <div className="nav-group" key={g.name}>
              <p className="nav-heading">{g.name}</p>
              {g.items.map(item => (
                <a key={item.id} href={hashOf(item.id)} className="nav-item" aria-current={view === item.id ? 'page' : undefined}>
                  <Icon name={item.icon} /> <span>{item.label}</span>
                </a>
              ))}
            </div>
          ))}
        </nav>
        <div className="sidebar-foot">
          <div className="me">
            <span className="avatar" aria-hidden="true">{session.username.slice(0, 1).toUpperCase()}</span>
            <div className="grow">
              <span className="truncate block">{session.username}</span>
              <span className="small accent">{session.profile.rankLabel ?? 'Staff'}</span>
            </div>
            <button type="button" className="icon-btn" onClick={logout} title="Sign out" aria-label="Sign out"><Icon name="logout" /></button>
          </div>
        </div>
      </aside>
      <div className="content">
        <header className="topbar">
          <p className="topbar-title"><span className="muted">{group} /</span> {title}</p>
          <div className="topbar-tools">
            {worlds.length > 0 && WORLD_VIEWS.includes(view) && (
              <label className="world-picker">
                <span className="small muted">World</span>
                <select className="select" value={world ?? ''} onChange={e => setWorld(Number(e.target.value))}>
                  {worlds.map(w => <option key={w.worldId} value={w.worldId}>{w.name}{w.up ? '' : ' (offline)'}</option>)}
                </select>
              </label>
            )}
            {allowed('manageAccounts') && <Button size="sm" onClick={() => setCreating(true)}><Icon name="plus" size={16} /> New account</Button>}
          </div>
        </header>
        {view === 'dashboard' && <div className="view"><Home call={call} profile={session.profile} active /></div>}
        {view === 'accounts' && (
          <div className="view">
            {accountId ? <AccountView key={accountId} call={call} profile={session.profile} accountId={accountId} onOpenCharacter={openCharacter} />
              : <AccountList call={call} active />}
          </div>
        )}
        {view === 'staff' && <div className="view"><AccountList call={call} active staffOnly /></div>}
        {page('overview', <Overview call={call} token={session.token} active={view === 'overview'} onOpen={openCharacter} />)}
        {page('characters', <Dashboard session={session} call={call} focus={focus} />)}
        {page('moderation', <Moderation call={call} active={view === 'moderation'} />)}
        {page('broadcast', <div className="page-stack"><Broadcast call={call} active={view === 'broadcast'} worldName={worldName} /><div className="page page-tail"><Events call={call} active={view === 'broadcast'} /></div></div>)}
        {page('rewards', <Rewards call={call} isAdmin={allowed('rewardEveryone')} />)}
        {page('shop', <Shop call={call} active={view === 'shop'} isAdmin={allowed('editShop')} worldName={worldName} />)}
        {page('economy', <Economy call={call} active={view === 'economy'} onOpen={openCharacter} />)}
        {page('activity', <Activity call={call} active={view === 'activity'} onOpen={openCharacter} />)}
        {page('worlds', <Worlds call={call} active={view === 'worlds'} />)}
        {page('server', <ServerView call={call} token={session.token} world={world} active={view === 'server'} isAdmin={allowed('manageServer')} />)}
      </div>
      <NewAccountDialog open={creating} call={call} onClose={() => setCreating(false)} />
    </div>
  );
}
