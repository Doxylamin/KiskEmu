// One of the account's characters (#/characters/<id>): class, level, world, in the game or when last seen, where it is
// (live while in the game, else where it was last saved), combat power and legion. GET /api/characters/<id>/details
// answers for the signed-in account's own characters only.
import { useEffect, useState } from 'react';
import { api, ApiError } from '../shared/api';
import { Badge } from '../shared/components';
import { EmptyState, Facts, PageHeader, Section, StatTile, formatDate, formatNumber } from '../shared/layout';
import { pageTitle, useSiteName } from '../shared/site';
import { useWorldNames } from '../shared/worlds';

interface Location { mapId: number; mapName: string; area: string | null; x: number | null; y: number | null; z: number | null }
interface Legion { id: number; name: string; level: number; members: number; leader: boolean }
export interface CharacterDetails {
  characterId: string; name: string; worldId: number; worldName: string | null; level: number; race: number; gender: number | null;
  className: string | null; ascension: number | null; experience: number | null; online: boolean; inWorldSince: string | null;
  combatPower: number | null; highestCombatPower: number | null; location: Location | null; locationLive: boolean;
  legion: Legion | null; createdAt: string | null; lastPlayedAt: string | null; source: 'world' | 'index';
}

const RACE: Record<number, string> = { 1: 'Elyos', 2: 'Asmodian' };

/** "3 h 20 min", "12 min", "less than a minute". */
function duration(ms: number): string {
  const minutes = Math.floor(ms / 60_000);
  if (minutes < 1) return 'less than a minute';
  const days = Math.floor(minutes / 1440), hours = Math.floor(minutes % 1440 / 60), rest = minutes % 60;
  if (days > 0) return hours > 0 ? `${days} d ${hours} h` : `${days} d`;
  if (hours > 0) return rest > 0 ? `${hours} h ${rest} min` : `${hours} h`;
  return `${rest} min`;
}

function since(value: string | null, now: number): string | null {
  if (!value) return null;
  const at = new Date(value).getTime();
  return Number.isNaN(at) ? null : duration(Math.max(0, now - at));
}

/** A position in the world in metres (the server counts centimetres). */
function metres(value: number | null): string {
  return value === null ? '—' : (value / 100).toLocaleString(undefined, { maximumFractionDigits: 0 });
}

export function CharacterPage({ id, token, onSignedOut }: { id: string; token: string; onSignedOut: (text: string) => void }) {
  const [details, setDetails] = useState<CharacterDetails | null>(null);
  const [failure, setFailure] = useState<'missing' | 'error' | null>(null);
  const [now, setNow] = useState(() => Date.now());
  const worldName = useWorldNames();
  const site = useSiteName();

  // Asked again every 30 s, so the place and "in the game" stay current while the page is open.
  useEffect(() => {
    let live = true;
    setDetails(null); setFailure(null);
    const load = () => api<CharacterDetails>(`/api/characters/${encodeURIComponent(id)}/details`, { token })
      .then(d => { if (live) { setDetails(d); setFailure(null); setNow(Date.now()); } })
      .catch(error => {
        if (!live) return;
        if (error instanceof ApiError && error.status === 401) onSignedOut('Your session ended. Sign in again.');
        else if (error instanceof ApiError && (error.status === 404 || error.status === 400)) setFailure('missing');
        else setFailure(f => f ?? 'error');
      });
    load();
    const timer = window.setInterval(load, 30_000);
    return () => { live = false; window.clearInterval(timer); };
  }, [id, token, onSignedOut]);
  useEffect(() => {
    document.title = pageTitle(details?.name ?? 'Character', site);
  }, [details?.name, site]);

  const back = <a className="btn btn-secondary" href="#/characters">All characters</a>;
  if (failure === 'missing')
    return (
      <>
        <PageHeader eyebrow="Character" title="Character not found" actions={back} />
        <Section><EmptyState title="This character is not on your account.">It may have been deleted or renamed in the game.</EmptyState></Section>
      </>
    );
  if (!details)
    return (
      <>
        <PageHeader eyebrow="Character" title="Character" actions={back} />
        <Section><p className="muted">{failure ? 'The character could not be loaded. Try again in a moment.' : 'Loading…'}</p></Section>
      </>
    );

  const d = details;
  const world = d.worldName ?? worldName(d.worldId);
  const sub = [`Level ${d.level}`, d.className, RACE[d.race], world].filter(Boolean).join(' · ');
  const inGameFor = d.online ? since(d.inWorldSince, now) : null;
  const lastSeen = since(d.lastPlayedAt, now);
  const place = d.location;

  return (
    <>
      <PageHeader eyebrow="Character" title={<span className="row wrap character-title">{d.name}
          {d.online ? <Badge tone="ok">In game</Badge> : <Badge>Offline</Badge>}</span>}
        sub={sub} actions={back} />

      {d.source === 'index' && (
        <p className="notice small">The world server is not answering right now, so this page shows what the account server last heard.</p>
      )}

      <div className="stat-grid">
        <StatTile label="Level" value={d.level} note={d.className ?? undefined} />
        <StatTile label="Combat power" value={d.combatPower === null ? '—' : formatNumber(d.combatPower)}
          note={d.combatPower === null ? 'Shown while you are in the game.'
            : d.highestCombatPower !== null && d.highestCombatPower > d.combatPower ? `Highest ${formatNumber(d.highestCombatPower)}` : undefined} />
        <StatTile label={d.online ? 'In the game' : 'Last seen'} tone={d.online ? 'ok' : undefined}
          value={d.online ? (inGameFor ?? 'Now') : lastSeen ? `${lastSeen} ago` : '—'}
          note={d.online ? (d.inWorldSince ? `Since ${formatDate(d.inWorldSince, true)}` : undefined)
            : d.lastPlayedAt ? formatDate(d.lastPlayedAt, true) : undefined} />
      </div>

      <Section title={d.locationLive ? 'Where it is now' : 'Last saved location'}
        actions={d.locationLive ? <Badge tone="ok">Live</Badge> : undefined}>
        {place
          ? <div className="stack">
              <div>
                <p className="location-map">{place.mapName}</p>
                {place.area && <p className="muted">{place.area}</p>}
              </div>
              <Facts items={[
                { label: 'World', value: world ?? '—' },
                { label: 'Map', value: <span className="mono">{place.mapId}</span> },
                { label: 'Position (m)', value: place.x === null ? '—'
                    : <span className="mono">X {metres(place.x)} · Y {metres(place.y)} · Z {metres(place.z)}</span> },
              ]} />
              {!d.locationLive && <p className="muted small">{d.online ? 'Updated when the game saves the character.' : 'Where the character was when it last left the game.'}</p>}
            </div>
          : <p className="muted">No location recorded yet.</p>}
      </Section>

      <Section title="Legion">
        {d.legion
          ? <Facts items={[
              { label: 'Name', value: d.legion.name },
              { label: 'Legion level', value: d.legion.level },
              { label: 'Members', value: formatNumber(d.legion.members) },
              { label: 'Rank', value: d.legion.leader ? 'Leader' : 'Member' },
            ]} />
          : <p className="muted">{d.source === 'index' ? 'Shown when the world server answers.' : 'Not in a legion.'}</p>}
      </Section>

      <Section title="Details">
        <Facts items={[
          { label: 'Class', value: d.className ?? '—' },
          { label: 'Race', value: RACE[d.race] ?? '—' },
          { label: 'World', value: world ?? '—' },
          { label: 'Experience', value: formatNumber(d.experience) },
          { label: 'Created', value: formatDate(d.createdAt) },
          { label: 'Last played', value: d.online ? 'Now' : formatDate(d.lastPlayedAt, true) },
        ]} />
      </Section>
    </>
  );
}
