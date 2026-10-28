import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { Section } from '@/components/page/Section';
import { Input } from '@/components/ui/input';
import { errorText, type SectionProps } from './fields';
import '@/locales/tax';

/** The household: the dependents the declaration counts. */
export function HouseholdSection({ estimate, draft }: SectionProps) {
  const { t } = useTranslation();
  const d = draft.value;
  return (
    <Section id="foyer" title={t('tax.profile.household')}>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('tax.profile.dependents')} error={errorText(t, draft.errors, 'dependents')}>
          {(c) => <Input inputMode="numeric" {...c} placeholder={t('tax.profile.dependents_default', { count: estimate.dependents })} value={d.dependents} onChange={(e) => draft.set({ dependents: e.target.value })} />}
        </Field>
        <Field label={t('tax.profile.under6')} error={errorText(t, draft.errors, 'under6')}>
          {(c) => <Input inputMode="numeric" {...c} value={d.under6} onChange={(e) => draft.set({ under6: e.target.value })} />}
        </Field>
      </div>
    </Section>
  );
}
