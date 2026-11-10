// A small hash router (no dependency): views get addresses like #/accounts/<id>, so a refresh or Back keeps the page.
// The hash never reaches the server, so the host needs no route for any of them.
import { useCallback, useEffect, useState } from 'react';

/** The route's segments: "#/accounts/abc" is ["accounts", "abc"], "" or "#/" is []. */
export function parseHash(hash: string): string[] {
  return hash.replace(/^#\/?/, '').split('/').filter(Boolean).map(part => {
    try { return decodeURIComponent(part); } catch { return part; }
  });
}

export function hashOf(...segments: string[]): string {
  return '#/' + segments.map(encodeURIComponent).join('/');
}

/** The current route and a navigate(...segments) that pushes a history entry (replace: true does not). */
export function useRoute(): [string[], (segments: string[], options?: { replace?: boolean }) => void] {
  const [route, setRoute] = useState(() => parseHash(window.location.hash));
  useEffect(() => {
    const changed = () => setRoute(parseHash(window.location.hash));
    window.addEventListener('hashchange', changed);
    return () => window.removeEventListener('hashchange', changed);
  }, []);
  const navigate = useCallback((segments: string[], options: { replace?: boolean } = {}) => {
    const target = hashOf(...segments);
    if (target === window.location.hash) return;
    if (options.replace) { window.history.replaceState(null, '', target); setRoute(segments); }
    else window.location.hash = target;
  }, []);
  return [route, navigate];
}
