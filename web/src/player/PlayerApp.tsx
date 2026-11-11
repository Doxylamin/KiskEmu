// The player site: sign-in and registration, then the account pages (overview, characters, security, support) at hash
// addresses (#/security), so a refresh keeps the page. The session survives a refresh (shared/session.ts).
import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react';
import { useWorldNames } from '../shared/worlds';
import { QrCode } from '../shared/QrCode';
import { pageTitle, siteIcon, useSiteName } from '../shared/site';
import { api, ApiError } from '../shared/api';
import { Badge, Button, Status, type StatusMessage } from '../shared/components';
import { readSession, writeSession } from '../shared/session';
import { hashOf, useRoute } from '../shared/router';
import { CharacterPage } from './CharacterPage';
import { EmptyState, PageHeader, Section, SideNav, StatTile, formatDate, formatNumber } from '../shared/layout';

const STORE_KEY = 'aion-account-session';

/** GET /api/characters: the account's characters (the realm's character index). */
interface Character { characterId: string; name: string; level: number; worldId: number }
interface Me { accountId: string; username?: string; createdAt?: string; totpEnabled?: boolean; quna?: number | null; showPlaying?: boolean }
/** GET /api/characters/current: the character the account plays right now (by its session, not its address). */
interface Playing { inGame: boolean; showPlaying: boolean; worldId?: number; character?: { name: string; level: number; className: string | null } }
interface Status { accountsConfigured: boolean; registrationOpen?: boolean; launcherAvailable?: boolean }
interface MyTicket { id: number; createdAt: string; category: string; content: string; status: string; staffNote: string; handledAt: string | null }
interface Session { token: string; username: string }

const TICKET_KIND: Record<string, string> = { bug: 'Bug', player: 'Report a player', stuck: 'I am stuck', other: 'Other' };
type Page = 'overview' | 'characters' | 'security' | 'support';
const PAGES: Page[] = ['overview', 'characters', 'security', 'support'];

function describe(error: unknown, signingIn = false): string {
  if (!(error instanceof ApiError)) return 'Something went wrong.';
  if (error.status === 0) return 'The server could not be reached.';
  if (error.status === 401) return signingIn ? 'Sign-in failed. Check your username and password.' : 'That did not work. Check what you entered.';
  if (error.status === 409) return 'That username is unavailable.';
  if (error.status === 429) return 'Too many attempts. Please wait a minute.';
  return 'The server could not complete this request.';
}

export function PlayerApp() {
  const [session, setSession] = useState<Session | null>(null);
  const [restoring, setRestoring] = useState(() => readSession(STORE_KEY) !== null);
  const [status, setStatus] = useState<Status | null>(null);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const site = useSiteName();
  useEffect(() => { if (!session) document.title = pageTitle('Sign in', site); }, [session, site]);

  useEffect(() => {
    api<Status>('/api/status').then(setStatus).catch(error => setMessage({ text: describe(error), kind: 'error' }));
  }, []);

  // A saved session from before the refresh: still valid? (a 401 forgets it; an unreachable server keeps it)
  useEffect(() => {
    const saved = readSession(STORE_KEY);
    if (!saved) return;
    api<Me>('/api/accounts/me', { token: saved.token })
      .then(() => setSession(saved))
      .catch(error => {
        if (error instanceof ApiError && error.status === 401) writeSession(STORE_KEY, null);
        else setMessage({ text: describe(error), kind: 'error' });
      })
      .finally(() => setRestoring(false));
  }, []);

  const signedOut = useCallback((text: string, kind: StatusMessage['kind'] = '') => {
    writeSession(STORE_KEY, null);
    setSession(null);
    setMessage({ text, kind });
    window.history.replaceState(null, '', window.location.pathname);
  }, []);

  const signedIn = (token: string, username: string, remember: boolean) => {
    writeSession(STORE_KEY, { token, username }, remember);
    setSession({ token, username });
    setMessage(null);
  };

  return (
    <div className="site">
      <header className="site-header">
        <a className="site-brand" href="#/"><img className="site-mark" src={siteIcon} alt="" /><span>{site}</span></a>
        {session
          ? <div className="row"><span className="muted hide-narrow">Signed in as <strong className="ink">{session.username}</strong></span>
              <Button onClick={async () => {
                await api('/api/accounts/logout', { method: 'POST', token: session.token }).catch(() => {});
                signedOut('Signed out.');
              }}>Sign out</Button></div>
          : null}
      </header>
      {restoring ? <main className="site-main"><p className="muted">Signing in…</p></main>
        : session ? <Account session={session} status={status} onSignedOut={signedOut} />
        : <SignInPage status={status} message={message} onMessage={setMessage} onSignedIn={signedIn} />}
    </div>
  );
}

/* ---------- Signed out ---------- */

function SignInPage({ status, message, onMessage, onSignedIn }:
  { status: Status | null; message: StatusMessage | null; onMessage: (m: StatusMessage | null) => void; onSignedIn: (token: string, name: string, remember: boolean) => void }) {
  const [challenge, setChallenge] = useState<{ token: string; username: string; remember: boolean } | null>(null);
  const configured = status?.accountsConfigured ?? true;
  const registrationOpen = Boolean(status?.registrationOpen);

  async function signIn(name: string, password: string, register: boolean, remember: boolean) {
    if (register) await api('/api/accounts/register', { method: 'POST', body: { username: name, password } });
    const reply = await api<{ token?: string; requiresTotp?: boolean; challengeToken?: string }>(
      '/api/accounts/login', { method: 'POST', body: { username: name, password, remember } });
    if (reply.requiresTotp && reply.challengeToken) { setChallenge({ token: reply.challengeToken, username: name, remember }); onMessage(null); return; }
    if (!reply.token) throw new ApiError(401, 'Sign-in failed');
    onSignedIn(reply.token, name, remember);
  }

  async function completeTotp(code: string, recoveryCode?: string) {
    if (!challenge) return;
    const reply = await api<{ token: string }>('/api/accounts/login/totp', {
      method: 'POST', body: { challengeToken: challenge.token, code: code || null, recoveryCode: recoveryCode || null },
    });
    onSignedIn(reply.token, challenge.username, challenge.remember);
  }

  return (
    <main className="site-main signin-layout">
      <section className="signin-intro">
        <p className="eyebrow">Account</p>
        <h1>{challenge ? 'Two-step verification' : 'Sign in to your account'}</h1>
        <p className="lead">Manage your characters, your password and two-step verification, and get in touch with the team.</p>
        {!registrationOpen && <p className="muted">New accounts are created by the team.</p>}
      </section>
      <section className="card signin-panel" aria-label="Sign in">
        {!configured
          ? <p className="status status-error">The account service is not configured yet.</p>
          : challenge
            ? <TotpStep onSubmit={completeTotp} onCancel={() => { setChallenge(null); onMessage(null); }}
                onError={error => onMessage({ text: describe(error, true), kind: 'error' })} />
            : <SignInForm registrationOpen={registrationOpen} onSubmit={signIn} onError={error => onMessage({ text: describe(error, true), kind: 'error' })} />}
        <Status message={message} />
      </section>
    </main>
  );
}

function SignInForm({ registrationOpen, onSubmit, onError }:
  { registrationOpen: boolean; onSubmit: (name: string, password: string, register: boolean, remember: boolean) => Promise<void>; onError: (error: unknown) => void }) {
  const [mode, setMode] = useState<'signin' | 'register'>('signin');
  const [busy, setBusy] = useState(false);
  const register = registrationOpen && mode === 'register';
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    setBusy(true);
    try { await onSubmit(String(data.get('username')), String(data.get('password')), register, data.get('remember') === 'on'); }
    catch (error) { onError(error); }
    finally {
      setBusy(false);
      const password = form.elements.namedItem('password');
      if (password instanceof HTMLInputElement) password.value = '';
    }
  }
  return (
    <form className="stack form-lg" onSubmit={submit}>
      {registrationOpen && (
        <div className="segmented" role="tablist" aria-label="Sign in or create an account">
          <button type="button" role="tab" aria-selected={!register} onClick={() => setMode('signin')}>Sign in</button>
          <button type="button" role="tab" aria-selected={register} onClick={() => setMode('register')}>Create account</button>
        </div>
      )}
      <div className="field">
        <label htmlFor="username">Username</label>
        <input id="username" name="username" className="input" autoComplete="username" required maxLength={32}
          {...(register ? { minLength: 3, pattern: '[A-Za-z0-9_]+' } : {})} />
        {register && <small>3–32 letters, numbers or underscores.</small>}
      </div>
      <div className="field">
        <label htmlFor="password">Password</label>
        <input id="password" name="password" type="password" className="input" autoComplete={register ? 'new-password' : 'current-password'} required maxLength={256}
          {...(register ? { minLength: 12 } : {})} />
        {register ? <small>At least 12 characters.</small> : <small>Forgot it? The team can give you a temporary one.</small>}
      </div>
      <label className="check">
        <input type="checkbox" name="remember" />
        <span>Keep me signed in for 30 days</span>
      </label>
      <Button type="submit" variant="primary" block busy={busy}>{register ? 'Create account' : 'Sign in'}</Button>
    </form>
  );
}

function TotpStep({ onSubmit, onCancel, onError }: {
  onSubmit: (code: string, recovery?: string) => Promise<void>; onCancel: () => void; onError: (error: unknown) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [useRecovery, setUseRecovery] = useState(false);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const data = new FormData(event.currentTarget);
    setBusy(true);
    try { await onSubmit(String(data.get('code') ?? ''), useRecovery ? String(data.get('recovery') ?? '') : undefined); }
    catch (error) { onError(error); }
    finally { setBusy(false); }
  }
  return (
    <form className="stack form-lg" onSubmit={submit}>
      <p className="muted">Open your authenticator app and enter the 6-digit code.</p>
      {!useRecovery
        ? <div className="field">
            <label htmlFor="totp-code">Code</label>
            <input id="totp-code" name="code" className="input input-code" inputMode="numeric" pattern="[0-9]{6}" required maxLength={6} autoComplete="one-time-code" autoFocus />
          </div>
        : <div className="field">
            <label htmlFor="recovery">Recovery code</label>
            <input id="recovery" name="recovery" className="input" required minLength={8} maxLength={32} autoComplete="off" autoFocus />
          </div>}
      <Button type="submit" variant="primary" block busy={busy}>Sign in</Button>
      <div className="row-between small">
        <button type="button" className="link" onClick={() => setUseRecovery(r => !r)}>{useRecovery ? 'Use the authenticator' : 'Use a recovery code'}</button>
        <button type="button" className="link muted" onClick={onCancel}>Back</button>
      </div>
    </form>
  );
}

/* ---------- Signed in ---------- */

function Account({ session, status, onSignedOut }:
  { session: Session; status: Status | null; onSignedOut: (text: string, kind?: StatusMessage['kind']) => void }) {
  const [route, navigate] = useRoute();
  const page: Page = PAGES.includes(route[0] as Page) ? route[0] as Page : 'overview';
  const [me, setMe] = useState<Me | null>(null);
  const [characters, setCharacters] = useState<Character[] | null>(null);
  const [tickets, setTickets] = useState<MyTicket[]>([]);
  const [playing, setPlaying] = useState<Playing | null>(null);
  const { token } = session;

  const loadMe = useCallback(() => api<Me>('/api/accounts/me', { token }).then(setMe).catch(error => {
    if (error instanceof ApiError && error.status === 401) onSignedOut('Your session ended. Sign in again.');
  }), [token, onSignedOut]);
  const loadTickets = useCallback(() => api<MyTicket[]>('/api/tickets', { token }).then(setTickets).catch(() => setTickets([])), [token]);
  useEffect(() => {
    loadMe();
    loadTickets();
    api<Character[]>('/api/characters', { token }).then(setCharacters).catch(() => setCharacters([]));
  }, [token, loadMe, loadTickets]);
  // "In game" next to the character being played: asked again every minute (a world reports about that often).
  useEffect(() => {
    let live = true;
    const load = () => api<Playing>('/api/characters/current', { token }).then(p => { if (live) setPlaying(p); }).catch(() => { if (live) setPlaying(null); });
    load();
    const timer = window.setInterval(load, 60_000);
    return () => { live = false; window.clearInterval(timer); };
  }, [token]);
  useEffect(() => { if (route[0] && !PAGES.includes(route[0] as Page)) navigate([], { replace: true }); }, [route, navigate]);
  const site = useSiteName();
  // #/characters/<id>: one character's page (it sets its own title).
  const characterId = page === 'characters' ? route[1] : undefined;
  useEffect(() => {
    if (characterId) return;
    document.title = pageTitle({ overview: 'Your account', characters: 'Characters', security: 'Security', support: 'Support' }[page], site);
  }, [page, site, characterId]);

  const answered = tickets.filter(t => t.status !== 'open' && t.staffNote).length;
  const groups = [{ items: [
    { id: 'overview', label: 'Overview' },
    { id: 'characters', label: 'Characters' },
    { id: 'security', label: 'Security' },
    { id: 'support', label: 'Support', badge: answered > 0 ? <span className="badge badge-warn">{answered} {answered === 1 ? 'reply' : 'replies'}</span> : undefined },
  ] }];

  return (
    <div className="account-layout">
      <SideNav groups={groups} current={page} label="Account" />
      <main className="account-main">
        {page === 'overview' && <Overview me={me} username={session.username} characters={characters} playing={playing} tickets={tickets} token={token} launcher={Boolean(status?.launcherAvailable)} />}
        {page === 'characters' && (characterId
          ? <CharacterPage key={characterId} id={characterId} token={token} onSignedOut={onSignedOut} />
          : <Characters characters={characters} playing={playing} />)}
        {page === 'security' && <Security token={token} me={me} onChanged={loadMe} onSignedOut={onSignedOut} />}
        {page === 'support' && <Support token={token} characters={characters ?? []} tickets={tickets} onSent={loadTickets} />}
      </main>
    </div>
  );
}

function Overview({ me, username, characters, playing, tickets, token, launcher }:
  { me: Me | null; username: string; characters: Character[] | null; playing: Playing | null; tickets: MyTicket[]; token: string; launcher: boolean }) {
  const worlds = new Set((characters ?? []).map(c => c.worldId)).size;
  const answered = tickets.find(t => t.status !== 'open' && t.staffNote);
  return (
    <>
      <PageHeader eyebrow="Account" title={me?.username ?? username} sub={me?.createdAt ? `Member since ${formatDate(me.createdAt)}` : undefined}
        actions={launcher && <LauncherDownload token={token} />} />
      <div className="stat-grid">
        <StatTile label="Quna balance" value={me?.quna === undefined || me?.quna === null ? '—' : formatNumber(me.quna)} note="Spend it in the in-game shop." />
        <div className="card stat-tile">
          <p className="stat-tile-label">Two-step verification</p>
          <p className={'stat-tile-flag ' + (me?.totpEnabled ? 'tone-ok' : 'tone-warn')}>{me === null ? '—' : me.totpEnabled ? 'On' : 'Off'}</p>
          <a className="small" href="#/security">{me?.totpEnabled ? 'Manage in Security' : 'Turn it on in Security'}</a>
        </div>
        <StatTile label="Characters" value={characters === null ? '—' : characters.length} note={characters?.length ? `On ${worlds} ${worlds === 1 ? 'world' : 'worlds'}` : 'Create one in the game.'} />
      </div>
      <CharacterTable characters={characters} playing={playing} />
      <Section title="Support" actions={<a className="btn btn-secondary" href="#/support">Open support</a>}>
        <p className="muted">{answered ? `The team answered your ticket #${answered.id}.` : 'Questions, bug reports and player reports go to the team from here.'}</p>
      </Section>
    </>
  );
}

function Characters({ characters, playing }: { characters: Character[] | null; playing: Playing | null }) {
  return (
    <>
      <PageHeader eyebrow="Account" title="Characters" sub="New characters are made in the game. Open one for its details and where it is." />
      <CharacterTable characters={characters} playing={playing} />
    </>
  );
}

function CharacterTable({ characters, playing }: { characters: Character[] | null; playing: Playing | null }) {
  const inGame = (c: Character) => Boolean(playing?.inGame && playing.worldId === c.worldId && playing.character?.name === c.name);
  const worldName = useWorldNames();
  const byWorld = new Map<number, Character[]>();
  for (const c of characters ?? []) byWorld.set(c.worldId, [...(byWorld.get(c.worldId) ?? []), c]);
  return (
    <Section title="Characters" flush>
      {characters === null ? <p className="muted section-pad">Loading…</p>
        : characters.length === 0 ? <EmptyState title="No characters yet">Create one in the game.</EmptyState>
        : <div className="table-scroll">
            <table className="table">
              <thead><tr><th scope="col">Name</th><th scope="col" className="num">Level</th></tr></thead>
              {[...byWorld.entries()].sort(([a], [b]) => a - b).map(([world, list]) => (
                <tbody key={world}>
                  <tr className="table-group"><th scope="rowgroup" colSpan={2}>{worldName(world)}</th></tr>
                  {list.map(c => (
                    <tr key={c.name}>
                      <td className="strong"><a className="character-link" href={hashOf('characters', c.characterId)}>{c.name}</a>{inGame(c) && <> <Badge tone="ok">In game</Badge></>}</td>
                      {/* The live level while playing (the list's is the last saved one). */}
                      <td className="num mono">{inGame(c) ? playing?.character?.level ?? c.level : c.level}</td>
                    </tr>
                  ))}
                </tbody>
              ))}
            </table>
          </div>}
    </Section>
  );
}

/** The launcher download: a short-lived link from the server, then the browser downloads it. */
function LauncherDownload({ token }: { token: string }) {
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const site = useSiteName();
  async function download() {
    setBusy(true); setFailed(false);
    try {
      // A ZIP of KiskLauncher with a launcher.json that adds this server (the exe alone when the server cannot make one).
      const { url } = await api<{ url: string }>('/api/downloads/launcher?package=zip', { method: 'POST', token });
      const zip = url.startsWith('/download/launcher.zip');
      const link = document.createElement('a');
      link.href = url;
      link.download = `${site.replace(/[\\/:*?"<>|]/g, '')} KiskLauncher.${zip ? 'zip' : 'exe'}`;
      link.click();
    } catch { setFailed(true); }
    finally { setBusy(false); }
  }
  return (
    <div className="stack-sm launcher">
      <Button variant="primary" busy={busy} onClick={download}><DownloadIcon /> Download launcher</Button>
      {/* KiskLauncher already installed (for this or another server): the link adds this server; the player still confirms it there. */}
      <a className="small" href={`kisklauncher://add?url=${encodeURIComponent(window.location.origin)}`}>Already have KiskLauncher? Add this server</a>
      {failed && <span className="status status-error">The download could not start. Try again.</span>}
    </div>
  );
}

function DownloadIcon() {
  return <svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><path d="M12 3v12" /><path d="m7 10 5 5 5-5" /><path d="M5 21h14" /></svg>;
}

/* ---------- Security ---------- */

function Security({ token, me, onChanged, onSignedOut }:
  { token: string; me: Me | null; onChanged: () => void; onSignedOut: (text: string, kind?: StatusMessage['kind']) => void }) {
  return (
    <>
      <PageHeader eyebrow="Account" title="Security" />
      <TwoStep token={token} onChanged={onChanged} />
      <ShowPlaying token={token} me={me} onChanged={onChanged} />
      <ChangePassword token={token} onSignedOut={onSignedOut} />
      <SignOutEverywhere token={token} onSignedOut={onSignedOut} />
    </>
  );
}

function TwoStep({ token, onChanged }: { token: string; onChanged: () => void }) {
  const [state, setState] = useState<{ enabled: boolean; recoveryCodesRemaining: number } | null>(null);
  const [enroll, setEnroll] = useState<{ secret: string; otpAuthUri: string } | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null);
  const [form, setForm] = useState<'regenerate' | 'disable' | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const load = useCallback(() => api<{ enabled: boolean; recoveryCodesRemaining: number }>('/api/accounts/totp/status', { token })
    .then(setState).catch(() => setState({ enabled: false, recoveryCodesRemaining: 0 })), [token]);
  useEffect(() => { load(); }, [load]);

  async function run(action: () => Promise<void>) {
    setBusy(true); setMessage(null);
    try { await action(); } catch (error) { setMessage({ text: describe(error), kind: 'error' }); } finally { setBusy(false); }
  }
  const field = (event: FormEvent<HTMLFormElement>, name: string) => String(new FormData(event.currentTarget).get(name) ?? '');

  return (
    <Section title="Two-step verification" actions={state && (state.enabled
      ? <span className="badge badge-ok">On</span> : <span className="badge badge-warn">Off</span>)}>
      <p className="muted">{state?.enabled ? 'Sign-ins ask for a code from your authenticator app.'
        : 'Protect your account with a code from an authenticator app at every sign-in. The game shows your account security as not set up until you turn it on.'}</p>
      {codes && (
        <div className="notice">
          <p><strong>Save these recovery codes now.</strong> They are shown once. Each one signs you in once without your phone.</p>
          <ul className="code-list">{codes.map(c => <li key={c}><code>{c}</code></li>)}</ul>
          <div><Button size="sm" onClick={() => setCodes(null)}>I saved them</Button></div>
        </div>
      )}
      {state && !state.enabled && !enroll && (
        <div><Button variant="primary" busy={busy} onClick={() => run(async () => {
          setEnroll(await api<{ secret: string; otpAuthUri: string }>('/api/accounts/totp/enroll', { method: 'POST', token }));
        })}>Set up authenticator</Button></div>
      )}
      {enroll && (
        <form className="stack" onSubmit={event => { event.preventDefault(); const code = field(event, 'code'); run(async () => {
          const r = await api<{ recoveryCodes: string[] }>('/api/accounts/totp/confirm', { method: 'POST', token, body: { code } });
          setEnroll(null); setCodes(r.recoveryCodes); await load(); onChanged();
        }); }}>
          <ol className="steps">
            <li>Scan this code with your authenticator app:
              <QrCode value={enroll.otpAuthUri} label="QR code for your authenticator app" />
              <span className="block muted small">Can't scan it? Enter this key instead: <code className="break">{enroll.secret}</code></span></li>
            <li>Enter the 6-digit code it shows.</li>
          </ol>
          <div className="row wrap align-end">
            <div className="field"><label htmlFor="enroll-code">Code</label>
              <input id="enroll-code" name="code" className="input input-code" inputMode="numeric" pattern="[0-9]{6}" required maxLength={6} autoComplete="one-time-code" /></div>
            <Button type="submit" variant="primary" busy={busy}>Turn on</Button>
            <Button variant="ghost" onClick={() => setEnroll(null)}>Cancel</Button>
          </div>
        </form>
      )}
      {state?.enabled && (
        <div className="split-row">
          <div><p className="strong">Recovery codes</p><p className="muted small">{state.recoveryCodesRemaining} left.</p></div>
          <div className="row wrap">
            <Button onClick={() => setForm(form === 'regenerate' ? null : 'regenerate')}>New recovery codes</Button>
            <Button variant="danger" onClick={() => setForm(form === 'disable' ? null : 'disable')}>Turn off</Button>
          </div>
        </div>
      )}
      {form === 'regenerate' && (
        <form className="row wrap align-end" onSubmit={event => { event.preventDefault(); const password = field(event, 'password'); run(async () => {
          const r = await api<{ recoveryCodes: string[] }>('/api/accounts/totp/recovery/regenerate', { method: 'POST', token, body: { password } });
          setCodes(r.recoveryCodes); setForm(null); await load();
        }); }}>
          <div className="field grow"><label htmlFor="regen-password">Your password</label>
            <input id="regen-password" name="password" type="password" className="input" required autoComplete="current-password" maxLength={256} /></div>
          <Button type="submit" variant="primary" busy={busy}>Make new codes</Button>
        </form>
      )}
      {form === 'disable' && (
        <form className="stack" onSubmit={event => { event.preventDefault(); const data = new FormData(event.currentTarget); run(async () => {
          await api('/api/accounts/totp/disable', { method: 'POST', token, body: { password: String(data.get('password')), code: String(data.get('code') ?? '') || null, recoveryCode: null } });
          setForm(null); await load(); onChanged();
          setMessage({ text: 'Two-step verification is off.', kind: 'ok' });
        }); }}>
          <div className="form-grid">
            <div className="field"><label htmlFor="off-password">Your password</label>
              <input id="off-password" name="password" type="password" className="input" required autoComplete="current-password" maxLength={256} /></div>
            <div className="field"><label htmlFor="off-code">Authenticator code</label>
              <input id="off-code" name="code" className="input input-code" inputMode="numeric" pattern="[0-9]{6}" required maxLength={6} autoComplete="one-time-code" /></div>
          </div>
          <div><Button type="submit" variant="danger" busy={busy}>Turn off two-step verification</Button></div>
        </form>
      )}
      <Status message={message} />
    </Section>
  );
}

function ChangePassword({ token, onSignedOut }: { token: string; onSignedOut: (text: string, kind?: StatusMessage['kind']) => void }) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    setBusy(true); setMessage(null);
    try {
      await api('/api/accounts/password', { method: 'POST', token, body: { currentPassword: data.get('current'), newPassword: data.get('next') } });
      onSignedOut('Password changed. Sign in with your new password.', 'ok');
    } catch (error) { setMessage({ text: describe(error), kind: 'error' }); form.reset(); }
    finally { setBusy(false); }
  }
  return (
    <Section title="Change password">
      <form className="stack" onSubmit={submit}>
        <div className="form-grid">
          <div className="field"><label htmlFor="current">Current password</label>
            <input id="current" name="current" type="password" className="input" autoComplete="current-password" required maxLength={256} /></div>
          <div className="field"><label htmlFor="next">New password</label>
            <input id="next" name="next" type="password" className="input" autoComplete="new-password" required minLength={12} maxLength={256} />
            <small>At least 12 characters.</small></div>
        </div>
        <div className="split-row">
          <span className="muted small">Every device is signed out when it changes.</span>
          <Button type="submit" variant="primary" busy={busy}>Change password</Button>
        </div>
        <Status message={message} />
      </form>
    </Section>
  );
}

/** The show-playing opt-out: whether others see the character this account plays (KiskLauncher's Discord status names it). */
function ShowPlaying({ token, me, onChanged }: { token: string; me: Me | null; onChanged: () => void }) {
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  if (me?.showPlaying === undefined) return null;
  async function change(show: boolean) {
    setBusy(true); setMessage(null);
    try {
      await api('/api/accounts/me/preferences', { method: 'PUT', token, body: { showPlaying: show } });
      onChanged();
      setMessage({ text: show ? 'Others can see what you play.' : 'Others no longer see what you play.', kind: 'ok' });
    } catch { setMessage({ text: 'The setting was not saved. Try again.', kind: 'error' }); }
    finally { setBusy(false); }
  }
  return (
    <Section>
      <div className="stack-sm">
        <h2 className="h-section">Show what I play</h2>
        <p className="muted">While you are in the game, KiskLauncher's Discord status shows your character's name, class and level. Turn this off to show only the server. Your own account pages always show it to you.</p>
        <label className="check">
          <input type="checkbox" checked={me.showPlaying} disabled={busy} onChange={e => change(e.target.checked)} />
          <span>Show my character when I'm in the game</span>
        </label>
        <Status message={message} />
      </div>
    </Section>
  );
}

function SignOutEverywhere({ token, onSignedOut }: { token: string; onSignedOut: (text: string, kind?: StatusMessage['kind']) => void }) {
  const [busy, setBusy] = useState(false);
  return (
    <Section>
      <div className="split-row">
        <div><h2 className="h-section">Signed-in devices</h2><p className="muted">Sign out on every computer and browser, this one included.</p></div>
        <Button busy={busy} onClick={async () => {
          setBusy(true);
          try { await api('/api/accounts/revoke-sessions', { method: 'POST', token }); onSignedOut('Signed out on every device.', 'ok'); }
          catch { setBusy(false); }
        }}>Sign out everywhere</Button>
      </div>
    </Section>
  );
}

/* ---------- Support ---------- */

function Support({ token, characters, tickets, onSent }: { token: string; characters: Character[]; tickets: MyTicket[]; onSent: () => void }) {
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [busy, setBusy] = useState(false);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const data = new FormData(form);
    setBusy(true);
    try {
      const r = await api<{ id: number }>('/api/tickets', { method: 'POST', token, body: { category: data.get('category'), character: data.get('character') || null, content: data.get('content') } });
      setMessage({ text: `Sent as #${r.id}. The team answers here.`, kind: 'ok' });
      form.reset();
      onSent();
    } catch (error) {
      const code = error instanceof ApiError ? error.code : undefined;
      setMessage({ text: code === 'too_many_tickets' ? 'You have 10 open tickets already. Please wait for an answer.' : code === 'invalid_ticket' ? 'Write between 3 and 1000 characters.' : describe(error), kind: 'error' });
    } finally { setBusy(false); }
  }
  return (
    <>
      <PageHeader eyebrow="Account" title="Support" sub="Questions, bug reports and player reports. In the game, //report <text> works too." />
      <Section title="Write to the team">
        <form className="stack" onSubmit={submit}>
          <div className="form-grid">
            <div className="field"><label htmlFor="ticket-kind">Kind</label>
              <select id="ticket-kind" name="category" className="select" defaultValue="bug">
                {Object.entries(TICKET_KIND).map(([id, label]) => <option key={id} value={id}>{label}</option>)}
              </select></div>
            {characters.length > 0 && (
              <div className="field"><label htmlFor="ticket-character">Character</label>
                <select id="ticket-character" name="character" className="select" defaultValue={characters[0].name}>
                  {characters.map(c => <option key={`${c.worldId}:${c.name}`} value={c.name}>{c.name}</option>)}
                </select></div>
            )}
          </div>
          <div className="field"><label htmlFor="ticket-text">What happened?</label>
            <textarea id="ticket-text" name="content" className="input textarea" rows={4} required minLength={3} maxLength={1000} placeholder="For a player report, include their name." /></div>
          <div><Button type="submit" variant="primary" busy={busy}>Send</Button></div>
          <Status message={message} />
        </form>
      </Section>
      <Section title="Your tickets" flush>
        {tickets.length === 0 ? <EmptyState title="No tickets yet" />
          : <ul className="ticket-list-player">
              {tickets.map(t => (
                <li key={t.id}>
                  <div className="row-between wrap">
                    <strong>#{t.id} · {TICKET_KIND[t.category] ?? t.category}</strong>
                    <span className={'badge ' + (t.status === 'open' ? '' : 'badge-ok')}>{t.status === 'open' ? 'Waiting' : 'Answered'}</span>
                  </div>
                  <p className="muted small">{formatDate(t.createdAt, true)}</p>
                  <p className="pre">{t.content}</p>
                  {t.staffNote && <Reply>{t.staffNote}</Reply>}
                </li>
              ))}
            </ul>}
      </Section>
    </>
  );
}

function Reply({ children }: { children: ReactNode }) {
  return <div className="reply"><p className="small strong">Team</p><p className="pre">{children}</p></div>;
}
