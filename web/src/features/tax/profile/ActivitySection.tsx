import { useTranslation } from 'react-i18next';
import { SelectField } from '@/components/form/SelectField';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { STATUSES } from '../api';
import { percentText } from '../format';
import { DeclarantGrid } from './DeclarantGrid';
import { AdultInput, errorText, type SectionProps } from './fields';
import '@/locales/tax';

/** What turns each declarant's gross pay into taxable income: status, contribution rate, actual expenses. */
export function ActivitySection({ estimate, draft }: SectionProps) {
  const { t } = useTranslation();
  const statuses = draft.value.statuses;
  return (
    <Section id="activite" title={t('tax.profile.activity')}>
      <DeclarantGrid label={t('tax.profile.activity')} adults={estimate.adults} rows={[
        { key: 'status', label: t('tax.profile.status_short'), hint: t('tax.profile.status_hint'),
          cell: (a, c) => <SelectField {...c} value={statuses[a.id] ?? 'non_executive'} onValueChange={(v) => draft.set({ statuses: { ...statuses, [a.id]: v } })}
            options={STATUSES.map((s) => ({ value: s, label: t(`tax.status.${s}`) }))} /> },
        { key: 'rate', label: t('tax.profile.rate_short'), hint: t('tax.profile.rate_hint'),
          error: (id) => errorText(t, draft.errors, `rates.${id}`),
          cell: (a, c) => {
            const observed = estimate.observedRates[a.id];
            return (
              <>
                <AdultInput draft={draft} map="rates" adult={a.id} control={c}
                  placeholder={t(`tax.profile.rate_default.${(statuses[a.id] ?? 'non_executive') as (typeof STATUSES)[number]}`)} />
                {observed ? (
                  <span className="block text-sm text-muted-foreground">
                    <span>{t('tax.profile.observed_rate_use', { rate: percentText(t, observed) })}</span>{' · '}
                    {/* The server's rate as typed in the field (`22,85`): text only, nothing computed. */}
                    <Button type="button" variant="link" className="h-auto p-0 text-sm"
                      onClick={() => draft.set({ rates: { ...draft.value.rates, [a.id]: observed.replace('.', ',') } })}>{t('tax.profile.use')}</Button>
                  </span>
                ) : null}
              </>
            );
          } },
        { key: 'expenses', label: t('tax.profile.expenses_actual_short'), hint: t('tax.profile.expenses_actual_hint'),
          error: (id) => errorText(t, draft.errors, `expenses.${id}`),
          cell: (a, c) => <AdultInput draft={draft} map="expenses" adult={a.id} control={c} placeholder={t('tax.profile.expenses_default')} /> },
      ]} />
    </Section>
  );
}
