// A signed-in session kept in the browser so a page refresh does not sign out. "Keep me signed in" keeps it in
// localStorage (the server issues a 30-day session); otherwise sessionStorage keeps it until the tab closes.
// Storage can be blocked (private mode, site data off): then the user signs in on each page load.
export interface SavedSession { token: string; username: string }

export function readSession(key: string): SavedSession | null {
  for (const store of [() => window.localStorage, () => window.sessionStorage]) {
    try {
      const raw = store().getItem(key);
      if (raw) {
        const saved = JSON.parse(raw) as SavedSession;
        if (typeof saved.token === 'string' && typeof saved.username === 'string') return saved;
      }
    } catch { /* storage blocked or bad JSON: not signed in */ }
  }
  return null;
}

/** Saves (or with null, forgets) the session; remember picks localStorage over sessionStorage. */
export function writeSession(key: string, saved: SavedSession | null, remember = false) {
  for (const [store, keep] of [[() => window.localStorage, remember], [() => window.sessionStorage, !remember]] as const) {
    try {
      if (saved && keep) store().setItem(key, JSON.stringify(saved));
      else store().removeItem(key);
    } catch { /* ignore */ }
  }
}
