// World names (GET /api/worlds) for showing which world (game server) a character is on.
import { useEffect, useState } from 'react';
import { api } from '../shared/api';

// One request per page load, shared by every component.
let loading: Promise<Map<number, string>> | null = null;

export function useWorldNames(): (worldId: number | undefined) => string | null {
  const [names, setNames] = useState<Map<number, string>>(new Map());
  useEffect(() => {
    let live = true;
    loading ??= api<{ worldId: number; name: string }[]>('/api/worlds')
      .then(list => new Map(list.map(w => [w.worldId, w.name])))
      .catch(() => { loading = null; return new Map(); });   // names are optional
    loading.then(map => { if (live) setNames(map); });
    return () => { live = false; };
  }, []);
  return worldId => (worldId === undefined ? null : names.get(worldId) ?? `World ${worldId}`);
}
