import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { InfoTip } from '@/components/InfoTip';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { SeriesLink } from '@/components/page/SeriesLink';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { monthText } from '@/components/LocalDate';
import { PERSONS } from '@/api/persons';
import { AssumptionsDialog } from './AssumptionsDialog';
import { RetirementDialog } from './RetirementDialog';
import { SettingsFrame } from './SettingsFrame';
import { ASSET_CLASSES, INFLATION, PROJECTION_ASSUMPTIONS } from './api';

/** A server percent as a French one: « 1.5 » reads « 1,5 % » (a string swap, nothing computed). */
const percent = (value: string) => `${value.replace('.', ',')} %`;

/** A long-term rate and the typed years before it: « 2026 2,5 %, 2027 2,2 %, puis 2 % » (the server already drops past years). */
const withYears = (longTerm: string, byYear: Record<string, string> | undefined, then: string) => {
  const years = Object.entries(byYear ?? {}).sort(([a], [b]) => a.localeCompare(b)).map(([y, v]) => `${y} ${percent(v)}`);
  return years.length > 0 ? `${years.join(', ')}, ${then} ${percent(longTerm)}` : percent(longTerm);
};

/** yyyy-mm-dd as jj/mm/aaaa, the way the date fields read it. */
const dayFirst = (iso: string) => iso.split('-').reverse().join('/');

const ASSUMPTIONS = 'assumptions';

/** A label and the InfoTip that explains it, for a table header (`end`: a right-aligned column). */
const Tipped = ({ label, tip, end = false }: { label: string; tip: string; end?: boolean }) => (
  <span className={`inline-flex items-center gap-1.5 ${end ? 'justify-end' : ''}`}>{label}<InfoTip label={label}>{tip}</InfoTip></span>
);

/** A theme of assumptions: dense label / figure rows under a small heading. */
const Group = ({ title, children }: { title: string; children: ReactNode }) => (
  <div className="space-y-1">
    <h3 className="text-label text-muted-foreground">{title}</h3>
    <dl className="divide-y text-sm">{children}</dl>
  </div>
);

const Row = ({ label, tip, children }: { label: string; tip: string; children: ReactNode }) => (
  <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-0.5 py-2">
    <dt className="inline-flex items-center gap-1.5"><span>{label}</span><InfoTip label={label}>{tip}</InfoTip></dt>
    <dd className="text-right tabular-nums">{children}</dd>
  </div>
);

/** Réglages › Projection: the household's assumptions and each person's retirement, both read by the net worth projection. */
export function ProjectionSettingsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const assumptions = $api.useQuery('get', PROJECTION_ASSUMPTIONS);
  const persons = $api.useQuery('get', PERSONS);
  const observed = $api.useQuery('get', INFLATION);
  // Which dialog is open: the assumptions', or a person's id.
  const [editing, setEditing] = useState<string | null>(null);
  const adults = persons.data?.filter((p) => p.kind !== 'dependent') ?? [];
  const person = adults.find((p) => p.id === editing);
  const close = () => setEditing(null);
  const done = (message: string) => { setEditing(null); toast.success(message); };
  const open = (what: string) => setEditing(what);

  return (
    <SettingsFrame title={t('settings.projection.title')}>
      <Section title={t('settings.projection.assumptions')} actions={<>
        <SeriesLink keys={['projection:inflation']} />
        {assumptions.data ? <Button variant="outline" size="sm" aria-label={t('settings.projection.edit_assumptions')} onClick={() => open(ASSUMPTIONS)}>{t('settings.projection.edit')}</Button> : null}
      </>}>
        {assumptions.data ? (
          <div className="grid gap-x-10 gap-y-6 lg:grid-cols-2">
            <div className="space-y-1">
              <h3 className="text-label text-muted-foreground">{t('settings.projection.group_returns')}</h3>
              <Table>
                <TableHeader>
                  <TableRow>
                    <TableHead>{t('settings.projection.class_column')}</TableHead>
                    <TableHead className="text-right"><Tipped label={t('settings.projection.rate_column')} tip={t('settings.projection.tip.rate')} end /></TableHead>
                    <TableHead className="text-right"><Tipped label={t('settings.projection.volatility_column')} tip={t('settings.projection.tip.volatility')} end /></TableHead>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {ASSET_CLASSES.map((c) => (
                    <TableRow key={c}>
                      <TableCell>{t(`settings.projection.class.${c}`)}</TableCell>
                      <TableCell className="text-right whitespace-normal tabular-nums">{withYears(assumptions.data.classRates[c] ?? '', assumptions.data.classRatesByYear[c], t('settings.projection.then'))}</TableCell>
                      <TableCell className="text-right tabular-nums">{percent(assumptions.data.classVolatility[c] ?? '0')}</TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
            <div className="space-y-6">
              <Group title={t('settings.projection.group_prices')}>
                <Row label={t('settings.projection.inflation')} tip={t('settings.projection.tip.inflation')}>{withYears(assumptions.data.inflation, assumptions.data.inflationByYear, t('settings.projection.then'))}</Row>
                <Row label={t('settings.projection.income_growth_summary')} tip={t('settings.projection.tip.income_growth')}>{t('settings.projection.income_growth_value', { percent: percent(assumptions.data.incomeGrowth) })}</Row>
              </Group>
              <Group title={t('settings.projection.group_savings')}>
                <Row label={t('settings.projection.monthly_savings')} tip={t('settings.projection.tip.monthly_savings')}>{assumptions.data.monthlySavings ? formatCurrency(assumptions.data.monthlySavings, assumptions.data.currency) : t('settings.projection.savings_auto_summary')}</Row>
                <Row label={t('settings.projection.savings_class')} tip={t('settings.projection.tip.savings_class')}>{t(`settings.projection.class.${assumptions.data.savingsClass as (typeof ASSET_CLASSES)[number]}`)}</Row>
                <Row label={t('settings.projection.freed')} tip={t('settings.projection.tip.freed')}>{t(assumptions.data.freedPaymentsToSavings ? 'settings.projection.yes' : 'settings.projection.no')}</Row>
              </Group>
              <Group title={t('settings.projection.group_independence')}>
                <Row label={t('settings.projection.withdrawal_rate')} tip={t('settings.projection.tip.withdrawal_rate')}>{percent(assumptions.data.withdrawalRate)}</Row>
                <Row label={t('settings.projection.living_cost_summary')} tip={t('settings.projection.tip.living_cost')}>{Number(assumptions.data.monthlyLivingCost) > 0 ? formatCurrency(assumptions.data.monthlyLivingCost, assumptions.data.currency) : t('settings.projection.living_cost_unset')}</Row>
              </Group>
            </div>
          </div>
        ) : assumptions.error ? <ProblemMessage problem={present(toProblem(assumptions.error))} />
          : <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('settings.projection.loading')}</span></Skeleton>}
      </Section>
      <Section title={t('settings.projection.observed')} actions={<SeriesLink keys={['cpi:yoy', 'projection:inflation']} />}>
        {observed.data?.lastPublished ? (
          <>
            {observed.data.last12Months ? (
              <p className="text-sm">{t('settings.projection.observed_last12', { month: monthText(observed.data.last12Months.to), percent: percent(observed.data.last12Months.percent) })}</p>
            ) : null}
            <Table className="max-w-xs">
              <TableHeader>
                <TableRow>
                  <TableHead>{t('settings.projection.observed_year')}</TableHead>
                  <TableHead className="text-right">{t('settings.projection.observed_percent')}</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {observed.data.years.map((y) => (
                  <TableRow key={y.year}>
                    <TableCell>{y.year}</TableCell>
                    <TableCell className="text-right tabular-nums">{percent(y.percent)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            <p className="text-xs text-muted-foreground">{t('settings.projection.observed_source', { month: monthText(observed.data.lastPublished) })}</p>
          </>
        ) : observed.error ? <ProblemMessage problem={present(toProblem(observed.error))} />
          : observed.data ? <p className="text-sm">{t('settings.projection.observed_unavailable')}</p>
            : <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('settings.projection.observed_loading')}</span></Skeleton>}
      </Section>
      <Section title={t('settings.projection.retirement')}>
        {persons.error ? <ProblemMessage problem={present(toProblem(persons.error))} /> : null}
        {adults.length > 0 ? (
          <Table className="max-w-2xl">
            <TableHeader>
              <TableRow>
                <TableHead>{t('settings.projection.person_column')}</TableHead>
                <TableHead><Tipped label={t('settings.projection.birth_date')} tip={t('settings.projection.tip.birth_date')} /></TableHead>
                <TableHead><Tipped label={t('settings.projection.departure_column')} tip={t('settings.projection.tip.retirement_age')} /></TableHead>
                <TableHead className="text-right"><span className="sr-only">{t('settings.projection.edit')}</span></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {adults.map((p) => (
                <TableRow key={p.id}>
                  <TableCell className="font-medium">{p.displayName}</TableCell>
                  <TableCell className="tabular-nums">{p.birthDate ? dayFirst(p.birthDate) : t('settings.projection.no_birth_date')}</TableCell>
                  <TableCell>{p.retirementMonths > 0 ? t('settings.projection.age_years_months', { years: p.retirementAge, months: p.retirementMonths }) : t('settings.projection.age_years', { count: p.retirementAge })}</TableCell>
                  <TableCell className="text-right">
                    <Button variant="outline" size="sm" aria-label={t('settings.projection.edit_retirement', { name: p.displayName })} onClick={() => open(p.id)}>{t('settings.projection.edit')}</Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : null}
      </Section>
      {editing === ASSUMPTIONS && assumptions.data ? <AssumptionsDialog current={assumptions.data} onClose={close} onDone={done} /> : null}
      {person ? <RetirementDialog person={person} onClose={close} onDone={done} /> : null}
    </SettingsFrame>
  );
}
