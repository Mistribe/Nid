import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatCurrency } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { Skeleton } from '@/components/ui/skeleton';
import { PERSONS } from '../subscriptions/api';
import { AssumptionsDialog } from './AssumptionsDialog';
import { RetirementDialog } from './RetirementDialog';
import { SettingsFrame } from './SettingsFrame';
import { ASSET_CLASSES, PROJECTION_ASSUMPTIONS } from './api';

/** A server percent as a French one: « 1.5 » reads « 1,5 % » (a string swap, nothing computed). */
const percent = (value: string) => `${value.replace('.', ',')} %`;

/** yyyy-mm-dd as jj/mm/aaaa, the way the date fields read it. */
const dayFirst = (iso: string) => iso.split('-').reverse().join('/');

const ASSUMPTIONS = 'assumptions';

/** Réglages › Projection: the household's assumptions and each person's retirement, both read by the net worth projection. */
export function ProjectionSettingsPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const assumptions = $api.useQuery('get', PROJECTION_ASSUMPTIONS);
  const persons = $api.useQuery('get', PERSONS);
  // Which dialog is open: the assumptions', or a person's id.
  const [editing, setEditing] = useState<string | null>(null);
  const [saved, setSaved] = useState('');
  const adults = persons.data?.filter((p) => p.kind !== 'dependent') ?? [];
  const person = adults.find((p) => p.id === editing);
  const close = () => setEditing(null);
  const done = (message: string) => { setEditing(null); setSaved(message); };
  const open = (what: string) => { setSaved(''); setEditing(what); };

  return (
    <SettingsFrame title={t('settings.projection.title')}>
      <p role="status" aria-live="polite" className="text-sm">{saved}</p>
      <Section title={t('settings.projection.assumptions')}>
        {assumptions.data ? (
          <>
            <ul className="space-y-1 text-sm">
              {ASSET_CLASSES.map((c) => <li key={c}>{t(`settings.projection.class.${c}`)} : {percent(assumptions.data.classRates[c] ?? '')}</li>)}
              <li>{t('settings.projection.inflation')} : {percent(assumptions.data.inflation)}</li>
              <li>{t('settings.projection.monthly_savings')} : {assumptions.data.monthlySavings ? formatCurrency(assumptions.data.monthlySavings, assumptions.data.currency) : t('settings.projection.savings_auto_summary')}</li>
              <li>{t('settings.projection.savings_class')} : {t(`settings.projection.class.${assumptions.data.savingsClass as (typeof ASSET_CLASSES)[number]}`)}</li>
              <li>{t('settings.projection.freed')} : {t(assumptions.data.freedPaymentsToSavings ? 'settings.projection.yes' : 'settings.projection.no')}</li>
            </ul>
            <Button variant="outline" aria-label={t('settings.projection.edit_assumptions')} onClick={() => open(ASSUMPTIONS)}>{t('settings.projection.edit')}</Button>
          </>
        ) : assumptions.error ? <ProblemMessage problem={present(toProblem(assumptions.error))} />
          : <Skeleton aria-busy="true" className="h-32"><span className="sr-only">{t('settings.projection.loading')}</span></Skeleton>}
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
