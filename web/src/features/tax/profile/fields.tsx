import type { TFunction } from 'i18next';
import { useTranslation } from 'react-i18next';
import { Field, type FieldControl } from '@/components/form/Field';
import { Input } from '@/components/ui/input';
import type { TaxEstimate } from '../api';
import type { DraftErrors, DraftField, ProfileDraft } from './draft';
import type { Draft } from './useProfileDraft';
import '@/locales/tax';

/** What every section of the profile tab reads: the estimate, and the page's draft. */
export type SectionProps = { estimate: TaxEstimate; draft: Draft };

const FORMATS = { amount: 'tax.amount_format', rate: 'tax.rate_format', count: 'tax.count_format' } as const;
/** The message under a field the API would refuse. */
export function errorText(t: TFunction, errors: DraftErrors, field: DraftField): string | undefined {
  const kind = errors[field];
  return kind ? t(FORMATS[kind]) : undefined;
}

/** A text input bound to one key of a per-adult map (`rates`, `ceilings`…). */
/** `locked`: the notice's value, shown and not editable; the stored value (never the notice's) is what goes back on save. */
export function AdultInput({ draft, map, adult, control, placeholder, locked }: {
  draft: Draft; map: 'rates' | 'expenses' | 'unused' | 'ceilings' | 'expiring' | 'exempt'; adult: string;
  control: FieldControl & { 'aria-labelledby'?: string }; placeholder?: string; locked?: string;
}) {
  const values = draft.value[map];
  return <Input inputMode="decimal" {...control} placeholder={placeholder} disabled={locked !== undefined} value={locked ?? values[adult] ?? ''}
    onChange={(e) => draft.set({ [map]: { ...values, [adult]: e.target.value } })} />;
}

/** One of the household's amounts, with its label, hint and form box. */
export function AmountField({ draft, name, hint, badge }: {
  draft: Draft; name: keyof ProfileDraft['amounts']; hint?: string; badge?: string;
}) {
  const { t } = useTranslation();
  const amounts = draft.value.amounts;
  return (
    <Field label={t(`tax.profile.${name}`)} hint={hint} badge={badge} error={errorText(t, draft.errors, `amounts.${name}`)}>
      {(c) => <Input inputMode="decimal" {...c} value={amounts[name]} onChange={(e) => draft.set({ amounts: { ...amounts, [name]: e.target.value } })} />}
    </Field>
  );
}
