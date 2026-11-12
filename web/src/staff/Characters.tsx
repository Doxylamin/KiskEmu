import { useEffect, useRef, useState, type FormEvent } from 'react';
import { ApiError, requestId } from '../shared/api';
import { Badge, Button, Tabs } from '../shared/components';
import { hashOf } from '../shared/router';
import type { Tools } from './Dashboard';
import { ROLE_LABEL, STAFF_ROLES, can, type AdminCharacter, type CharacterDetail } from './types';
import type { AccountSummary } from './Accounts';
import { iconUrl, type Wardrobe } from './useWardrobe';
import { useWorldNames, useWorlds } from '../shared/worlds';

const GENDER_NAME: Record<number, string> = { 1: 'Male', 2: 'Female' };

export function CharacterPanel({ tools, results, query, onQuery, onSearch, showDeleted, onShowDeleted, detail, onOpen, onRefresh, wardrobe }: {
  tools: Tools; results: AdminCharacter[]; query: string; onQuery: (q: string) => void; onSearch: (q: string) => void;
  showDeleted: boolean; onShowDeleted: (value: boolean) => void;
  detail: CharacterDetail | null; onOpen: (id: string) => void; onRefresh: () => Promise<void>; wardrobe: Wardrobe | null;
}) {
  const worldName = useWorldNames();
  return (
    <section className="card panel characters-panel" aria-label="Characters">
      <div className="panel-head stack-sm">
        <h2>Characters</h2>
        <form className="row" onSubmit={e => { e.preventDefault(); onSearch(query); }}>
          <input className="input" type="search" placeholder="Name, account or UUID" aria-label="Search characters" maxLength={64}
            value={query} onChange={e => onQuery(e.target.value)} />
          <Button type="submit">Search</Button>
        </form>
        <label className="check"><input type="checkbox" checked={showDeleted} onChange={e => onShowDeleted(e.target.checked)} /><span>Show deleted characters</span></label>
      </div>
      <ul className="results" aria-label="Search results">
        {results.map(row => (
          <li key={row.characterId}>
            <button type="button" className="result" aria-current={row.characterId === detail?.character.characterId}
              onClick={() => onOpen(row.characterId)}>
              <span className="result-name">
                <span className="truncate">{row.name}</span>
                <CharacterBadges row={row} />
              </span>
              <span className="result-meta">Lv {row.level} · {worldName(row.worldId)} · {row.deleted ? 'deleted' : row.accountName}</span>
            </button>
          </li>
        ))}
        {!results.length && <li className="empty">No characters match.</li>}
      </ul>
      {detail && <CharacterDetailView key={detail.character.characterId} tools={tools} detail={detail} onRefresh={onRefresh} wardrobe={wardrobe} />}
    </section>
  );
}

// The graveyard account is disabled too: a deleted character shows "deleted", not "disabled".
function CharacterBadges({ row }: { row: AdminCharacter }) {
  if (row.deleted) return <Badge tone="danger">deleted</Badge>;
  return <>
    {row.accountDisabled && <Badge tone="danger">disabled</Badge>}
    {row.blocked && <Badge tone="danger">deactivated</Badge>}
    {row.pendingDeleteAt && <Badge>deleting</Badge>}
  </>;
}

type Tab = 'overview' | 'inventory' | 'skills';

function CharacterDetailView({ tools, detail, onRefresh, wardrobe }:
  { tools: Tools; detail: CharacterDetail; onRefresh: () => Promise<void>; wardrobe: Wardrobe | null }) {
  const [tab, setTab] = useState<Tab>('overview');
  const c = detail.character;
  const worldName = useWorldNames();
  const learned = [...new Map(detail.inventory.skills.map(s => [s.skillId, s])).values()];
  return (
    <div className="detail">
      <div className="detail-head">
        <div className="row-between">
          <h3>{c.name}</h3>
          <Button size="sm" variant="ghost" onClick={() => onRefresh()}>Refresh</Button>
        </div>
        <p className="muted small">
          Level {c.level} · {detail.className ?? `Class ${c.creationId}`} · {GENDER_NAME[c.gender] ?? `Gender ${c.gender}`} · Race {c.race} · World {worldName(c.worldId)} · Account {c.accountName}
        </p>
      </div>
      <Tabs label="Character" value={tab} onChange={setTab} tabs={[
        { id: 'overview', label: 'Overview' },
        { id: 'inventory', label: `Inventory (${detail.inventory.items.length})` },
        { id: 'skills', label: `Skills (${learned.length})` },
      ]} />
      <div className="detail-body">
        {tab === 'overview' && <Overview tools={tools} detail={detail} onRefresh={onRefresh} />}
        {tab === 'inventory' && <Inventory detail={detail} wardrobe={wardrobe} />}
        {tab === 'skills' && <Skills detail={detail} wardrobe={wardrobe} learned={learned} />}
      </div>
    </div>
  );
}

function Overview({ tools, detail, onRefresh }: { tools: Tools; detail: CharacterDetail; onRefresh: () => Promise<void> }) {
  const c = detail.character;
  const roles = c.accountRoles ?? [];
  const canStaff = can(tools.session.profile, 'manageStaff');
  const canViewAccounts = can(tools.session.profile, 'viewAccounts');
  const canEdit = can(tools.session.profile, 'editCharacters');
  const shownRole = STAFF_ROLES.slice().reverse().find(r => roles.includes(r));
  const [level, setLevel] = useState(String(c.level));
  const [creationId, setCreationId] = useState(String(c.creationId));
  const [ascension, setAscension] = useState(String(detail.ascension || 1));
  const [busy, setBusy] = useState<string | null>(null);
  useEffect(() => { setLevel(String(c.level)); setCreationId(String(c.creationId)); setAscension(String(detail.ascension || 1)); }, [c, detail.ascension]);
  const classes = detail.classes ?? [];

  // One audited command per confirmation; the request UUID makes a retry harmless.
  function command(key: string, title: string, body: string, payload: Record<string, unknown>, done: string, logText: string) {
    tools.confirm({
      title, body, note: 'This is recorded in the audit log under your account.', confirmLabel: title,
      onConfirm: async () => {
        setBusy(key);
        try {
          await tools.call('/api/admin/commands', { method: 'POST', body: { requestId: requestId(), characterId: c.characterId, ...payload } });
          tools.notify(done, 'ok'); tools.log(logText);
          await onRefresh();
        } catch (error) { tools.fail(error); }
        finally { setBusy(null); }
      },
    });
  }

  // Staff standing is per character: a new character is a player even on a staff account.
  function toggleStaff() {
    const granted = !c.staff;
    command('staff', granted ? 'Make staff character' : 'Make player character',
      granted ? `Give ${c.name} the ${(shownRole && ROLE_LABEL[shownRole]) ?? 'staff'} standing in the world (name tag, builder commands, wings)? Applies at their next world entry.`
        : `Make ${c.name} a regular character again? Applies at their next world entry.`,
      { command: 'staff', staff: granted }, `${c.name} is now a ${granted ? 'staff' : 'player'} character.`, `${granted ? 'Staff' : 'Player'} character → ${c.name}`);
  }

  function submitLevel(event: FormEvent) {
    event.preventDefault();
    const value = Number(level);
    if (!Number.isInteger(value) || value < 1 || value > 100) { tools.notify('Level must be between 1 and 100.', 'error'); return; }
    if (value === c.level) return;
    command('level', 'Set level', `Set ${c.name} to level ${value}? Skills follow the new level.`, { command: 'level', level: value },
      `${c.name} is now level ${value}.`, `Level ${value} → ${c.name}`);
  }
  function submitClass(event: FormEvent) {
    event.preventDefault();
    const target = classes.find(x => x.creationId === Number(creationId));
    if (!target || target.creationId === c.creationId) return;
    command('class', 'Change class', `Change ${c.name} from ${detail.className ?? 'class ' + c.creationId} to ${target.name}? Skills follow the new class; race and appearance stay. Applies at their next world entry.`,
      { command: 'class', creationId: target.creationId }, `${c.name} is now a ${target.name}.`, `Class ${target.name} → ${c.name}`);
  }
  function submitAscension(event: FormEvent) {
    event.preventDefault();
    const grade = Number(ascension);
    if (grade === detail.ascension) return;
    command('ascension', 'Set ascension', `Set ${c.name} to ascension grade ${grade}? Applies at their next world entry.`,
      { command: 'ascension', ascension: grade }, `${c.name} is now ascension grade ${grade}.`, `Ascension ${grade} → ${c.name}`);
  }
  return (
    <div className="stack">
      <div className="stack-sm">
        <div className="row-between">
          <h4>Account {c.accountName}</h4>
          {canViewAccounts && <a className="btn btn-secondary btn-sm" href={hashOf('accounts', c.accountId)}>Open account</a>}
        </div>
        <div className="row wrap">
          {roles.length ? roles.map(r => <Badge key={r} tone="accent">{ROLE_LABEL[r] ?? r}</Badge>) : <Badge>Player</Badge>}
          {c.accountDisabled && <Badge tone="danger">Account disabled</Badge>}
          <QunaBalance tools={tools} accountId={c.accountId} />
        </div>
        <p className="muted small">Staff ranks, Quna, two-step verification and sign-in are managed on the account’s page.</p>
        <h4>This character</h4>
        <div className="row wrap">
          {c.staff ? <Badge tone="accent">Staff character</Badge> : <Badge>Player character</Badge>}
          {roles.length > 0 && !c.accountDisabled && canStaff && (
            <Button size="sm" variant={c.staff ? 'danger' : 'secondary'} busy={busy === 'staff'} onClick={toggleStaff}>
              {c.staff ? 'Make player character' : 'Make staff character'}
            </Button>
          )}
        </div>
      </div>
      <div className="stack-sm">
        <h4>Progression</h4>
        <form className="inline-field" onSubmit={submitLevel}>
          <label htmlFor="level">Level</label>
          <input id="level" className="input input-sm" type="number" min={1} max={100} required value={level} onChange={e => setLevel(e.target.value)} />
          <Button type="submit" size="sm" busy={busy === 'level'} disabled={!canEdit || Number(level) === c.level}>Set</Button>
        </form>
        {classes.length > 0 && (
          <form className="inline-field" onSubmit={submitClass}>
            <label htmlFor="class">Class</label>
            <select id="class" className="select select-sm" value={creationId} onChange={e => setCreationId(e.target.value)}>
              {!classes.some(x => x.creationId === c.creationId) && <option value={c.creationId} disabled>Class {c.creationId} (no profile)</option>}
              {classes.map(x => <option key={x.creationId} value={x.creationId}>{x.name}</option>)}
            </select>
            <Button type="submit" size="sm" busy={busy === 'class'} disabled={!canEdit || Number(creationId) === c.creationId}>Change</Button>
          </form>
        )}
        <form className="inline-field" onSubmit={submitAscension}>
          <label htmlFor="ascension">Ascension</label>
          <select id="ascension" className="select select-sm" value={ascension} onChange={e => setAscension(e.target.value)}>
            {[1, 2, 3, 4, 5, 6].map(g => <option key={g} value={g}>Grade {g}</option>)}
          </select>
          <Button type="submit" size="sm" busy={busy === 'ascension'} disabled={!canEdit || Number(ascension) === detail.ascension}>Set</Button>
        </form>
      </div>
      <ManageCharacter tools={tools} character={c} onRefresh={onRefresh} />
    </div>
  );
}

// Delete, cancel a pending deletion, deactivate and move to another account (POST /api/admin/characters/{id}/actions, answered
// by the character's world server). Each is confirmed, audited, and refused while the character is in the world.
function ManageCharacter({ tools, character: c, onRefresh }: { tools: Tools; character: AdminCharacter; onRefresh: () => Promise<void> }) {
  const canEdit = can(tools.session.profile, 'editCharacters');
  const canManage = can(tools.session.profile, 'manageAccounts');
  const [busy, setBusy] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [moving, setMoving] = useState(false);
  const [movingWorld, setMovingWorld] = useState(false);

  function act(action: string, title: string, body: string, done: string, extra: Record<string, unknown> = {}, danger = false) {
    tools.confirm({
      title, body, danger, confirmLabel: title,
      note: 'Refused while the character is in the world. This is recorded in the audit log under your account.',
      onConfirm: async () => {
        setBusy(action);
        try {
          await tools.call(`/api/admin/characters/${c.characterId}/actions`, { method: 'POST',
            body: { requestId: requestId(), action, reason: reason.trim() || null, ...extra } });
          tools.notify(done, 'ok'); tools.log(`${title} → ${c.name}`);
          setReason(''); setMoving(false);
          await onRefresh();
        } catch (error) { tools.fail(error); }
        finally { setBusy(null); }
      },
    });
  }

  if (c.deleted) return (
    <div className="stack-sm">
      <h4>Manage character</h4>
      <p className="muted small">This character was deleted: it is on the deleted-characters account and in no lobby.</p>
    </div>
  );
  if (!canEdit && !canManage) return null;
  const pending = c.pendingDeleteAt ? new Date(c.pendingDeleteAt) : null;
  return (
    <div className="stack-sm">
      <h4>Manage character</h4>
      <div className="row wrap">
        {c.blocked ? <Badge tone="danger">Deactivated{c.blockReason ? `: ${c.blockReason}` : ''}</Badge> : <Badge tone="ok">Active</Badge>}
        {pending && <Badge>Deletion pending · {pending.toLocaleString()}</Badge>}
      </div>
      <div className="inline-field">
        <label htmlFor="action-reason">Reason</label>
        <input id="action-reason" className="input input-sm" maxLength={200} value={reason} onChange={e => setReason(e.target.value)}
          placeholder="Optional, kept in the audit log" />
      </div>
      <div className="row wrap">
        {canEdit && (c.blocked
          ? <Button size="sm" variant="secondary" busy={busy === 'unblock'}
              onClick={() => act('unblock', 'Reactivate', `Let ${c.name} enter the world again?`, `${c.name} is active again.`)}>Reactivate</Button>
          : <Button size="sm" variant="secondary" busy={busy === 'block'}
              onClick={() => act('block', 'Deactivate', `Deactivate ${c.name}? The character stays on its account and in the lobby, but cannot enter the world until it is reactivated.`,
                `${c.name} is deactivated.`)}>Deactivate</Button>)}
        {canEdit && pending && (
          <Button size="sm" variant="secondary" busy={busy === 'cancel_delete'}
            onClick={() => act('cancel_delete', 'Cancel deletion', `Cancel the deletion of ${c.name} that its owner asked for (due ${pending.toLocaleString()})?`,
              `${c.name} will not be deleted.`)}>Cancel deletion</Button>
        )}
        {canManage && (
          <Button size="sm" variant="secondary" busy={busy === 'transfer'} onClick={() => setMoving(m => !m)}>Move to another account…</Button>
        )}
        {canManage && (
          <Button size="sm" variant="secondary" onClick={() => setMovingWorld(m => !m)}>Move to another world…</Button>
        )}
        {canManage && (
          <Button size="sm" variant="danger" busy={busy === 'delete'}
            onClick={() => act('delete', 'Delete character', `Delete ${c.name} now? It moves to the deleted-characters account and is renamed ${c.name}#deleted…, which frees the name. Its items and history are kept, but the owner cannot get it back from the lobby.`,
              `${c.name} is deleted.`, {}, true)}>Delete</Button>
        )}
      </div>
      {moving && canManage && <MoveCharacter tools={tools} character={c}
        onPick={account => act('transfer', 'Move character',
          `Move ${c.name} from ${c.accountName} to ${account.username}? It takes the first free character slot of that account on this world. Its legion membership, friends and mail stay with the character; a pending deletion is cancelled.`,
          `${c.name} is now on ${account.username}.`, { targetAccount: account.accountId })} />}
      {movingWorld && canManage && <MoveWorld tools={tools} character={c} reason={reason} onDone={async () => { setMovingWorld(false); setReason(''); await onRefresh(); }} />}
    </div>
  );
}

// A move to another world (POST /api/admin/world-moves on the realm, which runs it on both world servers): administrators.
// The request id is kept while a move is incomplete, so "Move" again finishes the same move instead of starting another.
function MoveWorld({ tools, character: c, reason, onDone }: { tools: Tools; character: AdminCharacter; reason: string; onDone: () => Promise<void> }) {
  const worldName = useWorldNames();
  const worlds = useWorlds().filter(w => w.worldId !== c.worldId);
  const [world, setWorld] = useState('');
  const [account, setAccount] = useState<AccountSummary | null>(null);
  const [picking, setPicking] = useState(false);
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const pending = useRef<string | null>(null);
  const target = Number(world) || worlds[0]?.worldId;
  if (!worlds.length) return <p className="muted small">There is no other world to move the character to.</p>;

  function submit(event: FormEvent) {
    event.preventDefault();
    if (!target) return;
    const newName = name.trim();
    const owner = account?.username ?? c.accountName;
    tools.confirm({
      title: 'Move to another world', confirmLabel: 'Move', danger: true,
      body: `Move ${c.name} from ${worldName(c.worldId)} to ${worldName(target)}${newName ? ` as ${newName}` : ''}, on ${owner}? It takes the first free character slot of that account there. `
        + 'Its items, skills, quests and progress move; its legion membership, friends, blocks, mail, market listings, account storage and shop purchase limits stay behind. '
        + `The original is then deleted on ${worldName(c.worldId)}.`,
      note: 'Refused while the character is in the world. Recorded in the audit log of both worlds under your account.',
      onConfirm: async () => {
        setBusy(true);
        pending.current ??= requestId();
        try {
          const moved = await tools.call<{ characterId: string; name: string; worldId: number }>('/api/admin/world-moves', {
            method: 'POST', world: 'all',
            body: { requestId: pending.current, characterId: c.characterId, sourceWorld: c.worldId, targetWorld: target,
              targetAccount: account?.accountId ?? c.accountId, newName: newName || null, reason: reason.trim() || null },
          });
          pending.current = null;
          tools.notify(`${c.name} is now ${moved.name} on ${worldName(moved.worldId)}.`, 'ok');
          tools.log(`Move to ${worldName(moved.worldId)} → ${c.name}`);
          await onDone();
        } catch (error) {
          // Only an incomplete move is finished with the same id; a refusal starts over.
          if (!(error instanceof ApiError && error.code === 'move_incomplete')) pending.current = null;
          tools.fail(error);
        } finally { setBusy(false); }
      },
    });
  }
  return (
    <form className="stack-sm" onSubmit={submit}>
      <div className="inline-field">
        <label htmlFor="move-world">World</label>
        <select id="move-world" className="select select-sm" value={target ?? ''} onChange={e => setWorld(e.target.value)}>
          {worlds.map(w => <option key={w.worldId} value={w.worldId}>{w.name}</option>)}
        </select>
      </div>
      <div className="inline-field">
        <label htmlFor="move-name">New name</label>
        <input id="move-name" className="input input-sm" maxLength={64} value={name} onChange={e => setName(e.target.value)}
          placeholder={`Optional (keeps ${c.name})`} />
      </div>
      <div className="row wrap">
        <span className="small muted">Account: {account?.username ?? `${c.accountName} (same)`}</span>
        <Button size="sm" variant="ghost" onClick={() => setPicking(p => !p)}>{picking ? 'Close' : 'Another account…'}</Button>
        {account && <Button size="sm" variant="ghost" onClick={() => setAccount(null)}>Same account</Button>}
      </div>
      {picking && <MoveCharacter tools={tools} character={c} onPick={a => { setAccount(a); setPicking(false); }} />}
      <div className="row"><Button type="submit" size="sm" variant="danger" busy={busy}>Move</Button></div>
      <p className="muted small">The name must be free on the target world. Legion membership, friends, mail and market listings do not move.</p>
    </form>
  );
}

// Picks the target account from the realm's account search (GET /api/admin/accounts, not world-scoped).
function MoveCharacter({ tools, character, onPick }: { tools: Tools; character: AdminCharacter; onPick: (account: AccountSummary) => void }) {
  const [query, setQuery] = useState('');
  const [found, setFound] = useState<AccountSummary[] | null>(null);
  async function search(event: FormEvent) {
    event.preventDefault();
    if (!query.trim()) return;
    try {
      const page = await tools.call<{ accounts: AccountSummary[] }>('/api/admin/accounts?' + new URLSearchParams({ q: query.trim(), filter: 'all', page: '0' }));
      setFound(page.accounts.filter(a => a.accountId !== character.accountId));
    } catch (error) { tools.fail(error); }
  }
  return (
    <div className="stack-sm">
      <form className="row" onSubmit={search}>
        <input className="input input-sm" type="search" placeholder="Target account or character name" aria-label="Search target account" maxLength={64}
          value={query} onChange={e => setQuery(e.target.value)} />
        <Button type="submit" size="sm">Find</Button>
      </form>
      {found && (found.length
        ? <ul className="results" aria-label="Target accounts">
            {found.map(a => (
              <li key={a.accountId}>
                <button type="button" className="result" disabled={!!a.disabledAt} onClick={() => onPick(a)}>
                  <span className="result-name"><span className="truncate">{a.username}</span>{a.disabledAt && <Badge tone="danger">disabled</Badge>}</span>
                  <span className="result-meta">{a.rankLabel ?? 'Player'}</span>
                </button>
              </li>
            ))}
          </ul>
        : <p className="empty">No other account matches.</p>)}
    </div>
  );
}

// The account's Quna balance (one wallet in the realm, shared by every world), read-only here: staff add or take Quna on
// the account's page (Accounts.tsx, QunaForm).
function QunaBalance({ tools, accountId }: { tools: Tools; accountId: string }) {
  const [balance, setBalance] = useState<number | null>(null);
  useEffect(() => {
    let live = true;
    setBalance(null);
    tools.call<{ balance: number }>(`/api/admin/accounts/${accountId}/quna`)
      .then(r => { if (live) setBalance(r.balance); }, () => { if (live) setBalance(null); });
    return () => { live = false; };
  }, [tools, accountId]);
  return <Badge>{balance === null ? '… Quna' : `${balance.toLocaleString()} Quna`}</Badge>;
}

function Inventory({ detail, wardrobe }: { detail: CharacterDetail; wardrobe: Wardrobe | null }) {
  // Equipped first in slot order, then everything else in storage order.
  const rank = (location: string, slot: number | null) => location === 'equipment' ? slot ?? 0 : Number.MAX_SAFE_INTEGER;
  const items = [...detail.inventory.items].sort((a, b) => rank(a.location, a.slot) - rank(b.location, b.slot));
  if (!items.length) return <p className="empty">No items.</p>;
  return (
    <ul className="inventory">
      {items.map((entry, index) => {
        const item = wardrobe?.byId.get(entry.templateId);
        return (
          <li key={entry.instanceId ?? index}>
            <ItemIcon src={iconUrl(wardrobe, item?.icon)} grade={item?.grade} />
            <div className="grow">
              <div className="truncate">{item?.name ?? `Template ${entry.templateId}`}</div>
              <div className="muted small">{entry.location === 'equipment' ? `Equipped · slot ${entry.slot}` : entry.location} · ID {entry.templateId}</div>
            </div>
            <span className="qty">×{entry.quantity.toLocaleString()}</span>
          </li>
        );
      })}
    </ul>
  );
}

function Skills({ detail, wardrobe, learned }: { detail: CharacterDetail; wardrobe: Wardrobe | null; learned: CharacterDetail['inventory']['skills'] }) {
  if (!learned.length) return <p className="empty">No skills learned.</p>;
  return (
    <div className="stack-sm">
      <p className="muted small">Skills follow the character level. Highlighted skills are on the skill bar.</p>
      <div className="skills">
        {learned.map(s => {
          const info = wardrobe?.data.skills[s.skillId];
          const name = info?.name && info.name !== 'None' ? info.name : `Skill ${s.skillId}`;
          const slotted = detail.inventory.skills.some(x => x.skillId === s.skillId && x.slot !== null);
          const title = `${name} · level ${s.level} · ID ${s.skillId}${slotted ? ' · on skill bar' : ''}`;
          const src = iconUrl(wardrobe, info?.icon);
          return src
            ? <img key={s.skillId} className={slotted ? 'slotted' : ''} src={src} alt={name} title={title} loading="lazy" />
            : <div key={s.skillId} className={`skill-chip ${slotted ? 'slotted' : ''}`} title={title}>{s.skillId}</div>;
        })}
      </div>
    </div>
  );
}

export function ItemIcon({ src, grade, size = 36 }: { src: string | null; grade?: string | null; size?: 36 | 48 | 56 }) {
  const className = `icon icon-${size} g-${grade ?? 'none'}`;
  return src ? <img className={className} src={src} alt="" loading="lazy" /> : <div className={`${className} icon-empty`} aria-hidden="true" />;
}
