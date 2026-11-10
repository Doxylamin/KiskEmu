// The server's name (Aion:Web:SiteName, from GET /api/status), so renaming the server needs no new web build.
import { useEffect, useState } from 'react';
import { api } from './api';

export const DEFAULT_SITE_NAME = 'KiskEmu';

// One request per page load, shared by every component.
let loading: Promise<string> | null = null;
let known: string | null = null;

export function useSiteName(): string {
  const [name, setName] = useState(known ?? DEFAULT_SITE_NAME);
  useEffect(() => {
    let live = true;
    loading ??= api<{ siteName?: string }>('/api/status')
      .then(status => (known = status.siteName?.trim() || DEFAULT_SITE_NAME))
      .catch(() => { loading = null; return DEFAULT_SITE_NAME; });
    loading.then(value => { if (live) setName(value); });
    return () => { live = false; };
  }, []);
  return name;
}

/** The brand mark's letter: the name's first letter or digit. */
export const siteMark = (name: string) => (name.match(/[\p{L}\p{N}]/u)?.[0] ?? 'A').toUpperCase();

/** "Page · Site" for the browser tab. */
export const pageTitle = (page: string, site: string) => (page ? `${page} · ${site}` : site);
