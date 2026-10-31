import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Controller, useFieldArray, useForm, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { PROPERTIES } from '../../properties/api';
import { INSURANCE, invalidateSubscriptions, type Detail } from '../api';
import { Reload } from '../edits/Reload';
import { useSubmitErrors } from '../edits/useSubmitErrors';
import { INSURANCE_KINDS } from './InsuranceSection';
import '@/locales/forms';
import '@/locales/subscriptions';

const schema = z.object({
  kind: z.string(),
  propertyId: z.string(),
  insuredLabel: z.string(),
  policyNumber: z.string(),
  insurerContact: z.string(),
  guarantees: z.array(z.object({ label: z.string(), ceiling: z.string(), deductible: z.string() })),
});
type Values = z.infer<typeof schema>;
type Name = Path<Values>;

const TOP = ['kind', 'propertyId', 'insuredLabel', 'policyNumber', 'insurerContact'];
/** The form field an API path belongs to (`guarantees[2].label` → `guarantees.2.label`); none for the rest, which show above the form. */
const nameOf = (path: string): Name | undefined => {
  if (TOP.includes(path)) return path as Name;
  const row = /^guarantees\[(\d+)\]\.(label|ceiling|deductible)$/.exec(path);
  return row ? (`guarantees.${row[1]}.${row[2]}` as Name) : undefined;
};

/** A blank text is no text: the API keeps `null` for what the household left empty. */
const text = (v: string) => (v.trim() === '' ? null : v.trim());

/**
 * Design 2026-10-30-contract-terms-insurance §10: the whole policy in a dialog; « Enregistrer » replaces it (PUT). The linked
 * property keeps its own option even when the list does not hold it.
 */
export function InsuranceForm({ detail, titleId, onDone }: { detail: Detail; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const current = detail.insurance;
  const form = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: {
      kind: current?.kind ?? 'home', propertyId: current?.property?.id ?? '',
      insuredLabel: current?.insuredLabel ?? '', policyNumber: current?.policyNumber ?? '', insurerContact: current?.insurerContact ?? '',
      guarantees: (current?.guarantees ?? []).map((g) => ({ label: g.label, ceiling: g.ceiling ?? '', deductible: g.deductible ?? '' })),
    },
  });
  const rows = useFieldArray({ control: form.control, name: 'guarantees' });
  const errors = useSubmitErrors(form.setError, nameOf);
  const save = $api.useMutation('put', INSURANCE, {
    onSuccess: () => { void invalidateSubscriptions(queryClient); onDone(t('insurance.saved')); },
    onError: (error) => errors.onError(error),
  });
  const properties = $api.useQuery('get', PROPERTIES, { params: { query: { scope: 'me' } } });
  const items = properties.data?.items ?? [];
  const linked = current?.property ?? null;
  const options = [
    { value: '', label: t('insurance.property_none') },
    ...(linked ? [{ value: linked.id, label: items.find((p) => p.id === linked.id)?.name ?? linked.name }] : []),
    ...items.filter((p) => p.id !== linked?.id).map((p) => ({ value: p.id, label: p.name })),
  ];
  const noProperty = properties.isSuccess && items.length === 0 && !linked;

  // A hidden link (the reader cannot read the property, so `property` is null) is not a missing one: the PUT replaces the whole,
  // so an untouched empty field asks the server to keep it (it never says whether one exists).
  const keepsHidden = !!current && !current.property;

  const submit = form.handleSubmit((values) => {
    errors.clear();
    // The blank rows leave the form first, so the list sent and the rows shown share their indices (a refusal names a row).
    const blank = values.guarantees.flatMap((g, i) => (g.label.trim() === '' && g.ceiling.trim() === '' && g.deductible.trim() === '' ? [i] : []));
    if (blank.length > 0) rows.remove(blank);
    save.mutate({
      params: { path: { id: detail.id } },
      body: {
        kind: values.kind, propertyId: values.propertyId === '' ? null : values.propertyId,
        keepProperty: keepsHidden && values.propertyId === '',
        insuredLabel: text(values.insuredLabel), policyNumber: text(values.policyNumber), insurerContact: text(values.insurerContact),
        guarantees: values.guarantees
          .filter((_, i) => !blank.includes(i))
          .map((g) => ({ label: g.label.trim(), ceiling: text(g.ceiling), deductible: text(g.deductible) })),
      },
    });
  });

  const e = form.formState.errors;
  return (
    <form aria-labelledby={titleId} onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3" noValidate>
      {errors.problem ? <><ProblemMessage problem={errors.problem} />{errors.problem.kind === 'conflict' ? <Reload /> : null}</> : null}
      <Controller control={form.control} name="kind" render={({ field }) => (
        <Field label={t('insurance.kind')} error={e.kind?.message}>
          {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
            options={INSURANCE_KINDS.map((k) => ({ value: k, label: t(`insurance.kind_options.${k}`) }))} />}
        </Field>
      )} />
      <Controller control={form.control} name="propertyId" render={({ field }) => (
        <Field label={t('insurance.property')} error={e.propertyId?.message} hint={noProperty ? t('insurance.property_hint_empty') : undefined}>
          {(c) => <ComboboxField {...c} value={field.value} onValueChange={field.onChange} options={options}
            placeholder={t('insurance.property_placeholder')} searchPlaceholder={t('insurance.property_search')} empty={t('insurance.property_empty')} />}
        </Field>
      )} />
      <Field label={t('insurance.insured_label')} error={e.insuredLabel?.message}>
        {(c) => <Input {...c} {...form.register('insuredLabel')} />}
      </Field>
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('insurance.policy_number')} error={e.policyNumber?.message}>
          {(c) => <Input {...c} {...form.register('policyNumber')} />}
        </Field>
        <Field label={t('insurance.insurer_contact')} hint={t('insurance.hint_contact')} error={e.insurerContact?.message}>
          {(c) => <Input {...c} {...form.register('insurerContact')} />}
        </Field>
      </div>
      <fieldset className="space-y-3">
        <legend className="text-sm font-medium">{t('insurance.guarantees')}</legend>
        <p className="text-sm text-muted-foreground">{t('insurance.guarantee_hint')}</p>
        {rows.fields.map((row, i) => {
          const n = i + 1;
          const re = e.guarantees?.[i];
          return (
            <div key={row.id} className="grid gap-2 rounded-md border p-3 sm:grid-cols-[2fr_1fr_1fr_auto] sm:items-start">
              <Field label={t('insurance.guarantee_label', { n })} error={re?.label?.message}>
                {(c) => <Input {...c} {...form.register(`guarantees.${i}.label`)} />}
              </Field>
              <Field label={t('insurance.guarantee_ceiling', { n })} error={re?.ceiling?.message}>
                {(c) => <Input {...c} {...form.register(`guarantees.${i}.ceiling`)} />}
              </Field>
              <Field label={t('insurance.guarantee_deductible', { n })} error={re?.deductible?.message}>
                {(c) => <Input {...c} {...form.register(`guarantees.${i}.deductible`)} />}
              </Field>
              <Button type="button" variant="outline" size="sm" className="sm:mt-6" onClick={() => rows.remove(i)}>{t('insurance.remove_guarantee', { n })}</Button>
            </div>
          );
        })}
        <Button type="button" variant="outline" size="sm" onClick={() => rows.append({ label: '', ceiling: '', deductible: '' })}>{t('insurance.add_guarantee')}</Button>
      </fieldset>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={save.isPending}>{t('insurance.submit')}</Button>
      </div>
    </form>
  );
}
