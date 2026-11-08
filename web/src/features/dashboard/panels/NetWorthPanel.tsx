import { Link } from '@tanstack/react-router';
import { Clock } from 'lucide-react';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSession } from '@/app/Providers';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { Segmented } from '@/components/form/Segmented';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { Direction } from '@/components/Direction';
import type { DashboardEnvelope, DashboardView } from '../api';
import { personColor } from '@/components/colors';
import { percent } from '@/lib/format';
import { ItemLink } from '../ItemLink';
import { Value } from '../KeyFigures';
import { AllocationSection } from './Allocation';
import type { CompositionView } from './NetWorthCharts';

const NetWorthCharts = lazy(() => import('./NetWorthCharts').then((m) => ({ default: m.NetWorthCharts })));

// Spec §4.2: the server's rows grouped by module, never a subtotal (no amount arithmetic in the browser).
const GROUPS = [
  { module: 'investments', label: 'nav.investments' },
  { module: 'properties', label: 'nav.properties' },
  { module: 'loans', label: 'nav.loans' },
] as const;

function Amount({ row }: { row: DashboardEnvelope }) {
  const { t } = useTranslation();
  if (row.converted) return <>{formatMoney(row.converted)}</>;
  if (row.missingCurrency && row.value)
    return <span className="text-muted-foreground">{t('dashboard.split.unknown_rate', { code: row.missingCurrency, amount: formatMoney(row.value) })}</span>;
  return <span className="text-muted-foreground">{t('dashboard.split.without_value')}</span>;
}

function Composition({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  return (
    <Table aria-label={t('dashboard.split.envelopes_table')}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('dashboard.split.col_envelope')}</TableHead>
          <TableHead className="text-right">{t('dashboard.split.col_amount')}</TableHead>
          <TableHead className="text-right">{t('dashboard.split.col_as_of')}</TableHead>
        </TableRow>
      </TableHeader>
      {GROUPS.map((g) => {
        const rows = d.envelopeSplit.filter((r) => r.module === g.module);
        return rows.length === 0 ? null : (
          <TableBody key={g.module}>
            <TableRow className="bg-muted/40">
              <TableHead colSpan={3} scope="colgroup">{t(g.label)}</TableHead>
            </TableRow>
            {rows.map((row) => (
              <TableRow key={row.id}>
                <TableHead scope="row" className="font-normal">
                  <ItemLink module={row.module} id={row.id}>{row.name}</ItemLink>
                </TableHead>
                <TableCell className="text-right tabular-nums"><Amount row={row} /></TableCell>
                <TableCell className={row.stale ? 'text-right text-status-warning-text' : 'text-right'}>
                  {row.stale ? <Clock aria-hidden="true" className="mr-1 inline size-3" /> : null}
                  {row.asOf ? formatLocalDate(row.asOf) : null}
                  {row.estimated
                    ? <span className={row.stale ? 'block text-xs' : 'block text-xs text-muted-foreground'}>{row.stale && row.valuedOn ? t('dashboard.split.estimated_stale', { valuedOn: formatLocalDate(row.valuedOn) }) : t('dashboard.split.estimated')}</span>
                    : row.stale ? <span className="block text-xs">{t('dashboard.split.stale')}</span> : null}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        );
      })}
    </Table>
  );
}

/** One person's pro-rata share; one unknown rate makes the whole split unknown. */
function People({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  if (d.people === null || d.people === undefined)
    return <p className="text-sm text-muted-foreground">{t('dashboard.split.people_unknown_rate', { code: d.peopleMissingCurrency ?? '' })}</p>;
  if (d.people.length === 0) return <p className="text-sm text-muted-foreground">{t('dashboard.split.people_none')}</p>;
  return (
    <Table aria-label={t('dashboard.split.people_table')}>
      <TableHeader>
        <TableRow>
          <TableHead>{t('dashboard.split.col_person')}</TableHead>
          <TableHead className="text-right">{t('dashboard.split.col_amount')}</TableHead>
          <TableHead className="text-right">{t('dashboard.split.col_share')}</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {d.people.map((p) => (
          <TableRow key={p.id}>
            <TableHead scope="row" className="font-normal">
              <span aria-hidden="true" className="mr-2 inline-block size-3 rounded-sm align-middle" style={{ background: personColor(p.slot) }} />
              {p.name}
            </TableHead>
            <TableCell className="text-right tabular-nums">{formatMoney(p.amount)}</TableCell>
            <TableCell className="text-right tabular-nums">{t('dashboard.pct', { value: percent(p.pct) })}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  );
}

/** Spec §4.2: the figure, what the home dropped (date, stale, without value, change detail), then the composition. */
export function NetWorthPanel({ d }: { d: DashboardView }) {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  const [view, setView] = useState<CompositionView>('type');
  const has = (m: string) => capabilities.modules.includes(m);
  const change = d.change.total;
  const paidIn = d.changeContributions.total;
  return (
    <div className="space-y-4">
      <div className="space-y-1">
        <Value figure={d.netWorth} unknown={t('dashboard.net_worth_unknown')} />
        {d.valuesAsOf ? <p className="text-sm text-muted-foreground">{t('dashboard.values_as_of', { date: formatLocalDate(d.valuesAsOf) })}</p> : null}
        <p className="text-sm">{t('dashboard.change')}</p>
        <p className="text-sm">
          {change ? (
            <Direction amount={change}>
              {d.changePct != null ? <span className="tabular-nums">{t('dashboard.pct', { value: percent(d.changePct, true) })}</span> : null}
            </Direction>
          ) : <Value figure={d.change} unknown={t('dashboard.change_unknown')} unknownRate="dashboard.change_unknown_rate" />}
        </p>
        {paidIn ? <p className="text-sm text-muted-foreground">{t('dashboard.of_which_paid_in', { amount: formatMoney(paidIn) })}</p> : null}
        {d.envelopesWithoutStart > 0 ? <p className="text-sm text-muted-foreground">{t('dashboard.without_start', { count: d.envelopesWithoutStart })}</p> : null}
      </div>
      <ul className="space-y-1 text-sm">
        {d.staleEnvelopes > 0 ? (
          <li className="flex items-center gap-1 text-status-warning-text">
            <Clock aria-hidden="true" className="size-4" />
            {has('investments') || has('realestate')
              ? <Link to="/routine" className="underline">{t('dashboard.stale', { count: d.staleEnvelopes })}</Link>
              : t('dashboard.stale', { count: d.staleEnvelopes })}
          </li>
        ) : null}
        {d.estimatedEnvelopes > 0 ? <li className="text-muted-foreground">{t('dashboard.estimated', { count: d.estimatedEnvelopes })}</li> : null}
        {d.envelopesWithoutValue > 0 ? <li className="text-muted-foreground">{t('dashboard.without_value', { count: d.envelopesWithoutValue })}</li> : null}
      </ul>
      {d.envelopeSplit.length > 0 ? (
        <section className="space-y-3">
          <Segmented value={view} onValueChange={(v) => setView(v as CompositionView)} aria-label={t('dashboard.panel.view')}
            options={[
              { value: 'type', label: t('dashboard.panel.by_type') }, { value: 'person', label: t('dashboard.panel.by_person') },
              { value: 'class', label: t('dashboard.panel.by_class') }, { value: 'liquidity', label: t('dashboard.panel.by_liquidity') },
            ]} />
          <Suspense fallback={<Skeleton className="h-24 w-full" />}>
            <NetWorthCharts view={view} rows={d.envelopeSplit} people={d.people ?? []} allocation={d.allocation} />
          </Suspense>
          {view === 'type' ? <Composition d={d} /> : view === 'person' ? <People d={d} /> : <AllocationSection d={d} kind={view} />}
        </section>
      ) : null}
      <div className="flex flex-wrap gap-4 text-sm">
        {has('investments') ? <Link to="/investments" className="underline">{t('dashboard.see_investments')}</Link> : null}
        {has('realestate') ? <Link to="/properties" className="underline">{t('dashboard.see_properties')}</Link> : null}
      </div>
    </div>
  );
}
