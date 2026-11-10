// Page building blocks shared by both sites (styles: layout.css).
import type { ReactNode } from 'react';
import { hashOf } from './router';

export function PageHeader({ eyebrow, title, sub, actions, children }:
  { eyebrow?: string; title: ReactNode; sub?: ReactNode; actions?: ReactNode; children?: ReactNode }) {
  return (
    <header className="page-header">
      <div className="page-header-text">
        {eyebrow && <p className="eyebrow">{eyebrow}</p>}
        <h1>{title}</h1>
        {sub && <p className="muted">{sub}</p>}
        {children}
      </div>
      {actions && <div className="page-header-actions">{actions}</div>}
    </header>
  );
}

/** A titled card; `flush` drops the body padding (tables). */
export function Section({ title, actions, flush, className = '', children, id }:
  { title?: ReactNode; actions?: ReactNode; flush?: boolean; className?: string; children: ReactNode; id?: string }) {
  return (
    <section className={'card section ' + className} aria-labelledby={title && id ? id : undefined}>
      {(title || actions) && (
        <div className="section-head">
          {title && <h2 id={id}>{title}</h2>}
          {actions && <div className="row wrap">{actions}</div>}
        </div>
      )}
      <div className={flush ? '' : 'section-body'}>{children}</div>
    </section>
  );
}

export function StatTile({ label, value, note, tone }: { label: string; value: ReactNode; note?: ReactNode; tone?: 'ok' | 'warn' | 'danger' }) {
  return (
    <div className="card stat-tile">
      <p className="stat-tile-label">{label}</p>
      <p className={'stat-tile-value' + (tone ? ' tone-' + tone : '')}>{value}</p>
      {note && <p className="stat-tile-note">{note}</p>}
    </div>
  );
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="empty-state">
      <p className="empty-state-title">{title}</p>
      {children && <div className="muted">{children}</div>}
    </div>
  );
}

/** Label/value pairs in a responsive grid. */
export function Facts({ items }: { items: { label: string; value: ReactNode }[] }) {
  return (
    <dl className="facts-grid">
      {items.map(item => <div key={item.label}><dt>{item.label}</dt><dd>{item.value}</dd></div>)}
    </dl>
  );
}

export interface NavItem { id: string; label: string; icon?: ReactNode; badge?: ReactNode }
export interface NavGroup { name?: string; items: NavItem[] }

/** A side menu of hash links in named groups; `current` is the active item's id. */
export function SideNav({ groups, current, label }: { groups: NavGroup[]; current: string; label: string }) {
  return (
    <nav className="side-nav" aria-label={label}>
      {groups.filter(group => group.items.length > 0).map((group, index) => (
        <div className="side-nav-group" key={group.name ?? index}>
          {group.name && <p className="side-nav-heading">{group.name}</p>}
          {group.items.map(item => (
            <a key={item.id} href={hashOf(...item.id.split('/'))} className="side-nav-item" aria-current={item.id === current ? 'page' : undefined}>
              {item.icon}<span className="grow">{item.label}</span>{item.badge}
            </a>
          ))}
        </div>
      ))}
    </nav>
  );
}

export function formatDate(value: string | null | undefined, withTime = false): string {
  if (!value) return '—';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '—';
  return withTime
    ? date.toLocaleString(undefined, { day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
    : date.toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' });
}

export function formatNumber(value: number | null | undefined): string {
  return value === null || value === undefined ? '—' : value.toLocaleString();
}
