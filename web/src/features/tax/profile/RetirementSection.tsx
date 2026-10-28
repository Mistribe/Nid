import { useState } from 'react';
import { flushSync } from 'react-dom';
import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { formatCurrency } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { DeclarantGrid } from './DeclarantGrid';
import { AdultInput, errorText, type SectionProps } from './fields';
import '@/locales/tax';

/** Each declarant's retirement savings ceiling, from the tax notice; the unused ceilings only stand in for a missing notice. */
export function RetirementSection({ estimate, draft }: SectionProps) {
  const { t } = useTranslation();
  const d = draft.value;
  // A fallback for a missing notice: folded unless already typed.
  const [open, setOpen] = useState<Record<string, boolean>>({});
  // The server's ceiling for each adult, and where it comes from (the notice, or estimated on last year's income).
  // What the notice supplies shows its value, locked, with the notice's year in place of the source.
  const carried = estimate.carriedFrom;
  const fromNotice = carried ? <span className="block text-sm text-muted-foreground">{t('tax.profile.from_notice', { year: carried.year })}</span> : null;
  const perOf = (adult: string) => estimate.result.declarants.find((x) => x.person === adult)?.per;
  return (
    <Section id="retraite" title={t('tax.profile.retirement')}>
      <DeclarantGrid label={t('tax.profile.retirement')} adults={estimate.adults} rows={[
        { key: 'ceiling', label: t('tax.profile.per_ceiling'), hint: t('tax.profile.per_ceiling_hint'),
          error: (id) => errorText(t, draft.errors, `ceilings.${id}`),
          cell: (a, c) => {
            const locked = carried?.perCeilings[a.id];
            if (locked !== undefined) return <><AdultInput draft={draft} map="ceilings" adult={a.id} control={c} locked={locked} />{fromNotice}</>;
            const per = perOf(a.id);
            const estimated = per?.source === 'estimated';
            return (
              <>
                <AdultInput draft={draft} map="ceilings" adult={a.id} control={c}
                  placeholder={estimated ? t('tax.profile.per_estimated_placeholder', { amount: formatCurrency(per.ceiling, 'EUR', { maximumFractionDigits: 0 }) }) : undefined} />
                {per ? (
                  <span className={estimated ? 'block text-sm font-medium text-status-warning-text' : 'block text-sm text-status-good-text'}>
                    {t(estimated ? 'tax.profile.per_source_estimated' : 'tax.profile.per_source_notice')}
                  </span>
                ) : null}
              </>
            );
          } },
        { key: 'expiring', label: t('tax.profile.per_expiring'), error: (id) => errorText(t, draft.errors, `expiring.${id}`),
          cell: (a, c) => {
            const locked = carried?.perCeilingsExpiring[a.id];
            return <><AdultInput draft={draft} map="expiring" adult={a.id} control={c} locked={locked} />{locked !== undefined ? fromNotice : null}</>;
          } },
        { key: 'unused', label: t('tax.profile.per_unused_short'), hint: t('tax.profile.per_unused_hint'),
          error: (id) => errorText(t, draft.errors, `unused.${id}`),
          // Shown once asked for, or while it holds a value (saved or typed): clearing a saved value keeps the field.
          cell: (a, c) => (carried?.perUnusedCeilings[a.id] !== undefined
            ? <><AdultInput draft={draft} map="unused" adult={a.id} control={c} locked={carried.perUnusedCeilings[a.id]} />{fromNotice}</>
            : open[a.id] || Boolean(draft.saved.unused[a.id]) || Boolean(d.unused[a.id]) || draft.errors[`unused.${a.id}`]
            ? <AdultInput draft={draft} map="unused" adult={a.id} control={c} />
            : <Button type="button" variant="outline" className="w-full border-dashed text-primary"
              onClick={() => {
                // The button gives way to the field: the focus follows it.
                flushSync(() => setOpen({ ...open, [a.id]: true }));
                document.getElementById(c.id)?.focus();
              }}>{t('tax.profile.per_unused_add')}</Button>) },
      ]} />
      {estimate.adults.length > 1 ? (
        <Field inline label={t('tax.profile.per_pooling')} badge="6QR">
          {(c) => <Checkbox {...c} checked={d.pooling} onCheckedChange={(v) => draft.set({ pooling: v === true })} />}
        </Field>
      ) : null}
    </Section>
  );
}
