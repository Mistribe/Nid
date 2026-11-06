import { Link } from '@tanstack/react-router';
import { lazy, Suspense, useId, useState, type ReactNode } from 'react';
import { Trans, useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { tileOf } from '@/app/nav';
import { seeAllClass } from '@/components/page/seeAll';
import { present, toProblem } from '@/api/errors';
import { EmptyState } from '@/components/EmptyState';
import { formatLocalDate, LocalDate, monthText } from '@/components/LocalDate';
import { formatMoney, Money, type MoneyValue } from '@/components/Money';
import { GroupedList, GroupedRow } from '@/components/page/GroupedList';
import { StatLine } from '@/components/page/StatLine';
import { TableToggle } from '@/components/page/TableToggle';
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
import { durationText } from '../format';

// Loaded with this lazy chunk, not with the initial bundle.
i18n.addResourceBundle('fr', 'subscriptions-board', board, true, true);

const MonthsChart = lazy(() => import('./MonthsChart').then((m) => ({ default: m.MonthsChart })));
const YearsChart = lazy(() => import('./YearsChart').then((m) => ({ default: m.YearsChart })));

type Scope = 'household' | 'me';

/** A server figure as text; « — » when a rate is missing. */
const text = (value?: MoneyValue | null) => (value ? formatMoney(value) : '—');

/** Subscriptions dashboard design §4.2: the server's figures, then its panels; nothing is computed here. */
export function OverviewTab({ scope }: { scope: Scope }) {
  const { t } = useTranslation('subscriptions-board');
  const { t: tc } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', OVERVIEW, { params: { query: { scope } } });
  const [monthsTable, setMonthsTable] = useState(false);

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  const d = answer.data;
  if (!d) return <Skeleton aria-busy="true" className="h-64 w-full" />;
  if (d.activeCount === 0 && d.endedCount === 0)
    return (
      <EmptyState module="/subscriptions" title={tc('subscriptions.empty_title')} body={tc('subscriptions.empty_body')}
        action={<Link to="/subscriptions/new" className={buttonVariants({ variant: 'outline' })}>{tc('subscriptions.empty_action')}</Link>} />
    );

  // DOM order is the phone's (figures, months, watch, upcoming, persons, labels); `lg:order-*` lays out the desktop grid.
  return (
    <div className="space-y-6">
      <StatLine items={[
        { label: tc('subscriptions.per_month'), value: text(d.monthlyAmortized) },
        { label: tc('subscriptions.per_year'), value: text(d.annualAmortized) },
        // #376: the figure on its line, what it is read against under it.
        { label: t('overview.remaining'), value: text(d.monthCashRemaining), hint: t('overview.remaining_of', { total: text(d.monthCash) }) },
        { label: t('overview.count'), value: t('overview.active', { count: d.activeCount }), hint: t('overview.ended', { count: d.endedCount }) },
      ]} />
      <div className="grid gap-4 lg:grid-cols-3">
        <Panel title={t('overview.months')} className="lg:order-1 lg:col-span-2"
          action={<TableToggle asTable={monthsTable} onToggle={() => setMonthsTable((v) => !v)} />}>
          <Suspense fallback={<div className="h-64 shimmer rounded-md bg-muted" />}><MonthsChart months={d.months} asTable={monthsTable} /></Suspense>
        </Panel>
        <Panel title={t('overview.watch')} className="lg:order-5"><Watch d={d} scope={scope} /></Panel>
        <Panel title={t('overview.upcoming')} className="lg:order-4"
          action={<Link to="/subscriptions" search={(prev) => ({ scope: prev.scope, tab: 'calendar' })} className="text-sm underline-offset-4 hover:underline">{t('overview.calendar')}</Link>}><Upcoming d={d} /></Panel>
        <Panel title={t('overview.persons')} className="lg:order-2"><Persons d={d} /></Panel>
        <Panel title={t('overview.labels')} className="lg:order-3"
          action={<Link to="/subscriptions" search={(prev) => ({ scope: prev.scope, tab: 'list', group: 'label', status: 'all' })} className={seeAllClass}>{t('overview.see_list')}</Link>}>
          <Labels d={d} />
        </Panel>
        {d.years.length > 0 ? (
          <Panel title={t('years.title')} className="lg:order-6 lg:col-span-3">
            <Suspense fallback={<div className="h-64 shimmer rounded-md bg-muted" />}><YearsChart years={d.years} /></Suspense>
          </Panel>
        ) : null}
        {d.topProviders.length > 0 ? <Panel title={t('providers.title')} className="lg:order-7"><TopProviders d={d} /></Panel> : null}
        {d.savings.items.length > 0 ? <Panel title={t('savings.title')} className="lg:order-8"><Savings d={d} /></Panel> : null}
        {d.priceRises.items.length > 0 ? <Panel title={t('rises.title')} className="lg:order-9"><PriceRises d={d} /></Panel> : null}
      </div>
    </div>
  );
}

function Panel({ title, className, action, children }: { title: string; className?: string; action?: ReactNode; children: ReactNode }) {
  const id = useId();
  return (
    <section aria-labelledby={id} className={cn('min-w-0 space-y-3 rounded-xl border bg-card p-4 shadow-card', className)}>
      <div className="flex items-center justify-between gap-2">
        <h2 id={id} className="text-base font-semibold">{title}</h2>
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
              style={{ width: `${max > 0 && l.amortized ? (toPlot(l.amortized.amount) / max) * 100 : 0}%` }} />
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

  // design-system.md §6.1, « Listes groupées »: a row per alert, as on Placements and Biens; an alert says so after its sentence.
  return (
    <GroupedList>
      {d.watch.map((w) => {
        const first = byId.get(w.itemIds[0] ?? '');
        const provider = first?.provider?.name ?? t('overview.unknown_provider');
        const alert = w.code === ALERT;
        return (
          <GroupedRow key={`${w.code}/${w.itemIds.join()}`} tile={tileOf('/subscriptions')}
            link={w.code === 'subscriptions.same_provider'
              ? { to: '/subscriptions', search: { scope: scope === 'me' ? 'me' as const : undefined, tab: 'list', group: 'provider' as const } }
              : { to: '/subscriptions/$id', params: { id: w.itemIds[0] ?? '' } }}
            title={w.code === 'subscriptions.same_provider'
              ? t('overview.watch_same_provider', { count: w.itemIds.length, provider, amount: text(w.amount) })
              : t(alert ? 'overview.watch_ends' : 'overview.watch_long_cycle',
                { name: first?.displayName ?? t('overview.unknown_item'), date: date(w.date), amount: text(w.amount) })}
            trailing={alert ? <span data-tone="alert" className="text-xs font-medium text-status-warning-text">{t('overview.tone_alert')}</span> : undefined} />
        );
      })}
    </GroupedList>
  );
}

/** A partial total (a rate was missing) says so after the amount; the findings name the rate. */
function Partial({ partial }: { partial: boolean }) {
  const { t } = useTranslation('subscriptions-board');
  return partial ? <span className="ml-1 text-xs text-muted-foreground">{t('partial')}</span> : null;
}

/** #395: the five providers that cost the most since their first charge, ended subscriptions included; a row opens the list on it. */
function TopProviders({ d }: { d: Overview }) {
  return (
    <ul className="space-y-2 text-sm">
      {d.topProviders.map((p) => (
        <li key={p.provider.key} className="flex items-center gap-3">
          <ProviderIcon name={p.provider.name} iconUrl={p.provider.iconUrl} color={p.provider.labels[0]?.color} />
          <Link to="/subscriptions" search={(prev) => ({ scope: prev.scope, tab: 'list', status: 'all' as const, provider: p.provider.key })}
            className="min-w-0 truncate font-medium hover:underline">{p.provider.name}</Link>
          <span className="ml-auto whitespace-nowrap"><Amount value={p.spent} /><Partial partial={p.partial} /></span>
        </li>
      ))}
    </ul>
  );
}

/** #397: what the ended subscriptions without successor would have cost over the last 12 months, and each of them. */
function Savings({ d }: { d: Overview }) {
  const { t } = useTranslation('subscriptions-board');
  return (
    <>
      <p className="text-sm"><Trans t={t} i18nKey="savings.total" values={{ amount: text(d.savings.last12Months) }}
        components={{ amount: <span className="text-figure whitespace-nowrap" /> }} /><Partial partial={d.savings.partial} /></p>
      <ul className="space-y-2 text-sm">
        {d.savings.items.map((s) => (
          <li key={s.itemId} className="flex items-center gap-3">
            <ProviderIcon name={s.provider?.name ?? s.displayName} iconUrl={s.provider?.iconUrl} color={s.provider?.labels[0]?.color} />
            <span className="min-w-0">
              <Link to="/subscriptions/$id" params={{ id: s.itemId }} className="block truncate font-medium hover:underline">{s.displayName}</Link>
              <span className="text-xs text-muted-foreground">{t('savings.ended', { date: formatLocalDate(s.endedOn) })}</span>
            </span>
            <span className="ml-auto whitespace-nowrap"><Amount value={s.last12Months} /></span>
          </li>
        ))}
      </ul>
    </>
  );
}

const pct = (value: number) => new Intl.NumberFormat('fr-FR', { minimumFractionDigits: 1, maximumFractionDigits: 1, signDisplay: 'exceptZero' }).format(value);
const REASONS = ['unavailable', 'currency', 'not_published'] as const;

/** #419: « +10,0 %, soit +7,8 % après inflation », or the nominal change and why there is no real one. */
function RiseChange({ rise }: { rise: Overview['priceRises']['items'][number] }) {
  const { t } = useTranslation('subscriptions-board');
  if (rise.changePct == null) return null;
  const { real } = rise;
  const change = pct(rise.changePct);
  const reason = REASONS.find((r) => r === real?.missing) ?? 'unavailable';
  return (
    <span className="block text-xs text-muted-foreground">
      {real?.percent != null
        ? t('rises.pct_real', { change, real: pct(real.percent) })
        : t('rises.pct', { change })}
      {real?.percent != null && real.inflationTo ? <> ({t('rises.real_to', { month: monthText(real.inflationTo) })})</> : null}
      {real?.percent == null ? <> · {t(`rises.real_missing.${reason}`)}</> : null}
    </span>
  );
}

/** #398: each dearer price of the last 12 months, latest first; a launch offer ending says so and stays out of the total. */
function PriceRises({ d }: { d: Overview }) {
  const { t } = useTranslation('subscriptions-board');
  const { t: tc } = useTranslation();
  const price = (money: MoneyValue, every: { count: number; unit: string }) => `${formatMoney(money)} / ${durationText(tc, every)}`;
  return (
    <>
      {d.priceRises.yearlyTotal ? <p className="text-sm">{t('rises.total', { amount: text(d.priceRises.yearlyTotal) })}</p> : null}
      <ul className="space-y-2 text-sm">
        {d.priceRises.items.map((r) => (
          <li key={`${r.itemId}/${r.on}`} className="flex items-center gap-3">
            <ProviderIcon name={r.provider?.name ?? r.displayName} iconUrl={r.provider?.iconUrl} color={r.provider?.labels[0]?.color} />
            <span className="min-w-0">
              <Link to="/subscriptions/$id" params={{ id: r.itemId }} search={{ tab: 'history' }} className="block truncate font-medium hover:underline">{r.displayName}</Link>
              <span className="text-xs text-muted-foreground">
                {t('rises.change', { old: price(r.oldPrice, r.oldEvery), new: price(r.newPrice, r.newEvery), date: formatLocalDate(r.on) })}
              </span>
            </span>
            <span className="ml-auto whitespace-nowrap text-right">
              {r.promoEnded
                ? <span className="text-xs font-medium text-muted-foreground">{t('rises.promo_ended')}</span>
                : <>{t('rises.yearly', { amount: text(r.yearlyDifference) })}<RiseChange rise={r} /></>}
            </span>
          </li>
        ))}
      </ul>
    </>
  );
}
