// Accounts (realm): search, an account's page with its characters on every world, sanctions and tickets, and the account
// tools (end sessions, temporary password, disable, two-step reset, Quna, staff role). The Staff page is the same list
// filtered to staff. Server: Authentication/StaffAccountEndpoints.cs and GameplayEndpoints.cs.
import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { ApiError, requestId } from '../shared/api';
import { Badge, Button, ConfirmDialog, Status, Tabs, type ConfirmRequest, type StatusMessage } from '../shared/components';
import { EmptyState, Facts, PageHeader, Section, formatDate, formatNumber } from '../shared/layout';
import { hashOf } from '../shared/router';
import { useWorldNames } from '../shared/worlds';
import { describe, type Call } from './StaffApp';
import { ROLE_LABEL, STAFF_ROLES, can, type StaffProfile } from './types';

export interface AccountSummary {
  accountId: string; username: string; createdAt: string; disabledAt: string | null; lastSignInAt: string | null;
  roles: string[]; rankLabel: string | null;
}
interface AccountPage { accounts: AccountSummary[]; total: number; page: number; pageSize: number }
interface AccountDetail {
  account: AccountSummary; totpEnabled: boolean; sessions: number; staffSessions: number; quna: number | null;
  characters: { characterId: string; worldId: number; name: string; level: number; race: number; lastLoginAt: string | null }[];
  sanctions: { id: number; kind: string; reason: string; createdAt: string; expiresAt: string | null; createdBy: string | null; revokedAt: string | null; active: boolean }[];
  tickets: { id: number; createdAt: string; category: string; content: string; status: string; staffNote: string; handledAt: string | null }[];
}
type Filter = 'all' | 'staff' | 'disabled';

/** The account list. `staffOnly`: the Staff page (accounts with a staff role). */
export function AccountList({ call, active, staffOnly = false }: { call: Call; active: boolean; staffOnly?: boolean }) {
  const [query, setQuery] = useState('');
  const [submitted, setSubmitted] = useState('');
  const [filter, setFilter] = useState<Filter>(staffOnly ? 'staff' : 'all');
  const [page, setPage] = useState(0);
  const [data, setData] = useState<AccountPage | null>(null);
  const [message, setMessage] = useState<StatusMessage | null>(null);

  useEffect(() => {
    if (!active) return;
    let live = true;
    const params = new URLSearchParams({ filter: staffOnly ? 'staff' : filter, page: String(page) });
    if (submitted) params.set('q', submitted);
    call<AccountPage>('/api/admin/accounts?' + params)
      .then(result => { if (live) { setData(result); setMessage(null); } })
      .catch(error => { if (live) setMessage({ text: describe(error), kind: 'error' }); });
    return () => { live = false; };
  }, [call, active, submitted, filter, page, staffOnly]);

  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  return (
    <div className="page">
      <PageHeader title={staffOnly ? 'Staff' : 'Accounts'}
        sub={staffOnly ? 'Accounts with a staff rank. Change a rank on the account’s page.' : 'Search by account or character name.'} />
      <form className="row wrap" onSubmit={e => { e.preventDefault(); setPage(0); setSubmitted(query.trim()); }}>
        <input className="input search-input" type="search" placeholder="Account or character name" aria-label="Search accounts" maxLength={64}
          value={query} onChange={e => setQuery(e.target.value)} />
        <Button type="submit">Search</Button>
        {!staffOnly && (
          <div className="segmented-sm" role="group" aria-label="Show">
            {(['all', 'staff', 'disabled'] as Filter[]).map(f => (
              <button key={f} type="button" aria-pressed={filter === f} onClick={() => { setFilter(f); setPage(0); }}>
                {f === 'all' ? 'All' : f === 'staff' ? 'Staff' : 'Disabled'}
              </button>
            ))}
          </div>
        )}
      </form>
      <Status message={message} />
      <Section flush title={data ? `${formatNumber(data.total)} ${data.total === 1 ? 'account' : 'accounts'}` : 'Accounts'}
        actions={pages > 1 && (
          <div className="row">
            <Button size="sm" disabled={page === 0} onClick={() => setPage(p => p - 1)}>Previous</Button>
            <span className="small muted">Page {page + 1} of {pages}</span>
            <Button size="sm" disabled={page + 1 >= pages} onClick={() => setPage(p => p + 1)}>Next</Button>
          </div>
        )}>
        {data && data.accounts.length === 0 ? <EmptyState title="No accounts match" />
          : <div className="table-wrap">
              <table className="data-table">
                <thead><tr><th scope="col">Account</th><th scope="col">Rank</th><th scope="col">Status</th><th scope="col">Created</th><th scope="col">Latest session</th></tr></thead>
                <tbody>
                  {data?.accounts.map(a => (
                    <tr key={a.accountId}>
                      <td data-label="Account"><a className="strong-link" href={hashOf('accounts', a.accountId)}>{a.username}</a></td>
                      <td data-label="Rank">{a.rankLabel ?? <span className="muted">Player</span>}</td>
                      <td data-label="Status">{a.disabledAt ? <span className="pill pill-danger">Disabled</span> : <span className="pill pill-ok">Active</span>}</td>
                      <td data-label="Created" className="nowrap">{formatDate(a.createdAt)}</td>
                      <td data-label="Latest session" className="nowrap muted">{formatDate(a.lastSignInAt, true)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>}
      </Section>
    </div>
  );
}

type Tab = 'overview' | 'characters' | 'sanctions' | 'tickets';

export function AccountView({ call, profile, accountId, onOpenCharacter }:
  { call: Call; profile: StaffProfile; accountId: string; onOpenCharacter: (characterId: string, worldId: number) => void }) {
  const [detail, setDetail] = useState<AccountDetail | null>(null);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [tab, setTab] = useState<Tab>('overview');
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const [password, setPassword] = useState<string | null>(null);
  const [roleOpen, setRoleOpen] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const worldName = useWorldNames();

  const load = useCallback(() => call<AccountDetail>(`/api/admin/accounts/${accountId}`)
    .then(d => { setDetail(d); })
    .catch(error => setMessage({ text: error instanceof ApiError && error.status === 404 ? 'There is no such account.' : describe(error), kind: 'error' })), [call, accountId]);
  useEffect(() => { setDetail(null); setTab('overview'); setPassword(null); load(); }, [load]);

  async function act(name: string, action: () => Promise<void>, done: string) {
    setBusy(name); setMessage(null);
    try { await action(); setMessage({ text: done, kind: 'ok' }); await load(); }
    catch (error) {
      setMessage({ text: error instanceof ApiError && error.status === 403 ? 'Your staff rank does not allow this for this account.' : describe(error), kind: 'error' });
    } finally { setBusy(null); }
  }

  if (!detail) return <div className="page"><p className="breadcrumb"><a href="#/accounts">Accounts</a></p><Status message={message ?? { text: 'Loading…', kind: '' }} /></div>;
  const a = detail.account;
  const staffTarget = a.roles.length > 0;
  const canModerate = can(profile, 'disableAccounts') && (!staffTarget || can(profile, 'manageStaff'));
  const canManage = can(profile, 'manageAccounts');
  const activeSanctions = detail.sanctions.filter(s => s.active);
  const openTickets = detail.tickets.filter(t => t.status === 'open').length;

  return (
    <div className="page">
      <nav aria-label="Breadcrumb" className="breadcrumb"><a href={staffTarget ? '#/staff' : '#/accounts'}>{staffTarget ? 'Staff' : 'Accounts'}</a> <span aria-hidden="true">/</span> <span>{a.username}</span></nav>
      <PageHeader title={a.username} actions={!a.disabledAt && (
        <>
          {canModerate && <Button busy={busy === 'revoke'} onClick={() => setConfirm({
            title: 'End every session', body: `Sign ${a.username} out on every device, the game launcher included?`, confirmLabel: 'End sessions',
            onConfirm: () => act('revoke', () => call(`/api/admin/accounts/${a.accountId}/sessions/revoke`, { method: 'POST' }), 'Every session of this account has ended.'),
          })}>End sessions</Button>}
          {canManage && <Button busy={busy === 'password'} onClick={() => setConfirm({
            title: 'Temporary password', body: `Give ${a.username} a new random password? Their current password stops working and they are signed out everywhere.`,
            note: 'You see the new password once. Pass it on privately; they can change it on the account page.', confirmLabel: 'Make a password',
            onConfirm: () => act('password', async () => {
              const r = await call<{ temporaryPassword: string }>(`/api/admin/accounts/${a.accountId}/password`, { method: 'POST' });
              setPassword(r.temporaryPassword);
            }, 'The password was changed.'),
          })}>Temporary password</Button>}
          {canModerate && <Button variant="danger" busy={busy === 'disable'} onClick={() => setConfirm({
            title: 'Disable account', danger: true, body: `Disable ${a.username}? They are signed out and cannot sign in or play.`, confirmLabel: 'Disable account',
            onConfirm: () => act('disable', () => call(`/api/admin/accounts/${a.accountId}/disable`, { method: 'POST' }), 'The account is disabled.'),
          })}>Disable account</Button>}
        </>
      )}>
        <div className="row wrap">
          {a.disabledAt ? <span className="pill pill-danger">Disabled {formatDate(a.disabledAt)}</span> : <span className="pill pill-ok">Active</span>}
          <span className="pill">{a.rankLabel ?? 'Player'}</span>
          <span className={'pill' + (detail.totpEnabled ? '' : ' pill-warn')}>{detail.totpEnabled ? 'Two-step on' : 'Two-step off'}</span>
          {activeSanctions.map(s => <span key={s.id} className="pill pill-warn">{sanctionLabel(s.kind)}{s.expiresAt ? ` until ${formatDate(s.expiresAt)}` : ''}</span>)}
        </div>
      </PageHeader>
      {password && (
        <div className="notice">
          <p><strong>Temporary password for {a.username}:</strong> <code className="select-all">{password}</code></p>
          <p className="small muted">Shown once. Pass it on privately.</p>
          <div><Button size="sm" onClick={() => setPassword(null)}>Done</Button></div>
        </div>
      )}
      <Status message={message} />
      <Tabs label="Account" value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: 'Overview' },
        { id: 'characters', label: `Characters · ${detail.characters.length}` },
        { id: 'sanctions', label: `Sanctions · ${detail.sanctions.length}` },
        { id: 'tickets', label: `Tickets · ${detail.tickets.length}` },
      ]} />
      {tab === 'overview' && (
        <>
          <Section title="Details">
            <Facts items={[
              { label: 'Created', value: formatDate(a.createdAt) },
              { label: 'Latest session', value: formatDate(a.lastSignInAt, true) },
              { label: 'Signed-in devices', value: String(detail.sessions) + (detail.staffSessions ? ` (+${detail.staffSessions} staff)` : '') },
              { label: 'Quna', value: detail.quna === null ? '—' : formatNumber(detail.quna) },
              { label: 'Open tickets', value: String(openTickets) },
              { label: 'Account id', value: <code className="small">{a.accountId}</code> },
              { label: 'Staff rank', value: <span className="row">{a.rankLabel ?? 'None'}{can(profile, 'manageStaff') && !a.disabledAt &&
                  <button type="button" className="link small" onClick={() => setRoleOpen(true)}>Change</button>}</span> },
              { label: 'Two-step verification', value: <span className="row">{detail.totpEnabled ? 'On' : 'Off'}{detail.totpEnabled && canManage &&
                  <button type="button" className="link small" onClick={() => setConfirm({
                    title: 'Reset two-step verification', danger: true, confirmLabel: 'Reset',
                    body: `Turn off two-step verification for ${a.username}? Use this when they lost their phone and their recovery codes.`,
                    note: staffTarget ? 'Staff set up a new authenticator at their next dashboard sign-in.' : undefined,
                    onConfirm: () => act('totp', () => call(`/api/admin/accounts/${a.accountId}/totp/reset`, { method: 'POST' }), 'Two-step verification is off for this account.'),
                  })}>Reset</button>}</span> },
            ]} />
          </Section>
          {can(profile, 'manageQuna') && !a.disabledAt && <QunaForm call={call} account={a} onDone={text => { setMessage({ text, kind: 'ok' }); load(); }} onError={e => setMessage({ text: describe(e), kind: 'error' })} />}
          <CharacterTable detail={detail} worldName={worldName} onOpen={onOpenCharacter} />
        </>
      )}
      {tab === 'characters' && <CharacterTable detail={detail} worldName={worldName} onOpen={onOpenCharacter} />}
      {tab === 'sanctions' && (
        <Section flush title="Sanctions" actions={<a className="small" href="#/moderation">Moderation</a>}>
          {detail.sanctions.length === 0 ? <EmptyState title="No sanctions" />
            : <div className="table-wrap"><table className="data-table">
                <thead><tr><th scope="col">Kind</th><th scope="col">Reason</th><th scope="col">From</th><th scope="col">Until</th><th scope="col">By</th></tr></thead>
                <tbody>{detail.sanctions.map(s => (
                  <tr key={s.id} className={s.active ? '' : 'row-dim'}>
                    <td data-label="Kind">{sanctionLabel(s.kind)}{!s.active && <span className="muted small"> · ended</span>}</td>
                    <td data-label="Reason">{s.reason}</td>
                    <td data-label="From" className="nowrap">{formatDate(s.createdAt, true)}</td>
                    <td data-label="Until" className="nowrap">{s.revokedAt ? `Lifted ${formatDate(s.revokedAt)}` : s.expiresAt ? formatDate(s.expiresAt, true) : 'Permanent'}</td>
                    <td data-label="By">{s.createdBy ?? '—'}</td>
                  </tr>))}</tbody>
              </table></div>}
        </Section>
      )}
      {tab === 'tickets' && (
        <Section flush title="Tickets" actions={<a className="small" href="#/moderation">Moderation</a>}>
          {detail.tickets.length === 0 ? <EmptyState title="No tickets" />
            : <ul className="history">{detail.tickets.map(t => (
                <li key={t.id} className="stack-sm">
                  <div className="row wrap"><strong>#{t.id} · {t.category}</strong><Badge tone={t.status === 'open' ? 'accent' : undefined}>{t.status === 'open' ? 'Open' : 'Closed'}</Badge>
                    <span className="muted small">{formatDate(t.createdAt, true)}</span></div>
                  <p className="pre">{t.content}</p>
                  {t.staffNote && <p className="ticket-note">{t.staffNote}</p>}
                </li>))}</ul>}
        </Section>
      )}
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
      <RoleDialog open={roleOpen} account={a} onClose={() => setRoleOpen(false)} onSave={rank => act('role', async () => {
        // One rank at a time: grant the chosen role, then take the others away.
        if (rank) await call(`/api/admin/accounts/${a.accountId}/roles`, { method: 'POST', body: { role: rank, granted: true } });
        for (const role of a.roles.filter(r => r !== rank))
          await call(`/api/admin/accounts/${a.accountId}/roles`, { method: 'POST', body: { role, granted: false } });
      }, rank ? `${a.username} is now ${ROLE_LABEL[rank]}.` : `${a.username} has no staff rank now.`)} />
    </div>
  );
}

function sanctionLabel(kind: string): string {
  return ({ mute: 'Muted', ban: 'Banned', kick: 'Kicked', warn: 'Warned' } as Record<string, string>)[kind] ?? kind;
}

function CharacterTable({ detail, worldName, onOpen }:
  { detail: AccountDetail; worldName: (id: number) => string | null; onOpen: (characterId: string, worldId: number) => void }) {
  return (
    <Section flush title="Characters on every world">
      {detail.characters.length === 0 ? <EmptyState title="No characters" />
        : <div className="table-wrap"><table className="data-table">
            <thead><tr><th scope="col">Name</th><th scope="col">World</th><th scope="col" className="num">Level</th><th scope="col">Last played</th><th scope="col"><span className="visually-hidden">Open</span></th></tr></thead>
            <tbody>{detail.characters.map(c => (
              <tr key={c.characterId}>
                <td data-label="Name" className="strong">{c.name}</td>
                <td data-label="World">{worldName(c.worldId)}</td>
                <td data-label="Level" className="num mono">{c.level}</td>
                <td data-label="Last played" className="nowrap muted">{formatDate(c.lastLoginAt, true)}</td>
                <td className="num"><button type="button" className="link" onClick={() => onOpen(c.characterId, c.worldId)}>Open character</button></td>
              </tr>))}</tbody>
          </table></div>}
    </Section>
  );
}

function QunaForm({ call, account, onDone, onError }:
  { call: Call; account: AccountSummary; onDone: (text: string) => void; onError: (error: unknown) => void }) {
  const [busy, setBusy] = useState(false);
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const sign = (event.nativeEvent as SubmitEvent).submitter?.dataset.sign === 'take' ? -1 : 1;
    const data = new FormData(form);
    const value = Number(data.get('amount'));
    if (!Number.isInteger(value) || value <= 0) return;
    setBusy(true);
    try {
      const r = await call<{ balance: number }>(`/api/admin/accounts/${account.accountId}/quna`,
        { method: 'POST', body: { requestId: requestId(), amount: sign * value, note: String(data.get('note') ?? '').trim() } });
      form.reset();
      onDone(`${sign > 0 ? 'Added' : 'Took'} ${value.toLocaleString()} Quna. Balance ${r.balance.toLocaleString()}.`);
    } catch (error) { onError(error); } finally { setBusy(false); }
  }
  return (
    <Section title="Quna" actions={<span className="small muted">Shared by every world. Recorded in the wallet ledger.</span>}>
      <form className="row wrap align-end" onSubmit={submit}>
        <div className="field"><label htmlFor="acct-quna">Amount</label><input id="acct-quna" name="amount" className="input" type="number" min={1} step={1} required /></div>
        <div className="field grow"><label htmlFor="acct-quna-note">Note</label><input id="acct-quna-note" name="note" className="input" maxLength={200} placeholder="Optional" /></div>
        <Button type="submit" busy={busy} data-sign="add">Add</Button>
        <Button type="submit" variant="danger" disabled={busy} data-sign="take">Take</Button>
      </form>
    </Section>
  );
}

const RANK_NOTE: Record<string, string> = {
  '': 'No dashboard access.',
  gamemaster: 'Helps players, announces, answers tickets, sees the live map and the logs.',
  moderator: 'Also sanctions and disables players, edits characters, rewards, events and monsters.',
  admin: 'Everything, including staff ranks, Quna, the shop, worlds and the server.',
};

function RoleDialog({ open, account, onClose, onSave }: { open: boolean; account: AccountSummary; onClose: () => void; onSave: (rank: string) => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const current = STAFF_ROLES.slice().reverse().find(r => account.roles.includes(r)) ?? '';
  const [rank, setRank] = useState<string>(current);
  useEffect(() => {
    const dialog = ref.current;
    if (!dialog) return;
    if (open && !dialog.open) { setRank(current); dialog.showModal(); }
    if (!open && dialog.open) dialog.close();
  }, [open, current]);
  return (
    <dialog ref={ref} className="dialog" onClose={onClose} aria-labelledby="role-title">
      <form method="dialog" onSubmit={e => { e.preventDefault(); onClose(); if (rank !== current) onSave(rank); }}>
        <div className="dialog-body">
          <h2 id="role-title">Staff rank of {account.username}</h2>
          <fieldset className="plain rank-choices">
            <legend className="visually-hidden">Rank</legend>
            {['', ...STAFF_ROLES].map(r => (
              <label key={r || 'none'} className="rank-choice">
                <input type="radio" name="rank" value={r} checked={rank === r} onChange={() => setRank(r)} />
                <span><strong>{r ? ROLE_LABEL[r] : 'None'}</strong><span className="block small muted">{RANK_NOTE[r]}</span></span>
              </label>
            ))}
          </fieldset>
          <p className="small muted">A new staff member sets up an authenticator at their first dashboard sign-in. Taking a rank away signs them out of the dashboard.</p>
        </div>
        <div className="dialog-actions">
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button type="submit" variant="primary" disabled={rank === current}>Save</Button>
        </div>
      </form>
    </dialog>
  );
}
