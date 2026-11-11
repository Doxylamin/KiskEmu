// NPCs: custom NPCs that stand where staff place them and talk in the client's own dialog style (the realm's list, placed
// by every world). Their names and lines reach players through the client patch pak, built from "Export texts" with the
// client tool (docs/CUSTOM_NPCS.md); without the pak players see the base NPC and read the lines as notices.
import { useCallback, useEffect, useState } from 'react';
import { Badge, Button, ConfirmDialog, Icon, Status, type ConfirmRequest, type StatusMessage } from '../shared/components';
import { describe, type Call } from './StaffApp';
import { ERRORS, type LiveOverview } from './types';

type Texts = Record<string, string>;
interface Placement { mapId: number; x: number; y: number; z: number; yaw: number }
interface Condition { minLevel: number; maxLevel: number; flag: string | null; noFlag: string | null; itemId: number; itemCount: number; race: number }
interface Action { type: string; itemId: number; count: number; flag: string | null; text: string | null; mapId: number; x: number; y: number; z: number }
interface DialogNode { key: string; lines: Texts[]; when: Condition | null; actions: Action[] }
/** A custom NPC (/api/admin/custom-npcs; the realm's game.custom_npcs). */
export interface CustomNpc {
  templateId: number; slug: string; baseTemplateId: number; name: Texts; placements: Placement[]; nodes: DialogNode[];
  enabled: boolean; updatedAt: string;
}
interface CustomNpcList { npcs: CustomNpc[]; firstId: number; actions: string[]; talkNames: Record<string, string[]> }
interface NpcChoice { npcId: number; name: string; level: number; hostile: boolean }

Object.assign(ERRORS, {
  invalid_template_id: 'This NPC cannot be changed here.',
  invalid_slug: 'The code name is 1 to 32 letters and digits, no spaces.',
  slug_taken: 'Another NPC already has this code name.',
  invalid_base_template: 'Pick the official NPC this one looks like.',
  unknown_base_template: 'The server does not know that NPC. Pick another one.',
  hostile_base_template: 'That NPC is a monster. Pick one players talk to.',
  invalid_name: 'Give the NPC an English name of up to 40 characters.',
  invalid_placement: 'A placement needs a map and a position.',
  invalid_nodes: 'Add 1 to 20 dialogs.',
  invalid_node_key: 'Each dialog needs its own key: 1 to 24 letters and digits.',
  invalid_lines: 'Each dialog needs 1 to 20 English lines of up to 500 characters.',
  invalid_condition: 'Check the dialog conditions: levels 0 to 100, flag names of letters, digits and _, an item with its count.',
  invalid_action: 'Check the actions: an item and a count, a flag name, a notice text (up to 200), or a map and position.',
  npcs_full: 'There are no free custom NPC ids left.',
});

const LOCALES: [string, string][] = [['en-US', 'English'], ['de-DE', 'German'], ['fr-FR', 'French'], ['es-ES', 'Spanish'], ['pt-BR', 'Portuguese'],
  ['ru-RU', 'Russian'], ['ko-KR', 'Korean'], ['ja-JP', 'Japanese'], ['zh-CN', 'Chinese (simplified)'], ['zh-TW', 'Chinese (traditional)']];
const EN = 'en-US';
const ACTION_LABELS: Record<string, string> = {
  give_item: 'Give an item', give_kina: 'Give Kina', set_flag: 'Set a flag', clear_flag: 'Clear a flag', notice: 'Show a notice', teleport: 'Teleport',
};
const RACES = ['Both races', 'Light only', 'Dark only'];

const noCondition = (): Condition => ({ minLevel: 0, maxLevel: 0, flag: null, noFlag: null, itemId: 0, itemCount: 0, race: 0 });
const newAction = (type: string): Action => ({ type, itemId: 0, count: type === 'give_kina' ? 1000 : 1, flag: null, text: null, mapId: 0, x: 0, y: 0, z: 0 });
const newNode = (key: string): DialogNode => ({ key, lines: [{ [EN]: '' }], when: null, actions: [] });
const blank = (): CustomNpc => ({ templateId: 0, slug: '', baseTemplateId: 0, name: { [EN]: '' }, placements: [], nodes: [newNode('Hello')], enabled: true, updatedAt: '' });
const num = (value: string) => (Number.isFinite(Number(value)) ? Number(value) : 0);
const orNull = (value: string) => (value.trim() ? value.trim() : null);

/** Lines of the selected language, one per row; other languages keep the English line count. */
function linesText(node: DialogNode, locale: string) { return node.lines.map(l => l[locale] ?? '').join('\n'); }
function withLines(node: DialogNode, locale: string, text: string): DialogNode {
  const rows = text.split('\n');
  if (locale === EN) return { ...node, lines: rows.map((row, i) => ({ ...(node.lines[i] ?? {}), [EN]: row })) };
  return { ...node, lines: node.lines.map((l, i) => { const next = { ...l }; if (rows[i]?.trim()) next[locale] = rows[i]; else delete next[locale]; return next; }) };
}
/** Drops empty English rows and empty translations before saving. */
function cleaned(npc: CustomNpc): CustomNpc {
  const texts = (t: Texts) => Object.fromEntries(Object.entries(t).filter(([, v]) => v.trim()));
  return {
    ...npc, name: texts(npc.name),
    nodes: npc.nodes.map(n => ({ ...n, lines: n.lines.filter(l => (l[EN] ?? '').trim()).map(texts) })),
  };
}

export function CustomNpcs({ call, active, isAdmin }: { call: Call; active: boolean; isAdmin: boolean }) {
  const [list, setList] = useState<CustomNpcList | null>(null);
  const [draft, setDraft] = useState<CustomNpc | null>(null);
  const [locale, setLocale] = useState(EN);
  const [baseQuery, setBaseQuery] = useState('');
  const [baseFound, setBaseFound] = useState<NpcChoice[]>([]);
  const [baseName, setBaseName] = useState('');
  const [character, setCharacter] = useState('');
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<StatusMessage | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);

  const load = useCallback(async () => {
    try { setList(await call<CustomNpcList>('/api/admin/custom-npcs')); } catch (e) { setStatus({ text: describe(e), kind: 'error' }); }
  }, [call]);
  useEffect(() => { if (active) load(); }, [active, load]);

  useEffect(() => {
    if (baseQuery.trim().length < 2) { setBaseFound([]); return; }
    const t = window.setTimeout(() => call<NpcChoice[]>('/api/admin/npcs?' + new URLSearchParams({ q: baseQuery.trim() }))
      .then(found => setBaseFound(found.filter(n => !n.hostile))).catch(() => setBaseFound([])), 250);
    return () => clearTimeout(t);
  }, [call, baseQuery]);

  const set = <K extends keyof CustomNpc>(key: K, value: CustomNpc[K]) => setDraft(d => d && { ...d, [key]: value });
  const setNode = (index: number, node: DialogNode) => setDraft(d => d && { ...d, nodes: d.nodes.map((n, i) => (i === index ? node : n)) });

  function edit(npc: CustomNpc) {
    setStatus(null);
    setLocale(EN);
    setBaseName('');
    setDraft(JSON.parse(JSON.stringify(npc)) as CustomNpc);
  }

  /** A placement where a character in the picked world stands now (the live list). */
  async function placeAtCharacter() {
    if (!draft) return;
    try {
      const live = await call<LiveOverview>('/api/admin/live');
      const player = live.players.find(p => p.name.toLowerCase() === character.trim().toLowerCase());
      if (!player?.position || !player.mapId) { setStatus({ text: `${character.trim() || 'That character'} is not in the picked world now.`, kind: 'error' }); return; }
      const [x, y, z] = player.position;
      set('placements', [...draft.placements, { mapId: player.mapId, x: Math.round(x), y: Math.round(y), z: Math.round(z), yaw: Math.round(player.heading ?? 0) }]);
    } catch (e) { setStatus({ text: describe(e), kind: 'error' }); }
  }

  async function save() {
    if (!draft) return;
    setBusy(true);
    try {
      const body = cleaned(draft);
      const saved = await call<CustomNpc>(draft.templateId ? `/api/admin/custom-npcs/${draft.templateId}` : '/api/admin/custom-npcs',
        { method: draft.templateId ? 'PUT' : 'POST', body });
      setStatus({ text: `Saved “${saved.name[EN]}” (NPC ${saved.templateId}). The worlds place it within a minute; build a new patch pak for changed names or lines.`, kind: 'ok' });
      setDraft(null);
      await load();
    } catch (e) { setStatus({ text: describe(e), kind: 'error' }); }
    finally { setBusy(false); }
  }

  async function remove(npc: CustomNpc) {
    try {
      await call(`/api/admin/custom-npcs/${npc.templateId}`, { method: 'DELETE' });
      setStatus({ text: `Removed “${npc.name[EN]}”.`, kind: 'ok' });
      if (draft?.templateId === npc.templateId) setDraft(null);
      await load();
    } catch (e) { setStatus({ text: describe(e), kind: 'error' }); }
  }

  /** custom-npcs.json for the client tool (kisk-client-tool custom-npcs --definitions). */
  async function exportTexts() {
    try {
      const definitions = await call<unknown>('/api/admin/custom-npcs/export');
      const url = URL.createObjectURL(new Blob([JSON.stringify(definitions, null, 2)], { type: 'application/json' }));
      const link = document.createElement('a');
      link.href = url;
      link.download = 'custom-npcs.json';
      link.click();
      URL.revokeObjectURL(url);
    } catch (e) { setStatus({ text: describe(e), kind: 'error' }); }
  }

  const ready = !!draft && /^[A-Za-z0-9]{1,32}$/.test(draft.slug) && draft.baseTemplateId > 0 && !!draft.name[EN]?.trim()
    && draft.nodes.length > 0 && draft.nodes.every(n => n.key.trim() && n.lines.some(l => l[EN]?.trim()));

  return (
    <main className="page">
      <header className="page-head">
        <div>
          <h1>NPCs <span className="badge">Beta</span></h1>
          <p className="muted">NPCs you place in the worlds. They look and are named like the official merchant you pick, and say their lines as notices.</p>
        </div>
        <div className="row">
          <Button size="sm" disabled={!list?.npcs.length} onClick={exportTexts}><Icon name="open" size={16} /> Export texts</Button>
          {isAdmin && <Button variant="primary" size="sm" disabled={!list} onClick={() => { setStatus(null); setLocale(EN); setBaseName(''); setDraft(blank()); }}>
            <Icon name="plus" size={16} /> New NPC</Button>}
        </div>
      </header>
      <Status message={status} />
      <div className="broadcast-grid">
        <section className="card">
          <div className="panel-head"><h2>Custom NPCs</h2><Button size="sm" onClick={load}><Icon name="refresh" size={16} /> Refresh</Button></div>
          <div className="table-wrap">
            <table className="data-table">
              <thead><tr><th>NPC</th><th>Placed</th><th>Dialogs</th><th>State</th>{isAdmin && <th aria-label="Actions" />}</tr></thead>
              <tbody>
                {list?.npcs.map(n => (
                  <tr key={n.templateId}>
                    <td>{n.name[EN]}<span className="faint small block">{n.templateId} · {n.slug} · looks like {n.baseTemplateId}</span></td>
                    <td className="small">{n.placements.length ? n.placements.map(p => `map ${p.mapId}`).join(', ') : 'Nowhere'}</td>
                    <td className="small">{n.nodes.map(d => d.key).join(', ')}</td>
                    <td>{n.enabled ? <Badge tone="ok">Placed</Badge> : <Badge tone="danger">Hidden</Badge>}</td>
                    {isAdmin && <td className="actions">
                      <button type="button" className="icon-btn" aria-label={`Edit ${n.name[EN]}`} onClick={() => edit(n)}><Icon name="edit" size={16} /></button>
                      <button type="button" className="icon-btn" aria-label={`Remove ${n.name[EN]}`} onClick={() => setConfirm({ title: `Remove “${n.name[EN]}”?`, confirmLabel: 'Remove', danger: true,
                        body: <p>It leaves every world within a minute. Characters keep the flags its dialogs set.</p>, onConfirm: () => remove(n) })}>
                        <Icon name="trash" size={16} /></button>
                    </td>}
                  </tr>
                ))}
                {list && list.npcs.length === 0 && <tr><td colSpan={isAdmin ? 5 : 4} className="empty pad">No custom NPCs yet.{isAdmin ? ' Add one with “New NPC”.' : ''}</td></tr>}
              </tbody>
            </table>
          </div>
        </section>

        {draft ? (
          <section className="card card-body stack">
            <h2>{draft.templateId ? `Edit NPC ${draft.templateId}` : 'New NPC'}</h2>
            <div className="field">
              <label htmlFor="cn-locale">Texts in</label>
              <select id="cn-locale" className="select select-sm" value={locale} onChange={e => setLocale(e.target.value)}>
                {LOCALES.map(([code, label]) => <option key={code} value={code}>{label}{code === EN ? ' (required)' : ''}</option>)}
              </select>
              {locale !== EN && <p className="faint small">Empty texts use the English one.</p>}
            </div>
            <div className="field"><label htmlFor="cn-name">Name</label>
              <input id="cn-name" className="input" maxLength={40} value={draft.name[locale] ?? ''} placeholder={locale === EN ? '' : draft.name[EN]}
                onChange={e => set('name', { ...draft.name, [locale]: e.target.value })} /></div>
            <div className="field"><label htmlFor="cn-slug">Code name (letters and digits; names its texts in the patch pak)</label>
              <input id="cn-slug" className="input" maxLength={32} value={draft.slug} onChange={e => set('slug', e.target.value.replace(/[^A-Za-z0-9]/g, ''))} /></div>
            <div className="field">
              <label htmlFor="cn-base">Looks like (an official merchant, so players get [F])</label>
              <input id="cn-base" className="input" type="search" placeholder="NPC name or ID" value={baseQuery} onChange={e => setBaseQuery(e.target.value)} />
              {baseFound.length > 0 && (
                <ul className="pick-list">
                  {baseFound.map(f => (
                    <li key={f.npcId}><button type="button" className="mob-choice" onClick={() => { set('baseTemplateId', f.npcId); setBaseName(f.name); setBaseQuery(''); }}>
                      <span>{f.name}</span><span className="faint small">{f.npcId} · level {f.level}</span></button></li>
                  ))}
                </ul>
              )}
              <p className="small">{draft.baseTemplateId ? <>NPC <span className="mono">{draft.baseTemplateId}</span>{baseName ? ` (${baseName})` : ''}</> : <span className="faint">None picked</span>}</p>
            </div>

            <div className="field">
              <label>Where it stands</label>
              <ul className="reward-items">
                {draft.placements.map((p, i) => (
                  <li key={i}>
                    <span className="grow small mono">map {p.mapId} · {p.x}, {p.y}, {p.z} · facing {p.yaw}°</span>
                    <button type="button" className="icon-btn" aria-label="Remove placement" onClick={() => set('placements', draft.placements.filter((_, n) => n !== i))}>×</button>
                  </li>
                ))}
              </ul>
              <form className="row" onSubmit={e => { e.preventDefault(); placeAtCharacter(); }}>
                <input className="input input-sm" placeholder="Character in the picked world" aria-label="Character" value={character} onChange={e => setCharacter(e.target.value)} />
                <Button size="sm" type="submit" disabled={!character.trim() || draft.placements.length >= 20}>Place where they stand</Button>
              </form>
            </div>

            {draft.nodes.map((node, i) => (
              <fieldset key={i} className="card card-body stack">
                <div className="row-between">
                  <label className="small" htmlFor={`cn-key-${i}`}>Dialog {i + 1}{i === 0 ? ' (checked first)' : ''}</label>
                  <button type="button" className="icon-btn" aria-label="Remove dialog" disabled={draft.nodes.length === 1}
                    onClick={() => set('nodes', draft.nodes.filter((_, n) => n !== i))}>×</button>
                </div>
                <input id={`cn-key-${i}`} className="input input-sm" maxLength={24} aria-label="Dialog key" value={node.key}
                  onChange={e => setNode(i, { ...node, key: e.target.value.replace(/[^A-Za-z0-9]/g, '') })} />
                <label className="small" htmlFor={`cn-lines-${i}`}>Lines, one per row (the player continues with the interaction key)</label>
                <textarea id={`cn-lines-${i}`} className="input" rows={Math.max(3, node.lines.length + 1)} value={linesText(node, locale)}
                  onChange={e => setNode(i, withLines(node, locale, e.target.value))} />
                <ConditionEditor value={node.when} onChange={when => setNode(i, { ...node, when })} />
                <ActionsEditor actions={node.actions} types={list?.actions ?? Object.keys(ACTION_LABELS)} onChange={actions => setNode(i, { ...node, actions })} />
                {draft.templateId > 0 && <p className="faint small mono">{list?.talkNames[String(draft.templateId)]?.[i]}</p>}
              </fieldset>
            ))}
            <Button size="sm" disabled={draft.nodes.length >= 20} onClick={() => set('nodes', [...draft.nodes, newNode(`Dialog${draft.nodes.length + 1}`)])}>
              <Icon name="plus" size={16} /> Add a dialog</Button>
            <label className="check"><input type="checkbox" checked={draft.enabled} onChange={e => set('enabled', e.target.checked)} /><span>Place it in the worlds</span></label>
            <div className="row-between">
              <Button onClick={() => setDraft(null)}>Cancel</Button>
              <Button variant="primary" busy={busy} disabled={!ready} onClick={save}>Save NPC</Button>
            </div>
          </section>
        ) : (
          <section className="card card-body stack">
            <h2>How it works</h2>
            <ul className="muted help-list">
              <li>Each NPC looks like the official NPC you pick and stands where you place it, on every world, within a minute of saving.</li>
              <li>When a player talks to it, the first dialog whose conditions hold plays. Its actions run when the player finishes the last line.</li>
              <li>Flags belong to each character and this NPC: set one at the end of a dialog and give the next dialog that flag as a condition (or the first one “not flag”) to move the story on or pay a reward once.</li>
              <li>Players see the official merchant's look and name, and read the lines as notices, one per press of the interaction key. Custom names and the game's own dialog window would need a patch pak, which the game does not load (see docs/CUSTOM_NPCS.md).</li>
            </ul>
          </section>
        )}
      </div>
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </main>
  );
}

function ConditionEditor({ value, onChange }: { value: Condition | null; onChange: (value: Condition | null) => void }) {
  if (!value) return <Button size="sm" onClick={() => onChange(noCondition())}>Add conditions</Button>;
  const set = <K extends keyof Condition>(key: K, v: Condition[K]) => onChange({ ...value, [key]: v });
  return (
    <div className="stack">
      <div className="row-between"><span className="small">Plays only when</span>
        <button type="button" className="icon-btn" aria-label="Remove conditions" onClick={() => onChange(null)}>×</button></div>
      <div className="row">
        <label className="small">level</label>
        <input className="input input-sm" type="number" min={0} max={100} aria-label="From level" value={value.minLevel} onChange={e => set('minLevel', num(e.target.value))} />
        <span className="faint">to</span>
        <input className="input input-sm" type="number" min={0} max={100} aria-label="To level (0 any)" value={value.maxLevel} onChange={e => set('maxLevel', num(e.target.value))} />
        <select className="select select-sm" aria-label="Race" value={value.race} onChange={e => set('race', num(e.target.value))}>
          {RACES.map((r, n) => <option key={r} value={n}>{r}</option>)}
        </select>
      </div>
      <div className="row">
        <input className="input input-sm" placeholder="has flag" aria-label="Has flag" value={value.flag ?? ''} onChange={e => set('flag', orNull(e.target.value))} />
        <input className="input input-sm" placeholder="not flag" aria-label="Does not have flag" value={value.noFlag ?? ''} onChange={e => set('noFlag', orNull(e.target.value))} />
      </div>
      <div className="row">
        <input className="input input-sm" type="number" min={0} placeholder="holds item ID" aria-label="Holds item" value={value.itemId || ''} onChange={e => set('itemId', num(e.target.value))} />
        <input className="input input-sm" type="number" min={0} placeholder="count" aria-label="Item count" value={value.itemCount || ''} onChange={e => set('itemCount', num(e.target.value))} />
      </div>
    </div>
  );
}

function ActionsEditor({ actions, types, onChange }: { actions: Action[]; types: string[]; onChange: (actions: Action[]) => void }) {
  const update = (i: number, action: Action) => onChange(actions.map((a, n) => (n === i ? action : a)));
  return (
    <div className="stack">
      <span className="small">When it ends</span>
      {actions.map((a, i) => (
        <div key={i} className="row">
          <span className="small">{ACTION_LABELS[a.type] ?? a.type}</span>
          {a.type === 'give_item' && <>
            <input className="input input-sm" type="number" min={1} placeholder="item ID" aria-label="Item ID" value={a.itemId || ''} onChange={e => update(i, { ...a, itemId: num(e.target.value) })} />
            <input className="input input-sm quantity" type="number" min={1} aria-label="Count" value={a.count} onChange={e => update(i, { ...a, count: num(e.target.value) })} />
          </>}
          {a.type === 'give_kina' && <input className="input input-sm" type="number" min={1} aria-label="Kina" value={a.count} onChange={e => update(i, { ...a, count: num(e.target.value) })} />}
          {(a.type === 'set_flag' || a.type === 'clear_flag') &&
            <input className="input input-sm" placeholder="flag" aria-label="Flag" value={a.flag ?? ''} onChange={e => update(i, { ...a, flag: orNull(e.target.value) })} />}
          {a.type === 'notice' && <input className="input input-sm grow" maxLength={200} aria-label="Notice text" value={a.text ?? ''} onChange={e => update(i, { ...a, text: e.target.value })} />}
          {a.type === 'teleport' && (['mapId', 'x', 'y', 'z'] as const).map(k => (
            <input key={k} className="input input-sm" type="number" placeholder={k === 'mapId' ? 'map' : k} aria-label={k} value={a[k] || ''} onChange={e => update(i, { ...a, [k]: num(e.target.value) })} />
          ))}
          <button type="button" className="icon-btn" aria-label="Remove action" onClick={() => onChange(actions.filter((_, n) => n !== i))}>×</button>
        </div>
      ))}
      {actions.length < 10 && (
        <select className="select select-sm" aria-label="Add an action" value="" onChange={e => { if (e.target.value) onChange([...actions, newAction(e.target.value)]); }}>
          <option value="">Add an action…</option>
          {types.map(t => <option key={t} value={t}>{ACTION_LABELS[t] ?? t}</option>)}
        </select>
      )}
    </div>
  );
}
