import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import type { DashboardEvent, DashboardTodo, DashboardView } from './api';
import { ItemLink } from './ItemLink';
import { Panel } from './Split';

function EventText({ e }: { e: DashboardEvent }) {
  const { t } = useTranslation();
  switch (e.kind) {
    case 'subscription_renewal': return <>{t('dashboard.upcoming.subscription_renewal')}</>;
    case 'subscription_expiry': return <>{t('dashboard.upcoming.subscription_expiry')}</>;
    case 'income_phase_end': return <>{t('dashboard.upcoming.income_phase_end', { label: e.label ?? '' })}</>;
    default: return null;
  }
}

/** Spec §3: the next 60 days, sorted by the server. */
export function Upcoming({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  return (
    <Panel title={t('dashboard.upcoming.title')}>
      <p className="text-xs text-muted-foreground">{t('dashboard.upcoming.window')}</p>
      {d.upcoming.length === 0 ? <p className="text-muted-foreground">{t('dashboard.upcoming.empty')}</p> : (
        <ul className="space-y-2 text-sm">
          {d.upcoming.map((e) => (
            <li key={`${e.kind}-${e.id}-${e.on}-${e.label ?? ''}`} className="flex flex-wrap gap-x-2">
              <span className="tabular-nums">{formatLocalDate(e.on)}</span>
              <ItemLink module={e.module} id={e.id}>{e.name}</ItemLink>
              <span><EventText e={e} /></span>
              {e.amount ? <span className="ml-auto tabular-nums">{formatMoney(e.amount)}</span> : null}
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

function TodoText({ item }: { item: DashboardTodo }) {
  const { t } = useTranslation();
  const date = item.on ? formatLocalDate(item.on) : '';
  switch (item.kind) {
    case 'stale_value': return <>{t('dashboard.todo.stale_value', { name: item.name, date })}</>;
    case 'no_value': return <>{t('dashboard.todo.no_value', { name: item.name })}</>;
    case 'income_gap_soon': return <>{t('dashboard.todo.income_gap_soon', { label: item.label ?? '', name: item.name, date })}</>;
    default: return null;
  }
}

/** Spec §3: what needs doing; the month's routine (2E) and proposals (stage 4) join later. */
export function Todo({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  return (
    <Panel title={t('dashboard.todo.title')}>
      {d.todo.length === 0 ? <p className="text-muted-foreground">{t('dashboard.todo.empty')}</p> : (
        <ul className="space-y-2 text-sm">
          {d.todo.map((item) => (
            <li key={`${item.kind}-${item.id}-${item.label ?? ''}`}>
              <ItemLink module={item.module} id={item.id}><TodoText item={item} /></ItemLink>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}
