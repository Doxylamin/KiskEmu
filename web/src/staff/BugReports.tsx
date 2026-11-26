// Bug reports ("/bug <text>" in chat, docs/BUG_REPORTS.md): every world's reports, newest first, filtered by status, map
// or quest. A report shows what the world server captured at that moment (character, place, quests, the NPCs and objects
// around with their data rows, the connection's last packets) and its screenshot, if the launcher attached one. Staff set
// its status and a note; "Copy as Markdown" copies a self-contained brief for an AI session (the same text as
// GET /api/admin/bug-reports/<id>/markdown).
import { useCallback, useEffect, useState } from 'react';
import { apiBlobUrl } from '../shared/api';
import { Badge, Button, Icon, Status, type StatusMessage } from '../shared/components';
import { describe, type Call } from './StaffApp';
import type { BugReportDetail, BugReportSummary, BugStatus } from './types';

const STATUS: Record<BugStatus, string> = { open: 'Open', in_progress: 'In progress', fixed: 'Fixed', wont_fix: "Won't fix" };
const TONE: Record<BugStatus, 'accent' | 'ok' | 'danger' | undefined> = { open: 'accent', in_progress: 'danger', fixed: 'ok', wont_fix: undefined };
const seconds = (ms: number) => `${(ms / 1000).toFixed(1)} s`;
const firstLine = (text: string) => { const line = text.split('\n')[0]; return line.length > 120 ? line.slice(0, 120) + '…' : line; };

async function copyText(text: string) {
  try { await navigator.clipboard.writeText(text); return; } catch { /* not allowed: the textarea way below */ }
  const area = document.createElement('textarea');
  area.value = text;
  area.style.position = 'fixed';
  area.style.opacity = '0';
  document.body.appendChild(area);
  area.select();
  document.execCommand('copy');
  area.remove();
}

export function BugReports({ call, token, active, reportId, onOpen }:
  { call: Call; token: string; active: boolean; reportId: string | null; onOpen: (id: number | null) => void }) {
  return reportId && /^\d+$/.test(reportId)
    ? <BugReportView key={reportId} call={call} token={token} id={Number(reportId)} onBack={() => onOpen(null)} />
    : <BugReportList call={call} active={active} onOpen={onOpen} />;
}

function BugReportList({ call, active, onOpen }: { call: Call; active: boolean; onOpen: (id: number) => void }) {
  const [status, setStatus] = useState<BugStatus | 'all'>('open');
  const [map, setMap] = useState('');
  const [quest, setQuest] = useState('');
  const [rows, setRows] = useState<BugReportSummary[] | null>(null);
  const [message, setMessage] = useState<StatusMessage | null>(null);

  const load = useCallback(async () => {
    const query = new URLSearchParams();
    if (status !== 'all') query.set('status', status);
    if (/^\d+$/.test(map.trim())) query.set('map', map.trim());
    if (quest.trim()) query.set('quest', quest.trim());
    try { setRows(await call<BugReportSummary[]>('/api/admin/bug-reports?' + query)); setMessage(null); }
    catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
  }, [call, status, map, quest]);
  useEffect(() => { if (active) load(); }, [active, load]);

  return (
    <main className="page stack">
      <div className="row-between wrap">
        <div>
          <h1>Bug reports</h1>
          <p className="muted small">Players report with <code>/bug &lt;what happened&gt;</code> in chat; the server adds their place, quests, nearby NPCs and last packets.</p>
        </div>
        <Button size="sm" variant="ghost" onClick={load}><Icon name="refresh" size={15} /> Refresh</Button>
      </div>
      <div className="row wrap">
        <div className="chips">
          {(['open', 'in_progress', 'fixed', 'wont_fix', 'all'] as const).map(s => (
            <button key={s} type="button" className="chip" aria-selected={status === s} onClick={() => setStatus(s)}>{s === 'all' ? 'All' : STATUS[s]}</button>
          ))}
        </div>
        <input className="input" placeholder="Map id" inputMode="numeric" value={map} onChange={e => setMap(e.target.value)} aria-label="Map id" />
        <input className="input" placeholder="Quest code" value={quest} onChange={e => setQuest(e.target.value)} aria-label="Quest code" />
      </div>
      <Status message={message} />
      <section className="card">
        <div className="table-wrap">
          <table className="data-table">
            <thead><tr><th>#</th><th>Reported</th><th>Character</th><th>Map</th><th>Quests</th><th>Report</th><th>Status</th></tr></thead>
            <tbody>
              {(rows ?? []).map(r => (
                <tr key={r.id} className="clickable" onClick={() => onOpen(r.id)}>
                  <td data-label="#"><a href={`#/bugs/${r.id}`} onClick={e => { e.preventDefault(); onOpen(r.id); }}>#{r.id}</a></td>
                  <td data-label="Reported" className="nowrap">{new Date(r.createdAt).toLocaleString()}</td>
                  <td data-label="Character">{r.characterName ?? '–'} <span className="faint small">{r.accountName ?? ''} · world {r.worldId}</span></td>
                  <td data-label="Map">{r.mapName ?? '–'} <span className="faint small">{r.mapId ?? ''}</span></td>
                  <td data-label="Quests" className="small">{r.quests.slice(0, 3).join(', ')}{r.quests.length > 3 ? ` +${r.quests.length - 3}` : ''}</td>
                  <td data-label="Report">{firstLine(r.text)} {r.screenshotType && <Badge>screenshot</Badge>}</td>
                  <td data-label="Status"><Badge tone={TONE[r.status]}>{STATUS[r.status]}</Badge></td>
                </tr>
              ))}
              {rows && rows.length === 0 && <tr><td colSpan={7} className="empty pad">No bug reports match.</td></tr>}
              {!rows && <tr><td colSpan={7} className="empty pad">Loading…</td></tr>}
            </tbody>
          </table>
        </div>
      </section>
    </main>
  );
}

function BugReportView({ call, token, id, onBack }: { call: Call; token: string; id: number; onBack: () => void }) {
  const [detail, setDetail] = useState<BugReportDetail | null>(null);
  const [status, setStatus] = useState<BugStatus>('open');
  const [note, setNote] = useState('');
  const [shot, setShot] = useState<string | null>(null);
  const [message, setMessage] = useState<StatusMessage | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(async () => {
    try {
      const d = await call<BugReportDetail>(`/api/admin/bug-reports/${id}`);
      setDetail(d);
      setStatus(d.report.status);
      setNote(d.report.staffNote);
    } catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
  }, [call, id]);
  useEffect(() => { load(); }, [load]);
  useEffect(() => {
    if (!detail?.report.screenshotType) return;
    let url: string | null = null;
    apiBlobUrl(`/api/admin/bug-reports/${id}/screenshot`, token).then(u => { url = u; setShot(u); });
    return () => { if (url) URL.revokeObjectURL(url); };
  }, [detail?.report.screenshotType, id, token]);

  async function save() {
    setBusy(true);
    try { await call(`/api/admin/bug-reports/${id}`, { method: 'PUT', body: { status, note } }); setMessage({ text: `Report #${id} saved.`, kind: 'ok' }); load(); }
    catch (e) { setMessage({ text: describe(e), kind: 'error' }); }
    finally { setBusy(false); }
  }
  async function copyMarkdown() {
    try {
      const response = await fetch(`/api/admin/bug-reports/${id}/markdown`, { headers: { Authorization: 'Bearer ' + token }, cache: 'no-store' });
      if (!response.ok) throw new Error(String(response.status));
      await copyText(await response.text());
      setMessage({ text: 'Copied the Markdown brief.', kind: 'ok' });
    } catch { setMessage({ text: 'The Markdown brief could not be copied.', kind: 'error' }); }
  }
  async function copyJson() {
    if (!detail) return;
    await copyText(JSON.stringify(detail, null, 2));
    setMessage({ text: 'Copied the JSON.', kind: 'ok' });
  }

  if (!detail) return <main className="page stack"><Button size="sm" variant="ghost" onClick={onBack}>← Bug reports</Button><Status message={message} />{!message && <p className="muted">Loading…</p>}</main>;
  const { report: r, context: c } = detail;
  const at = c.location;
  return (
    <main className="page stack">
      <div className="row-between wrap">
        <Button size="sm" variant="ghost" onClick={onBack}>← Bug reports</Button>
        <div className="row wrap">
          <Button size="sm" variant="primary" onClick={copyMarkdown}>Copy as Markdown</Button>
          <Button size="sm" onClick={copyJson}>Copy JSON</Button>
        </div>
      </div>
      <header>
        <h1>Bug report #{r.id} <Badge tone={TONE[r.status]}>{STATUS[r.status]}</Badge></h1>
        <p className="muted small">
          {new Date(r.createdAt).toLocaleString()} · {r.characterName ?? '–'} ({c.character.raceName} {c.character.className ?? '?'}, level {c.character.level})
          {' '}· {r.accountName ?? 'unknown account'} · world {r.worldId} · commit <code>{r.commit ?? 'unknown'}</code>
        </p>
      </header>
      <section className="card pad">
        <p className="pre">{r.text}</p>
      </section>
      <Status message={message} />
      <section className="card pad stack">
        <div className="row wrap">
          <label className="field">
            <span className="small muted">Status</span>
            <select className="select" value={status} onChange={e => setStatus(e.target.value as BugStatus)}>
              {(Object.keys(STATUS) as BugStatus[]).map(s => <option key={s} value={s}>{STATUS[s]}</option>)}
            </select>
          </label>
          <label className="field grow">
            <span className="small muted">Staff note</span>
            <input className="input" maxLength={2000} value={note} onChange={e => setNote(e.target.value)} />
          </label>
          <Button variant="primary" busy={busy} onClick={save}>Save</Button>
        </div>
        {r.handledBy && <p className="faint small">Last changed by {r.handledBy}{r.handledAt ? ` on ${new Date(r.handledAt).toLocaleString()}` : ''}.</p>}
      </section>

      <section className="card pad">
        <h2>Where</h2>
        {at ? (
          <p>
            {at.mapName} <span className="faint">({at.mapId}{at.instance ? `, instance ${at.instance}` : ''})</span>
            {' '}· subzone {at.subzoneId}{at.subzone ? ` ${at.subzone}` : ''}{at.area ? ` (${at.area})` : ''}<br />
            x {at.x}, y {at.y}, z {at.z}, heading {at.heading} · <code>//Teleport {at.mapId} {at.x} {at.y} {at.z}</code>
          </p>
        ) : <p className="muted">Not in the world.</p>}
        <p className="faint small">HP {c.character.hp}/{c.character.hpMax}{c.character.dead ? ', dead' : ''}{c.character.inCombat ? ', in combat' : ''}</p>
      </section>

      {shot && <section className="card pad"><h2>Screenshot</h2><img src={shot} alt={`Screenshot of report ${r.id}`} style={{ maxWidth: '100%' }} /></section>}

      <section className="card">
        <h2 className="pad">Quests</h2>
        <div className="table-wrap">
          <table className="data-table">
            <thead><tr><th>Code</th><th>Title</th><th>Status</th><th>Step</th><th>Goals</th><th>Step NPC</th><th>Objectives</th></tr></thead>
            <tbody>
              {c.quests.active.map(q => (
                <tr key={q.code}>
                  <td data-label="Code"><code>{q.code}</code> <span className="faint small">{q.id}</span></td>
                  <td data-label="Title">{q.title ?? '–'}</td>
                  <td data-label="Status">{q.statusName}</td>
                  <td data-label="Step">{q.step}</td>
                  <td data-label="Goals">{q.goals.join(', ')}</td>
                  <td data-label="Step NPC">{q.stepNpc ?? '–'}</td>
                  <td data-label="Objectives" className="small">{q.stepObjectives.map(o => [o.type, o.npc, o.item, o.target, o.count > 1 ? `x${o.count}` : ''].filter(Boolean).join(' ')).join('; ')}</td>
                </tr>
              ))}
              {c.quests.active.length === 0 && <tr><td colSpan={7} className="empty pad">No active quests.</td></tr>}
            </tbody>
          </table>
        </div>
        {c.quests.recentEvents.length > 0 && (
          <div className="pad">
            <h3>Recent quest events</h3>
            <ul className="small">
              {c.quests.recentEvents.map((e, i) => <li key={i}>{seconds(e.agoMs)} before: {e.kind} <code>{e.code}</code> step {e.step}, status {e.status}{e.goals?.length ? `, goals ${e.goals.join(',')}` : ''}</li>)}
            </ul>
          </div>
        )}
        {c.mapEvents.length > 0 && (
          <div className="pad">
            <h3>Map events</h3>
            <ul className="small">{c.mapEvents.map(e => <li key={e.id}>{e.name} (id {e.id}): status {e.status}, stage {e.stage}, step {e.step}, goals {e.goals.join(',')}</li>)}</ul>
          </div>
        )}
      </section>

      <section className="card">
        <h2 className="pad">NPCs within {Math.round(c.nearbyRadius / 100)} m</h2>
        <div className="table-wrap">
          <table className="data-table">
            <thead><tr><th>Key</th><th>NPC</th><th>Dist</th><th>Position</th><th>Heading</th><th>State</th><th>Seen</th><th>Source</th></tr></thead>
            <tbody>
              {c.nearby.map(n => (
                <tr key={n.key}>
                  <td data-label="Key">{n.key}</td>
                  <td data-label="NPC">{n.name} <span className="faint small">{n.npcId} · Lv {n.level}</span></td>
                  <td data-label="Dist">{n.distance}</td>
                  <td data-label="Position" className="nowrap small">{n.x}, {n.y}, {n.z}</td>
                  <td data-label="Heading">{n.heading}</td>
                  <td data-label="State" className="small">{n.dead ? 'dead, ' : ''}{n.hostile ? 'hostile' : 'friendly'}{n.function !== 'None' ? `, ${n.function}` : ''}</td>
                  <td data-label="Seen">{n.seen ? 'yes' : 'no'}</td>
                  <td data-label="Source" className="small">{n.source.kind === 'map_data'
                    ? <><code>{n.source.file}</code> maps["{n.source.map}"][{n.source.index}]{n.source.home ? ` · home ${n.source.home.join(', ')}` : ''}</>
                    : n.source.kind}</td>
                </tr>
              ))}
              {c.nearby.length === 0 && <tr><td colSpan={8} className="empty pad">No NPCs nearby.</td></tr>}
            </tbody>
          </table>
        </div>
        {c.nearbyObjects.length > 0 && (
          <div className="table-wrap">
            <table className="data-table">
              <thead><tr><th>Key</th><th>Object</th><th>Dist</th><th>Position</th><th>Shown</th><th>Row</th></tr></thead>
              <tbody>
                {c.nearbyObjects.map(o => (
                  <tr key={o.key}>
                    <td data-label="Key">{o.key}</td>
                    <td data-label="Object">{o.envName ?? o.name} <span className="faint small">env {o.env}</span></td>
                    <td data-label="Dist">{o.distance}</td>
                    <td data-label="Position" className="nowrap small">{o.x}, {o.y}, {o.z}</td>
                    <td data-label="Shown">{o.shown ? 'yes' : 'no'}</td>
                    <td data-label="Row" className="small"><code>{o.file}</code> maps["{o.map}"][{o.index}]</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>

      <section className="card pad">
        <h2>Last packets <span className="faint small">({c.packets.recent.length}, oldest first)</span></h2>
        <pre className="pre small">{c.packets.recent.map(p =>
          `${seconds(p.agoMs).padStart(8)} ${p.dir} ${p.op} ${(p.name ?? '?').padEnd(28)} ${String(p.length).padStart(6)}B ${p.hex ?? ''}`).join('\n')}</pre>
      </section>
    </main>
  );
}
