import { useQueryClient } from '@tanstack/react-query';
import { Link, useSearch } from '@tanstack/react-router';
import { useState } from 'react';
import { toast } from 'sonner';
import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { PERSONS } from '@/api/persons';
import { useSession } from '@/app/Providers';
import { present, toProblem } from '@/api/errors';
import { formatCurrency } from '@/components/Money';
import { PageHeader } from '@/components/page/PageHeader';
import { RowMenu } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { Loading, TableSkeleton } from '@/components/page/skeletons';
import { SortableTable } from '@/components/page/SortableTable';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { monthLabel } from '@/lib/format';
import type { RootSearch } from '@/lib/search';
import { RetirementDialog } from '@/features/settings/RetirementDialog';
import { PensionDialog } from './PensionDialog';
import { RETIREMENT, type Departure, type RetirementView } from './api';

/** A server month (yyyy-MM) as « novembre 2040 ». */
const month = (yyyyMm: string) => {
  const [year, m] = yyyyMm.split('-').map(Number);
  return monthLabel(year ?? 0, m ?? 1, 'long');
};
/** A server percent as a French one: « 4.0 » reads « 4,0 % » (a string swap, nothing computed). */
const percent = (value: string) => `${value.replace('.', ',')} %`;
const age = (d: Departure, t: TFunction) => (d.months > 0 ? t('settings.projection.age_years_months', { years: d.years, months: d.months }) : t('settings.projection.age_years', { count: d.years }));

/** Retirement spec §5: the income gap at the departure and the capital to close it, who leaves when, and the assumptions behind. */
export function RetirementPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const search: RootSearch = useSearch({ strict: false });
  const scope = search.scope === 'me' ? 'me' : 'household';
  const answer = $api.useQuery('get', RETIREMENT, { params: { query: { scope } } });

  return (
    <div className="space-y-6">
      <PageHeader title={t('nav.retirement')} />
      {answer.error ? <ProblemMessage problem={present(toProblem(answer.error))} />
        : !answer.data ? <Loading label={t('retirement.loading')}><TableSkeleton columns={4} /></Loading>
          : <Body data={answer.data} scope={scope} />}
    </div>
  );
}

function Body({ data, scope }: { data: RetirementView; scope: 'household' | 'me' }) {
  const { t } = useTranslation();
  const money = (amount: string) => formatCurrency(amount, data.currency);
  const f = data.figures;
  const livingCostMissing = data.warnings.some((w) => w.code === 'living_cost_missing');
  const settings = <Link to="/settings/projection" className="underline underline-offset-2">{t('retirement.living_cost_missing')}</Link>;
  const a = data.assumptions;
  const { session } = useSession();
  const queryClient = useQueryClient();
  const [dialog, setDialog] = useState<{ kind: 'departure' | 'pension'; d: Departure } | null>(null);
  const close = () => setDialog(null);
  const done = (message: string) => {
    close();
    toast.success(message);
    void queryClient.invalidateQueries({ queryKey: ['get', RETIREMENT] });
    void queryClient.invalidateQueries({ queryKey: ['get', PERSONS] });
  };
  const actions = (d: Departure) => (
    <RowMenu label={t('retirement.row_actions', { name: d.name })} items={[
      { label: t('retirement.edit_departure'), onSelect: () => setDialog({ kind: 'departure', d }) },
      ...(d.person === session.person.id && d.departure && d.sources.length === 0 ? [{ label: t('retirement.enter_pension'), onSelect: () => setDialog({ kind: 'pension' as const, d }) }] : []),
    ]} />
  );

  return (
    <>
      {f ? (
        <>
          <StatLine items={[
            { label: t('retirement.income_now'), value: money(f.incomeNow) },
            { label: t('retirement.income_at_retirement'), value: money(f.incomeAtRetirement), hint: month(f.month) },
            { label: t('retirement.gap'), value: money(f.gap) },
            { label: t('retirement.capital_needed'), value: money(f.capitalNeeded) },
            {
              label: t('retirement.independence'),
              value: data.financialIndependence ? month(data.financialIndependence) : livingCostMissing ? <span className="text-sm font-normal">{settings}</span> : t('retirement.independence_unreached'),
            },
          ]} />
          <p className="text-sm text-muted-foreground">{t('retirement.note')}</p>
        </>
      ) : (
        <>
          <p className="text-sm text-muted-foreground">{t('retirement.no_figures')}</p>
          {livingCostMissing ? <p className="text-sm">{settings}</p> : null}
        </>
      )}
      <Warnings data={data} />

      <Section title={t('retirement.departures')}>
        <SortableTable
          caption={t('retirement.departures')}
          rows={data.departures}
          rowKey={(d) => d.person}
          columns={[
            { id: 'name', header: t('retirement.adult'), cell: (d) => d.name },
            { id: 'departure', header: t('retirement.departure'), cell: (d) => (d.departure ? month(d.departure) : '—') },
            { id: 'age', header: t('retirement.age'), cell: (d) => age(d, t) },
            { id: 'pension', header: t('retirement.pension'), align: 'end', cell: (d) => <Pension d={d} money={money} /> },
            { id: 'actions', header: t('retirement.actions'), align: 'end', cell: actions },
          ]}
          mobile={(d) => (
            <div className="space-y-0.5">
              <div className="font-medium">{d.name}</div>
              <div className="text-sm text-muted-foreground">{d.departure ? month(d.departure) : '—'} · {age(d, t)}</div>
              <div className="text-sm"><Pension d={d} money={money} /></div>
              <div>{actions(d)}</div>
            </div>
          )}
        />
        {scope === 'household' ? <p className="text-sm text-muted-foreground">{t('retirement.household_pensions')}</p> : null}
      </Section>
      {dialog?.kind === 'departure' ? (
        <RetirementDialog person={{ id: dialog.d.person, displayName: dialog.d.name, birthDate: dialog.d.birthDate, retirementAge: dialog.d.years, retirementMonths: dialog.d.months }} onClose={close} onDone={done} />
      ) : null}
      {dialog?.kind === 'pension' && dialog.d.departure ? (
        <PensionDialog personId={dialog.d.person} departure={dialog.d.departure} currency={data.currency} onClose={close} onDone={done} />
      ) : null}

      <Section title={t('retirement.assumptions')}>
        <ul className="space-y-1 text-sm">
          <li>{t('settings.projection.withdrawal_rate')} : {percent(a.withdrawalRate)}</li>
          <li>{t('settings.projection.living_cost_summary')} : {Number(a.monthlyLivingCost) > 0 ? money(a.monthlyLivingCost) : t('settings.projection.living_cost_unset')}</li>
          <li>{t('settings.projection.inflation')} : {percent(a.inflation)}</li>
        </ul>
        <Link to="/settings/projection" className="text-sm underline underline-offset-2">{t('dashboard.projection.edit_assumptions')}</Link>
      </Section>
    </>
  );
}

function Pension({ d, money }: { d: Departure; money: (amount: string) => string }) {
  return (
    <div className="space-y-0.5">
      <div className="tabular-nums">{d.pension ? money(d.pension) : '—'}</div>
      {d.sources.map((s) => (
        <div key={s.source} className="text-sm">
          <Link to="/income/$id" params={{ id: s.source }} className="underline underline-offset-2">{s.name}</Link>
        </div>
      ))}
    </div>
  );
}

function Warnings({ data }: { data: RetirementView }) {
  const { t } = useTranslation();
  // living_cost_missing reads in the independence figure itself.
  const shown = data.warnings.filter((w) => w.code !== 'living_cost_missing');
  if (shown.length === 0) return null;
  return (
    <ul className="space-y-0.5 text-sm text-status-warning-text">
      {shown.map((w) => (
        <li key={`${w.code}-${w.detail}`}>
          {w.code === 'income_end_extended' ? t('dashboard.projection.warning_income_end_extended', { name: w.detail })
            : w.code === 'savings_exhausted' ? t('dashboard.projection.warning_savings_exhausted', { month: w.detail ? month(w.detail) : '' })
            : w.code === 'fx_missing' ? t('dashboard.projection.warning_fx_missing', { code: w.detail })
            : w.code}
        </li>
      ))}
    </ul>
  );
}
