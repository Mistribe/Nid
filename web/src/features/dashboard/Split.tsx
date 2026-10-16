import { Clock } from 'lucide-react';
import { useId, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import type { DashboardEnvelope, DashboardView } from './api';
import { personColor } from './colors';
import { percent } from './format';
import { ItemLink } from './ItemLink';

export function Panel({ title, guide, children }: { title: string; guide?: string; children: ReactNode }) {
  const id = useId();
  return (
    <section aria-labelledby={id} data-guide={guide} className="space-y-3 rounded-md border p-4">
      <h2 id={id} className="text-sm font-medium text-muted-foreground">{title}</h2>
      {children}
    </section>
  );
}

function EnvelopeAmount({ row }: { row: DashboardEnvelope }) {
  const { t } = useTranslation();
  if (row.converted) return <>{formatMoney(row.converted)}</>;
  if (row.missingCurrency && row.value)
    return <span className="text-muted-foreground">{t('dashboard.split.unknown_rate', { code: row.missingCurrency, amount: formatMoney(row.value) })}</span>;
  return <span className="text-muted-foreground">{t('dashboard.split.without_value')}</span>;
}

/** Spec §3: every envelope, sorted by the server; the chart (Task 5) goes above the table. */
export function EnvelopeSplit({ d, chart }: { d: DashboardView; chart?: ReactNode }) {
  const { t } = useTranslation();
  if (d.envelopeSplit.length === 0) return null;
  return (
    <Panel title={t('dashboard.split.by_envelope')}>
      {chart}
      <table className="w-full text-sm" aria-label={t('dashboard.split.envelopes_table')}>
        <thead>
          <tr>
            <th scope="col" className="text-left">{t('dashboard.split.col_envelope')}</th>
            <th scope="col" className="text-right">{t('dashboard.split.col_amount')}</th>
            <th scope="col" className="text-right">{t('dashboard.split.col_as_of')}</th>
          </tr>
        </thead>
        <tbody>
          {d.envelopeSplit.map((row) => (
            <tr key={row.id}>
              <th scope="row" className="text-left font-normal">
                <ItemLink module={row.module} id={row.id}>{row.name}</ItemLink>
                {row.stale ? (
                  <span className="ml-2 inline-flex items-center gap-1 text-xs"><Clock aria-hidden="true" className="size-3" />{t('dashboard.split.stale')}</span>
                ) : null}
              </th>
              <td className="text-right tabular-nums"><EnvelopeAmount row={row} /></td>
              <td className="text-right">{row.asOf ? formatLocalDate(row.asOf) : null}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </Panel>
  );
}

/** Spec §3: each person's pro-rata share; one unknown rate makes the whole split unknown. */
export function PeopleSplit({ d, chart }: { d: DashboardView; chart?: ReactNode }) {
  const { t } = useTranslation();
  if (d.envelopeSplit.length === 0) return null;
  return (
    <Panel title={t('dashboard.split.by_person')}>
      {d.people === null || d.people === undefined ? (
        <p className="text-muted-foreground">{t('dashboard.split.people_unknown_rate', { code: d.peopleMissingCurrency ?? '' })}</p>
      ) : d.people.length === 0 ? (
        <p className="text-muted-foreground">{t('dashboard.split.people_none')}</p>
      ) : (
        <>
          {chart}
          <table className="w-full text-sm" aria-label={t('dashboard.split.people_table')}>
            <thead>
              <tr>
                <th scope="col" className="text-left">{t('dashboard.split.col_person')}</th>
                <th scope="col" className="text-right">{t('dashboard.split.col_amount')}</th>
                <th scope="col" className="text-right">{t('dashboard.split.col_share')}</th>
              </tr>
            </thead>
            <tbody>
              {d.people.map((p) => (
                <tr key={p.id}>
                  <th scope="row" className="text-left font-normal">
                    <span aria-hidden="true" className="mr-2 inline-block size-3 rounded-sm align-middle" style={{ background: personColor(p.slot) }} />
                    {p.name}
                  </th>
                  <td className="text-right tabular-nums">{formatMoney(p.amount)}</td>
                  <td className="text-right tabular-nums">{t('dashboard.pct', { value: percent(p.pct) })}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>
      )}
    </Panel>
  );
}
