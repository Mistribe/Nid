import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import { Segmented } from '@/components/form/Segmented';
import { Section } from '@/components/page/Section';
import { AmountField, type SectionProps } from './fields';
import '@/locales/tax';

const OPTIONS = ['flat', 'scale'] as const;

/** #234: the capital income's option, flat tax or progressive scale (box 2OP), last year's deductible CSG (6DE), the earlier securities' losses. */
export function CapitalSection({ draft }: SectionProps) {
  const { t } = useTranslation();
  const hintId = useId();
  const option = draft.value.capitalScale ? 'scale' : 'flat';
  return (
    <Section id="placements" title={t('tax.profile.capital')}>
      <div className="grid items-start gap-3 sm:grid-cols-2">
        <div className="space-y-1">
          <span className="block text-sm">{t('tax.profile.capital_option')}</span>
          <Segmented aria-label={t('tax.profile.capital_option')} aria-describedby={hintId} value={option}
            onValueChange={(v) => draft.set({ capitalScale: v === 'scale' })} options={OPTIONS.map((o) => ({ value: o, label: t(`tax.capital_option.${o}`) }))} />
          <span id={hintId} className="block text-sm text-muted-foreground">{t(`tax.profile.capital_option_hint.${option}`)}</span>
        </div>
        <AmountField draft={draft} name="deductibleCsg" hint={t('tax.profile.deductible_csg_hint')} badge="6DE" />
        <AmountField draft={draft} name="capitalLossesCarried" hint={t('tax.profile.capital_losses_hint')} />
      </div>
    </Section>
  );
}
