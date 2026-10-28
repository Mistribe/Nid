import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { Section } from '@/components/page/Section';
import { Checkbox } from '@/components/ui/checkbox';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { REGIMES, regimeText } from '../format';
import { ENGAGEMENTS, suggestedDeficit } from './draft';
import { AmountField, type SectionProps } from './fields';
import '@/locales/tax';

/** The rental income's regime, the deficit carried over, and each let property's Pinel commitment. */
export function RentalSection({ estimate, draft }: SectionProps) {
  const { t } = useTranslation();
  const hintId = useId();
  const pinel = draft.value.pinel;
  const engagementOf = (property: string) => {
    const c = pinel.find((x) => x.property === property);
    return c ? `${c.initialYears}-${c.totalYears}` : '';
  };
  const setEngagement = (property: string, value: string) => {
    const rest = pinel.filter((x) => x.property !== property);
    if (!value) { draft.set({ pinel: rest }); return; }
    const [initialYears, totalYears] = value.split('-');
    const plus = pinel.find((x) => x.property === property)?.plus ?? false;
    draft.set({ pinel: [...rest, { property, initialYears: Number(initialYears), totalYears: Number(totalYears), startYear: null, plus }] });
  };
  if (!estimate.rentalProperties.length) return null;
  return (
    <Section id="location" title={t('tax.profile.rental')}>
      <div className="grid items-start gap-3 sm:grid-cols-2">
        <div className="space-y-1">
          <span className="block text-sm">{t('tax.profile.regime')}</span>
          <Segmented aria-label={t('tax.profile.regime')} aria-describedby={hintId} value={draft.value.regime}
            onValueChange={(regime) => draft.set({ regime })} options={REGIMES.map((r) => ({ value: r, label: regimeText(t, r) }))} />
          <span id={hintId} className="block text-sm text-muted-foreground">{t(`tax.profile.regime_hint.${draft.value.regime as (typeof REGIMES)[number]}`)}</span>
        </div>
        <AmountField draft={draft} name="rentalDeficitCarried" badge="4BD"
          hint={suggestedDeficit(estimate) && estimate.carriedFrom ? t('tax.profile.deficit_suggested', { year: estimate.carriedFrom.year }) : t('tax.profile.deficit_carried_hint')} />
      </div>
      <div className="rounded-xl border bg-card">
        <Table aria-label={t('tax.profile.pinel_table')}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('tax.profile.pinel_property')}</TableHead>
              <TableHead>{t('tax.profile.pinel_year')}</TableHead>
              <TableHead className="min-w-56">{t('tax.profile.pinel_engagement')}</TableHead>
              <TableHead>{t('tax.profile.pinel_plus')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {estimate.rentalProperties.map((property) => {
              const engaged = engagementOf(property.id) !== '';
              return (
                <TableRow key={property.id} className="hover:bg-transparent">
                  <TableHead scope="row" className="font-medium text-foreground">{property.name}</TableHead>
                  <TableCell className="tabular-nums text-muted-foreground">{property.acquiredIn}</TableCell>
                  <TableCell>
                    <SelectField id={`pinel-${property.id}`} aria-label={t('tax.profile.pinel_engagement_of', { name: property.name })}
                      value={engagementOf(property.id)} onValueChange={(v) => setEngagement(property.id, v)}
                      options={[{ value: '', label: t('tax.engagement.none') }, ...ENGAGEMENTS.map((e) => ({ value: e, label: t(`tax.engagement.${e}`) }))]} />
                  </TableCell>
                  <TableCell>
                    {engaged ? (
                      <Checkbox aria-label={t('tax.profile.pinel_plus_of', { name: property.name })}
                        checked={pinel.find((x) => x.property === property.id)?.plus ?? false}
                        onCheckedChange={(v) => draft.set({ pinel: pinel.map((x) => (x.property === property.id ? { ...x, plus: v === true } : x)) })} />
                    ) : <><span aria-hidden="true" className="text-muted-foreground">—</span><span className="sr-only">{t('tax.profile.not_applicable')}</span></>}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
        </Table>
      </div>
    </Section>
  );
}
