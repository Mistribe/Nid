import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { Section } from '@/components/page/Section';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { errorText, type SectionProps } from './fields';
import '@/locales/tax';

/**
 * The household: the dependents the declaration counts (alternate custody and disability among them), box T for a single adult, a disability
 * card per adult, and the department that dates the filing deadline (#409).
 */
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
        <Field label={t('tax.profile.alternate')} hint={t('tax.profile.alternate_hint')} badge="H" error={errorText(t, draft.errors, 'alternate')}>
          {(c) => <Input inputMode="numeric" {...c} value={d.alternate} onChange={(e) => draft.set({ alternate: e.target.value })} />}
        </Field>
        <Field label={t('tax.profile.disabled_dependents')} hint={t('tax.profile.disabled_dependents_hint')} badge="G" error={errorText(t, draft.errors, 'disabledChildren')}>
          {(c) => <Input inputMode="numeric" {...c} value={d.disabledChildren} onChange={(e) => draft.set({ disabledChildren: e.target.value })} />}
        </Field>
        <Field label={t('tax.profile.department')} hint={t('tax.profile.department_hint')} error={errorText(t, draft.errors, 'department')}>
          {(c) => <Input {...c} autoComplete="off" maxLength={3} value={d.department} onChange={(e) => draft.set({ department: e.target.value })} />}
        </Field>
      </div>
      <div className="mt-3 grid gap-3">
        {estimate.adults.length === 1 && (
          <Field inline label={t('tax.profile.cohabiting')} hint={t('tax.profile.cohabiting_hint')}>
            {(c) => <Checkbox {...c} checked={d.cohabiting} onCheckedChange={(v) => draft.set({ cohabiting: v === true })} />}
          </Field>
        )}
        {estimate.adults.map((a) => (
          <Field key={a.id} inline label={t('tax.profile.disabled', { name: a.displayName })} hint={t('tax.profile.disabled_hint')} badge="P">
            {(c) => <Checkbox {...c} checked={d.disabled.includes(a.id)}
              onCheckedChange={(v) => draft.set({ disabled: v === true ? [...d.disabled.filter((x) => x !== a.id), a.id] : d.disabled.filter((x) => x !== a.id) })} />}
          </Field>
        ))}
      </div>
    </Section>
  );
}
