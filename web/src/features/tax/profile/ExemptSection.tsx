import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { Section } from '@/components/page/Section';
import { Folded } from '../../loans/new/Folded';
import { ZERO } from '../format';
import { AdultInput, errorText, type SectionProps } from './fields';
import '@/locales/tax';

/** Exempt incomes: rare, folded unless a value is saved; counted in the reference income only. */
export function ExemptSection({ estimate, draft }: SectionProps) {
  const { t } = useTranslation();
  const exempt = draft.value.exempt;
  const [open, setOpen] = useState(() => Object.values(exempt).some((v) => v.trim() !== '' && !ZERO.test(v)));
  const hasError = estimate.adults.some((a) => draft.errors[`exempt.${a.id}`]);
  return (
    <Section id="exoneres" title={t('tax.profile.exempt_title')}>
      <Folded label={t('tax.profile.exempt_toggle')} hasError={hasError} open={open} onOpenChange={setOpen}>
        <div className="grid gap-3 sm:grid-cols-2">
          {estimate.adults.map((a) => (
            <Field key={a.id} label={t('tax.profile.exempt', { name: a.displayName })} hint={t('tax.profile.exempt_hint')}
              error={errorText(t, draft.errors, `exempt.${a.id}`)}>
              {(c) => <AdultInput draft={draft} map="exempt" adult={a.id} control={c} />}
            </Field>
          ))}
        </div>
      </Folded>
    </Section>
  );
}
