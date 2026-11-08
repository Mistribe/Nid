import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
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
      <Section title={t('settings.projection.assumptions')}>
        {assumptions.data ? (
          <>
            <ul className="space-y-1 text-sm">
              {ASSET_CLASSES.map((c) => <li key={c}>{t(`settings.projection.class.${c}`)} : {withYears(assumptions.data.classRates[c] ?? '', assumptions.data.classRatesByYear[c], t('settings.projection.then'))}</li>)}
              <li>{t('settings.projection.inflation')} : {withYears(assumptions.data.inflation, assumptions.data.inflationByYear, t('settings.projection.then'))}</li>
              <li>{t('settings.projection.income_growth_summary')} : {t('settings.projection.income_growth_value', { percent: percent(assumptions.data.incomeGrowth) })}</li>
              <li>{t('settings.projection.monthly_savings')} : {assumptions.data.monthlySavings ? formatCurrency(assumptions.data.monthlySavings, assumptions.data.currency) : t('settings.projection.savings_auto_summary')}</li>
              <li>{t('settings.projection.savings_class')} : {t(`settings.projection.class.${assumptions.data.savingsClass as (typeof ASSET_CLASSES)[number]}`)}</li>
              <li>{t('settings.projection.freed')} : {t(assumptions.data.freedPaymentsToSavings ? 'settings.projection.yes' : 'settings.projection.no')}</li>
            </ul>
            <Button variant="outline" aria-label={t('settings.projection.edit_assumptions')} onClick={() => open(ASSUMPTIONS)}>{t('settings.projection.edit')}</Button>
          </>
        ) : assumptions.error ? <ProblemMessage problem={present(toProblem(assumptions.error))} />
          : <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('settings.projection.loading')}</span></Skeleton>}
      </Section>
      <Section title={t('settings.projection.observed')}>
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
        <ul className="space-y-2 text-sm">
          {adults.map((p) => (
            <li key={p.id} className="flex flex-wrap items-center gap-x-4 gap-y-1">
              <span className="font-medium">{p.displayName}</span>
              <span>{p.birthDate ? dayFirst(p.birthDate) : t('settings.projection.no_birth_date')}</span>
              <span>{t('settings.projection.age_years', { count: p.retirementAge })}</span>
              <Button variant="outline" size="sm" aria-label={t('settings.projection.edit_retirement', { name: p.displayName })} onClick={() => open(p.id)}>{t('settings.projection.edit')}</Button>
            </li>
          ))}
        </ul>
      </Section>
      {editing === ASSUMPTIONS && assumptions.data ? <AssumptionsDialog current={assumptions.data} onClose={close} onDone={done} /> : null}
      {person ? <RetirementDialog person={person} onClose={close} onDone={done} /> : null}
    </SettingsFrame>
  );
}
