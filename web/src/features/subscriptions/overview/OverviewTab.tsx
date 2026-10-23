import { Link } from '@tanstack/react-router';
import { lazy, Suspense, useId, type ReactNode } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { formatMoney, Money, type MoneyValue } from '@/components/Money';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ProviderIcon } from '@/components/ProviderIcon';
import { buttonVariants } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { personColor } from '@/features/dashboard/colors';
import { toPlot } from '@/features/investments/chart/plot';
import i18n from '@/app/i18n';
import board from '@/locales/fr.subscriptions-board.json';
import { cn } from '@/lib/utils';
import { LIST, OVERVIEW, PERSONS, type Overview } from '../api';

// Loaded with this lazy chunk, not with the initial bundle.
i18n.addResourceBundle('fr', 'subscriptions-board', board, true, true);

const MonthsChart = lazy(() => import('./MonthsChart').then((m) => ({ default: m.MonthsChart })));

type Scope = 'household' | 'me';

/** A server figure as text; « — » when a rate is missing. */
const text = (value?: MoneyValue | null) => (value ? formatMoney(value) : '—');

/** Subscriptions dashboard design §4.2: the server's figures, then its panels; nothing is computed here. */
export function OverviewTab({ scope }: { scope: Scope }) {
  const { t } = useTranslation('subscriptions-board');
  const { t: tc } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', OVERVIEW, { params: { query: { scope } } });

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  const d = answer.data;
  if (!d) return <Skeleton aria-busy="true" className="h-64 w-full" />;
  if (d.activeCount === 0 && d.endedCount === 0)
    return (
      <EmptyState title={tc('subscriptions.empty_title')} body={tc('subscriptions.empty_body')}
        action={<Link to="/subscriptions/new" className={buttonVariants()}>{tc('subscriptions.empty_action')}</Link>} />
    );

  // DOM order is the phone's (figures, months, watch, upcoming, persons, labels); `lg:order-*` lays out the desktop grid.
  return (
    <div className="space-y-6">
      <StatLine items={[
        { label: tc('subscriptions.per_month'), value: text(d.monthlyAmortized) },
        { label: tc('subscriptions.per_year'), value: text(d.annualAmortized) },
        { label: t('overview.remaining'), value: <Trans t={t} i18nKey="overview.remaining_of" values={{ left: text(d.monthCashRemaining), total: text(d.monthCash) }}
            components={{ left: <span className="whitespace-nowrap" />, total: <span className="whitespace-nowrap" /> }} /> },
        { label: t('overview.count'), value: t('overview.counts', { active: t('overview.active', { count: d.activeCount }), ended: t('overview.ended', { count: d.endedCount }) }) },
      ]} />
      <div className="grid gap-4 lg:grid-cols-3">
        <Panel title={t('overview.months')} className="lg:order-1 lg:col-span-2">
          <Suspense fallback={<div className="h-64 shimmer rounded-md bg-muted" />}><MonthsChart months={d.months} /></Suspense>
        </Panel>
        <Panel title={t('overview.watch')} className="lg:order-5"><Watch d={d} scope={scope} /></Panel>
        <Panel title={t('overview.upcoming')} className="lg:order-4"
          action={<Link to="/subscriptions" search={(prev) => ({ scope: prev.scope, tab: 'calendar' })} className="text-sm underline-offset-4 hover:underline">{t('overview.calendar')}</Link>}><Upcoming d={d} /></Panel>
        <Panel title={t('overview.persons')} className="lg:order-2"><Persons d={d} /></Panel>
        <Panel title={t('overview.labels')} className="lg:order-3"
          action={<Link to="/subscriptions" search={(prev) => ({ scope: prev.scope, tab: 'list', group: 'label', status: 'all' })} className="text-sm underline-offset-4 hover:underline">{t('overview.see_list')}</Link>}>
          <Labels d={d} />
        </Panel>
      </div>
    </div>
  );
}

function Panel({ title, className, action, children }: { title: string; className?: string; action?: ReactNode; children: ReactNode }) {
  const id = useId();
  return (
    <section aria-labelledby={id} className={cn('space-y-3 rounded-lg border p-4', className)}>
      <div className="flex items-center justify-between gap-2">
        <h2 id={id} className="text-sm font-semibold">{title}</h2>
        {action}
      </div>
      {children}
    </section>
  );
}

function Amount({ value }: { value?: MoneyValue | null }) {
  return value ? <Money value={value} tabular /> : <span>—</span>;
}

/** A share bar and one line per person; the colour rank is the dashboard's (adults first, by creation), as on the owner chips. */
function Persons({ d }: { d: Overview }) {
  const { t } = useTranslation('subscriptions-board');
  const { $api } = useApi();
  const people = $api.useQuery('get', PERSONS);
  const ranked = [...(people.data ?? [])].sort((a, b) => Number(a.kind !== 'adult') - Number(b.kind !== 'adult'));
  const rows = d.byPerson.map((share) => {
    const slot = ranked.findIndex((p) => p.id === share.personId);
    return { ...share, color: personColor(slot), name: ranked[slot]?.displayName ?? '' };
  });
  return (
    <>
      <div aria-hidden="true" className="flex h-2.5 overflow-hidden rounded-full bg-muted">
        {rows.map((r) => <div key={r.personId} style={{ flexGrow: r.amortized ? toPlot(r.amortized.amount) : 0, background: r.color }} />)}
      </div>
      <ul className="space-y-2 text-sm">
        {rows.map((r) => (
          <li key={r.personId} className="flex items-center gap-2">
            <span aria-hidden="true" className="size-2.5 shrink-0 rounded-full" style={{ background: r.color }} />
            <span className="font-medium">{r.name}</span>
            <span className="text-muted-foreground">
              {t('overview.person_counts', { alone: t('overview.alone', { count: r.ownedAlone }), shared: t('overview.shared', { count: r.shared }) })}
            </span>
            <span className="ml-auto"><Amount value={r.amortized} /></span>
          </li>
        ))}
      </ul>
    </>
  );
}

/** Horizontal bars, in the server's order (largest first); the widths are plot sizes only. */
function Labels({ d }: { d: Overview }) {
  const { t: tc } = useTranslation();
  const max = Math.max(0, ...d.byLabel.map((l) => (l.amortized ? toPlot(l.amortized.amount) : 0)));
  return (
    <ul className="space-y-2 text-sm">
      {d.byLabel.map((l) => (
        <li key={l.key} className="space-y-1">
          <div className="flex justify-between gap-2">
            <span>{l.name ?? tc('subscriptions.no_label')}</span>
            <Amount value={l.amortized} />
          </div>
          <div aria-hidden="true" className="h-1.5 rounded-full bg-muted">
            <div className="h-full rounded-full bg-primary"
              style={{ width: `${max > 0 && l.amortized ? (toPlot(l.amortized.amount) / max) * 100 : 0}%`, background: l.color ?? undefined }} />
          </div>
        </li>
      ))}
    </ul>
  );
}

const UPCOMING_SHOWN = 8;

function Upcoming({ d }: { d: Overview }) {
  const { t } = useTranslation('subscriptions-board');
  if (d.upcoming.length === 0) return <p className="text-sm text-muted-foreground">{t('overview.upcoming_none')}</p>;
  return (
    <ul className="space-y-2 text-sm">
      {d.upcoming.slice(0, UPCOMING_SHOWN).map((c, i) => (
        <li key={`${c.date}/${c.itemId}/${i}`} className="flex items-center gap-3">
          <span className="w-24 shrink-0 text-muted-foreground"><LocalDate value={c.date} style="short" /></span>
          <ProviderIcon name={c.provider?.name ?? c.displayName} iconUrl={c.provider?.iconUrl} color={c.provider?.labels[0]?.color} />
          <Link to="/subscriptions/$id" params={{ id: c.itemId }} className="min-w-0 truncate font-medium hover:underline">{c.displayName}</Link>
          <span className="ml-auto whitespace-nowrap"><Amount value={c.converted ?? c.price} /></span>
        </li>
      ))}
      {d.upcoming.length > UPCOMING_SHOWN && (
        <li className="text-muted-foreground">{t('overview.upcoming_more', { count: d.upcoming.length - UPCOMING_SHOWN })}</li>
      )}
    </ul>
  );
}

const ALERT = 'subscriptions.ends_without_renewal';

/** One line per server code; the names come from the list (the overview carries ids only). */
function Watch({ d, scope }: { d: Overview; scope: Scope }) {
  const { t } = useTranslation('subscriptions-board');
  const { $api } = useApi();
  const items = $api.useQuery('get', LIST, { params: { query: { scope, status: 'all' } } }, { enabled: d.watch.length > 0 });
  if (d.watch.length === 0) return <p className="text-sm text-muted-foreground">{t('overview.watch_none')}</p>;
  if (!items.data && !items.error) return <Skeleton aria-busy="true" className="h-16 w-full" />;
  // Without the list the sentences still show, with generic names.
  const byId = new Map((items.data?.items ?? []).map((item) => [item.id, item]));
  const date = (iso?: string | null) => (iso ? formatLocalDate(iso) : '—');

  return (
    <ul className="space-y-2 text-sm">
      {d.watch.map((w) => {
        const first = byId.get(w.itemIds[0] ?? '');
        const provider = first?.provider?.name ?? t('overview.unknown_provider');
        const link = w.code === 'subscriptions.same_provider'
          ? <Link to="/subscriptions" search={(prev) => ({ scope: prev.scope, tab: 'list', group: 'provider' })} className="hover:underline">
              {t('overview.watch_same_provider', { count: w.itemIds.length, provider, amount: text(w.amount) })}
            </Link>
          : <Link to="/subscriptions/$id" params={{ id: w.itemIds[0] ?? '' }} className="hover:underline">
              {t(w.code === ALERT ? 'overview.watch_ends' : 'overview.watch_long_cycle',
                { name: first?.displayName ?? t('overview.unknown_item'), date: date(w.date), amount: text(w.amount) })}
            </Link>;
        return (
          <li key={`${w.code}/${w.itemIds.join()}`} className="flex items-start gap-2">
            <span data-tone={w.code === ALERT ? 'alert' : 'info'} className="inline-flex shrink-0 items-center gap-1.5 rounded-full border px-2 text-xs">
              <span aria-hidden="true" className={cn('size-2 rounded-full', w.code === ALERT ? 'bg-status-warning' : 'bg-primary')} />
              {t(w.code === ALERT ? 'overview.tone_alert' : 'overview.tone_info')}
            </span>
            {link}
          </li>
        );
      })}
    </ul>
  );
}
