// Shop: goods staff add to the in-game shop next to the official ones (the realm's list, read by every world), and the
// purchases made on the picked world (its ledger: NPC, coin and legion shops, the in-game shop, the Daeva Pass, membership).
import { useCallback, useEffect, useState } from 'react';
import { Badge, Button, ConfirmDialog, Icon, Status, type ConfirmRequest, type StatusMessage } from '../ui/components';
import { describe, type Call } from './AdminApp';
import { ERRORS, type CatalogItem, type CatalogPage } from './types';

/** Custom shop goods (/api/admin/shop/goods; the realm's game.custom_shop_goods). */
export interface ShopGoods {
  goodsId: number; name: string; description: string; tabLight: string; tabDark: string; icon: string;
  items: { templateId: number; quantity: number }[]; currency: 'kina' | 'quna' | 'free'; price: number;
  limitCount: number; limitTarget: number; limitPeriod: number; race: number; minLevel: number;
  saleFrom: string | null; saleTo: string | null; enabled: boolean; sortOrder: number; updatedAt: string;
}
interface ShopGoodsList { goods: ShopGoods[]; tabs: { light: string; dark: string; goods: number }[]; icons: string[]; firstId: number }
/** A world's purchase ledger row (/api/admin/shop/orders?world=…). */
interface ShopOrder {
  orderId: number; orderedAt: string; characterId: string; characterName: string; accountId: string;
  kind: string; goodsId: number; quantity: number; currencyItemId: number; price: number;
}

Object.assign(ERRORS, {
  realm_only: 'Shop goods are kept by the auth host. Reload the page and try again.',
  world_only: 'Pick a world server first: each world keeps its own purchases.',
  invalid_name: 'Give the goods a name of up to 80 characters (and a note of up to 400).',
  invalid_tab: 'Pick the shop tab the goods appear in.',
  invalid_items: 'Add 1 to 10 items, each 1 to 1,000,000.',
  invalid_currency: 'Pick Kina, Quna or free.',
  invalid_price: 'Set a price above 0 (or pick free).',
  invalid_limit: 'The purchase limit is 0 to 100,000.',
  invalid_restriction: 'The minimum level is 0 to 100.',
  invalid_sale_window: 'The sale must end after it starts.',
  invalid_goods_id: 'These goods cannot be changed here.',
  shop_full: 'There are no free custom goods ids left.',
  no_catalogue: 'No item catalogue has been imported yet.',
});

const KINA = 930100001, QUNA = 535300006;
const RESETS = ['never resets', 'resets daily', 'resets weekly', 'resets monthly'];
const RACES = ['Both races', 'Light only', 'Dark only'];
const KINDS: Record<string, string> = { npc: 'NPC shop', coin: 'Coin shop', guild: 'Legion shop', bm: 'In-game shop', custom: 'Custom goods', pass: 'Daeva Pass', membership: 'Membership' };

type Draft = Omit<ShopGoods, 'updatedAt' | 'items'> & { items: { templateId: number; quantity: number; name?: string }[] };

function blank(tabs: ShopGoodsList['tabs']): Draft {
  const tab = tabs.find(t => t.light === 'Shop_Usable') ?? tabs[0];
  return { goodsId: 0, name: '', description: '', tabLight: tab?.light ?? 'Shop_Usable', tabDark: tab?.dark ?? 'D_Shop_Usable', icon: '', items: [],
    currency: 'kina', price: 1000, limitCount: 0, limitTarget: 1, limitPeriod: 0, race: 0, minLevel: 0, saleFrom: null, saleTo: null, enabled: true, sortOrder: 0 };
}

/** datetime-local value (local time) of an ISO time, and back. */
function toLocal(iso: string | null) {
  if (!iso) return '';
  const d = new Date(iso);
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}
const fromLocal = (value: string) => (value ? new Date(value).toISOString() : null);

function priceText(g: { currency: string; price: number }) {
  return g.currency === 'free' ? 'Free' : `${g.price.toLocaleString()} ${g.currency === 'quna' ? 'Quna' : 'Kina'}`;
}
function limitText(g: { limitCount: number; limitTarget: number; limitPeriod: number }) {
  if (g.limitCount <= 0) return 'No limit';
  return `${g.limitCount} per ${g.limitTarget === 2 ? 'account' : 'character'}${g.limitPeriod ? `, ${RESETS[g.limitPeriod]}` : ''}`;
}
function onSale(g: ShopGoods) {
  const now = Date.now();
  return g.enabled && (!g.saleFrom || Date.parse(g.saleFrom) <= now) && (!g.saleTo || now < Date.parse(g.saleTo));
}
const clampInt = (value: string, min: number, max: number) => Math.max(min, Math.min(max, Math.floor(Number(value) || 0)));

export function Shop({ call, active, isAdmin, worldName }: { call: Call; active: boolean; isAdmin: boolean; worldName: string | null }) {
  const [list, setList] = useState<ShopGoodsList | null>(null);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [query, setQuery] = useState('');
  const [found, setFound] = useState<CatalogItem[]>([]);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState<StatusMessage | null>(null);
  const [confirm, setConfirm] = useState<ConfirmRequest | null>(null);
  const [orders, setOrders] = useState<ShopOrder[] | null>(null);
  const [character, setCharacter] = useState('');
  const [kind, setKind] = useState('');
  const [ordersError, setOrdersError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try { setList(await call<ShopGoodsList>('/api/admin/shop/goods')); } catch (e) { setStatus({ text: describe(e), kind: 'error' }); }
  }, [call]);
  const loadOrders = useCallback(async () => {
    try {
      const q = new URLSearchParams({ limit: '100' });
      if (character.trim()) q.set('character', character.trim());
      if (kind) q.set('kind', kind);
      setOrders(await call<ShopOrder[]>('/api/admin/shop/orders?' + q));
      setOrdersError(null);
    } catch (e) { setOrders(null); setOrdersError(describe(e)); }
  }, [call, character, kind]);
  useEffect(() => { if (active) load(); }, [active, load]);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { if (active) loadOrders(); }, [active, call, kind]);

  useEffect(() => {
    if (query.trim().length < 2) { setFound([]); return; }
    const t = window.setTimeout(() => call<CatalogPage>('/api/admin/items?' + new URLSearchParams({ q: query.trim(), limit: '12' }))
      .then(p => setFound(p.items)).catch(() => setFound([])), 250);
    return () => clearTimeout(t);
  }, [call, query]);

  // The list keeps item ids only: their names for the editor.
  async function edit(goods: ShopGoods) {
    setStatus(null);
    setDraft({ ...goods, items: goods.items.map(i => ({ ...i })) });
    const named = await Promise.all(goods.items.map(i => call<CatalogPage>('/api/admin/items?' + new URLSearchParams({ q: String(i.templateId), limit: '1' }))
      .then(p => p.items.find(x => x.templateId === i.templateId)?.name).catch(() => undefined)));
    setDraft(d => d && d.goodsId === goods.goodsId ? { ...d, items: d.items.map((i, n) => ({ ...i, name: named[n] ?? i.name })) } : d);
  }

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => setDraft(d => d && { ...d, [key]: value });

  async function save() {
    if (!draft) return;
    setBusy(true);
    try {
      const body = { ...draft, items: draft.items.map(i => ({ templateId: i.templateId, quantity: i.quantity })), price: draft.currency === 'free' ? 0 : draft.price };
      const saved = await call<ShopGoods>(draft.goodsId ? `/api/admin/shop/goods/${draft.goodsId}` : '/api/admin/shop/goods',
        { method: draft.goodsId ? 'PUT' : 'POST', body });
      setStatus({ text: `Saved “${saved.name}” (goods ${saved.goodsId}). Players see it the next time they open the shop.`, kind: 'ok' });
      setDraft(null);
      await load();
    } catch (e) { setStatus({ text: describe(e), kind: 'error' }); }
    finally { setBusy(false); }
  }

  async function remove(goods: ShopGoods) {
    try {
      await call(`/api/admin/shop/goods/${goods.goodsId}`, { method: 'DELETE' });
      setStatus({ text: `Removed “${goods.name}”.`, kind: 'ok' });
      if (draft?.goodsId === goods.goodsId) setDraft(null);
      await load();
    } catch (e) { setStatus({ text: describe(e), kind: 'error' }); }
  }

  const goodsName = (id: number) => list?.goods.find(g => g.goodsId === id)?.name;
  const tabKey = (light: string, dark: string) => `${light}|${dark}`;
  const ready = !!draft && draft.name.trim().length > 0 && draft.items.length > 0 && (draft.currency === 'free' || draft.price > 0);

  return (
    <main className="page">
      <header className="page-head">
        <div>
          <h1>Shop</h1>
          <p className="muted">Goods you add to the in-game shop next to the official ones, and what players bought.</p>
        </div>
        {isAdmin && <Button variant="primary" size="sm" disabled={!list} onClick={() => { setStatus(null); setDraft(blank(list?.tabs ?? [])); }}>
          <Icon name="plus" size={16} /> New goods</Button>}
      </header>
      <Status message={status} />
      <div className="broadcast-grid">
        <section className="card">
          <div className="panel-head"><h2>Custom goods</h2><Button size="sm" onClick={load}><Icon name="refresh" size={16} /> Refresh</Button></div>
          <div className="table-wrap">
            <table className="data-table">
              <thead><tr><th>Goods</th><th>Price</th><th>Limit</th><th>State</th>{isAdmin && <th aria-label="Actions" />}</tr></thead>
              <tbody>
                {list?.goods.map(g => (
                  <tr key={g.goodsId}>
                    <td>{g.name}<span className="faint small block">{g.goodsId} · {g.tabLight} · {g.items.length} item{g.items.length === 1 ? '' : 's'}</span></td>
                    <td className="mono">{priceText(g)}</td>
                    <td className="small">{limitText(g)}</td>
                    <td>{onSale(g) ? <Badge tone="ok">On sale</Badge> : g.enabled ? <Badge>Not now</Badge> : <Badge tone="danger">Hidden</Badge>}</td>
                    {isAdmin && <td className="actions">
                      <button type="button" className="icon-btn" aria-label={`Edit ${g.name}`} onClick={() => edit(g)}><Icon name="edit" size={16} /></button>
                      <button type="button" className="icon-btn" aria-label={`Remove ${g.name}`} onClick={() => setConfirm({ title: `Remove “${g.name}”?`, confirmLabel: 'Remove', danger: true,
                        body: <p>It leaves the shop at once. Purchases already made stay in the worlds' purchase lists.</p>, onConfirm: () => remove(g) })}>
                        <Icon name="trash" size={16} /></button>
                    </td>}
                  </tr>
                ))}
                {list && list.goods.length === 0 && <tr><td colSpan={isAdmin ? 5 : 4} className="empty pad">No custom goods yet.{isAdmin ? ' Add some with “New goods”.' : ''}</td></tr>}
              </tbody>
            </table>
          </div>
        </section>

        {draft ? (
          <section className="card card-body stack">
            <h2>{draft.goodsId ? `Edit goods ${draft.goodsId}` : 'New goods'}</h2>
            <div className="field"><label htmlFor="sg-name">Name in the shop</label>
              <input id="sg-name" className="input" maxLength={80} value={draft.name} onChange={e => set('name', e.target.value)} /></div>
            <div className="field"><label htmlFor="sg-desc">Note for staff (optional)</label>
              <input id="sg-desc" className="input" maxLength={400} value={draft.description} onChange={e => set('description', e.target.value)} /></div>
            <div className="field">
              <label htmlFor="sg-tab">Shop tab</label>
              <select id="sg-tab" className="select" value={tabKey(draft.tabLight, draft.tabDark)}
                onChange={e => { const [light, dark] = e.target.value.split('|'); setDraft({ ...draft, tabLight: light, tabDark: dark }); }}>
                {!list?.tabs.some(t => tabKey(t.light, t.dark) === tabKey(draft.tabLight, draft.tabDark)) &&
                  <option value={tabKey(draft.tabLight, draft.tabDark)}>{draft.tabLight}</option>}
                {list?.tabs.map(t => <option key={tabKey(t.light, t.dark)} value={tabKey(t.light, t.dark)}>{t.light} ({t.goods} official goods)</option>)}
              </select>
            </div>
            <div className="field">
              <label htmlFor="sg-icon">Shop icon (optional)</label>
              <input id="sg-icon" className="input" list="sg-icons" maxLength={128} value={draft.icon} placeholder="Icon_BMShop_…" onChange={e => set('icon', e.target.value)} />
              <datalist id="sg-icons">{list?.icons.map(i => <option key={i} value={i} />)}</datalist>
            </div>
            <div className="field">
              <label htmlFor="sg-search">Items it gives</label>
              <input id="sg-search" className="input" type="search" placeholder="Item name or ID" value={query} onChange={e => setQuery(e.target.value)} />
              {found.length > 0 && (
                <ul className="pick-list">
                  {found.map(f => (
                    <li key={f.templateId}><button type="button" className="mob-choice" disabled={draft.items.length >= 10} onClick={() => {
                      set('items', draft.items.some(i => i.templateId === f.templateId) ? draft.items : [...draft.items, { templateId: f.templateId, quantity: 1, name: f.name }]);
                      setQuery('');
                    }}><span>{f.name}</span><span className="faint small">{f.templateId}{f.grade ? ` · ${f.grade}` : ''}</span></button></li>
                  ))}
                </ul>
              )}
            </div>
            <ul className="reward-items">
              {draft.items.map(i => (
                <li key={i.templateId}>
                  <span className="grow truncate">{i.name ?? 'Item'} <span className="faint small">{i.templateId}</span></span>
                  <input className="input input-sm quantity" type="number" min={1} max={1000000} value={i.quantity} aria-label="Quantity"
                    onChange={e => set('items', draft.items.map(x => x === i ? { ...x, quantity: clampInt(e.target.value, 1, 1000000) } : x))} />
                  <button type="button" className="icon-btn" aria-label="Remove" onClick={() => set('items', draft.items.filter(x => x !== i))}>×</button>
                </li>
              ))}
            </ul>
            <div className="field">
              <label>Price</label>
              <div className="chips" role="tablist">
                {(['kina', 'quna', 'free'] as const).map(c => (
                  <button key={c} type="button" role="tab" className="chip" aria-selected={draft.currency === c} onClick={() => set('currency', c)}>
                    {c === 'kina' ? 'Kina' : c === 'quna' ? 'Quna' : 'Free'}</button>
                ))}
              </div>
              {draft.currency !== 'free' && <input className="input" type="number" min={1} aria-label="Price" value={draft.price}
                onChange={e => set('price', clampInt(e.target.value, 0, 10_000_000_000))} />}
            </div>
            <div className="field">
              <label htmlFor="sg-limit">Purchase limit (0 for none)</label>
              <div className="row">
                <input id="sg-limit" className="input input-sm" type="number" min={0} max={100000} value={draft.limitCount}
                  onChange={e => set('limitCount', clampInt(e.target.value, 0, 100000))} />
                <select className="select select-sm" aria-label="Limit per" value={draft.limitTarget} disabled={draft.limitCount === 0}
                  onChange={e => set('limitTarget', Number(e.target.value))}>
                  <option value={1}>per character</option><option value={2}>per account</option>
                </select>
                <select className="select select-sm" aria-label="Limit resets" value={draft.limitPeriod} disabled={draft.limitCount === 0}
                  onChange={e => set('limitPeriod', Number(e.target.value))}>
                  {RESETS.map((r, n) => <option key={r} value={n}>{r}</option>)}
                </select>
              </div>
            </div>
            <div className="field">
              <label htmlFor="sg-race">Who can buy it</label>
              <div className="row">
                <select id="sg-race" className="select select-sm" value={draft.race} onChange={e => set('race', Number(e.target.value))}>
                  {RACES.map((r, n) => <option key={r} value={n}>{r}</option>)}
                </select>
                <label className="small" htmlFor="sg-level">from level</label>
                <input id="sg-level" className="input input-sm" type="number" min={0} max={100} value={draft.minLevel}
                  onChange={e => set('minLevel', clampInt(e.target.value, 0, 100))} />
              </div>
            </div>
            <div className="field">
              <label>On sale (empty: always)</label>
              <div className="row">
                <input className="input input-sm" type="datetime-local" aria-label="Sale starts" value={toLocal(draft.saleFrom)} onChange={e => set('saleFrom', fromLocal(e.target.value))} />
                <span className="faint">to</span>
                <input className="input input-sm" type="datetime-local" aria-label="Sale ends" value={toLocal(draft.saleTo)} onChange={e => set('saleTo', fromLocal(e.target.value))} />
              </div>
            </div>
            <div className="field"><label htmlFor="sg-order">Order in the tab (lower first)</label>
              <input id="sg-order" className="input input-sm" type="number" value={draft.sortOrder} onChange={e => set('sortOrder', clampInt(e.target.value, -100000, 100000))} /></div>
            <label className="check"><input type="checkbox" checked={draft.enabled} onChange={e => set('enabled', e.target.checked)} /><span>Show it in the shop</span></label>
            <div className="row-between">
              <Button onClick={() => setDraft(null)}>Cancel</Button>
              <Button variant="primary" busy={busy} disabled={!ready} onClick={save}>Save goods</Button>
            </div>
          </section>
        ) : (
          <section className="card card-body stack">
            <h2>How it works</h2>
            <ul className="muted help-list">
              <li>Custom goods appear in the in-game shop, in the tab you pick, on every world.</li>
              <li>Players pay Kina from their bag and Quna from their account's wallet (shared by every world). The server's premium setting can make Quna goods cost Kina, be free, or be hidden.</li>
              <li>Limits count per character or per account on the buyer's world, and reset at the game's daily, weekly or monthly reset.</li>
              <li>Changes reach the worlds within a minute; players see them the next time they open the shop.</li>
              <li>The icon is one of the client's shop icons; leave it empty if none fits.</li>
            </ul>
          </section>
        )}

        <section className="card span-2">
          <div className="panel-head">
            <h2>Purchases{worldName ? ` on ${worldName}` : ''}</h2>
            <form className="row" onSubmit={e => { e.preventDefault(); loadOrders(); }}>
              <input className="input input-sm" type="search" placeholder="Character" aria-label="Character" value={character} onChange={e => setCharacter(e.target.value)} />
              <select className="select select-sm" aria-label="Shop" value={kind} onChange={e => setKind(e.target.value)}>
                <option value="">All shops</option>
                {Object.entries(KINDS).map(([k, label]) => <option key={k} value={k}>{label}</option>)}
              </select>
              <Button size="sm" type="submit"><Icon name="refresh" size={16} /> Show</Button>
            </form>
          </div>
          <Status message={ordersError ? { text: ordersError, kind: 'error' } : null} />
          <div className="table-wrap">
            <table className="data-table">
              <thead><tr><th>When</th><th>Character</th><th>Shop</th><th>Goods</th><th className="num">Qty</th><th className="num">Paid</th></tr></thead>
              <tbody>
                {orders?.map(o => (
                  <tr key={o.orderId}>
                    <td className="small">{new Date(o.orderedAt).toLocaleString()}</td>
                    <td>{o.characterName || o.characterId}</td>
                    <td className="small">{KINDS[o.kind] ?? o.kind}</td>
                    <td>{(o.kind === 'custom' && goodsName(o.goodsId)) || <span className="mono">{o.goodsId}</span>}</td>
                    <td className="num mono">{o.quantity.toLocaleString()}</td>
                    <td className="num mono">{o.price === 0 ? 'Free'
                      : `${o.price.toLocaleString()} ${o.currencyItemId === KINA ? 'Kina' : o.currencyItemId === QUNA ? 'Quna' : `× item ${o.currencyItemId}`}`}</td>
                  </tr>
                ))}
                {orders && orders.length === 0 && <tr><td colSpan={6} className="empty pad">No purchases recorded yet.</td></tr>}
              </tbody>
            </table>
          </div>
        </section>
      </div>
      <ConfirmDialog request={confirm} onClose={() => setConfirm(null)} />
    </main>
  );
}
