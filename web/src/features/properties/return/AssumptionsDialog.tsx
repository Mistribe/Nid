import { useQueryClient } from '@tanstack/react-query';
import { useRef } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { percent } from '../../loans/format';
import { fractionToPercent } from '../../loans/rate';
import { presentRefusal } from '../../loans/refusal';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { invalidateProperties, RETURN_ASSUMPTIONS, type PropertyDetail, type PropertyReturn } from '@/api/properties';
import { assumptionsBody, EMPTY, type AssumptionsForm } from './assumptionsBody';
import '@/locales/properties';

type Assumptions = PropertyReturn['assumptions'];
type Key = keyof AssumptionsForm;

const PATHS = Object.fromEntries(Object.keys(EMPTY).map((k) => [k, k])) as Record<string, Key>;
const RATES: readonly Key[] = ['valueGrowth', 'rentGrowth', 'chargesGrowth', 'resaleFees'];
const COUNTS: readonly Key[] = ['yearsAfterPinel', 'vacancyMonths'];
/** Spec §3: the income tax scale's marginal rates, as the API's fractions. */
const MARGINAL = ['0', '0.11', '0.3', '0.41', '0.45'];
/** A fraction of the API without its trailing zeros, the select's value: text only. */
const bare = (wire: string) => wire.replace(/(\.\d*?)0+$/, '$1').replace(/\.$/, '');

/** A field as typed from what the server uses; empty when it took its default, so a save keeps following the default. */
function typed(a: Assumptions, key: Key): string {
  if (a.defaulted.includes(key)) return '';
  const value = a[key];
  if (value === null || value === undefined) return '';
  if (RATES.includes(key)) return fractionToPercent(String(value));
  if (key === 'marginalRate') return bare(String(value));
  return String(value).replace('.', ',');
}

/** Property return spec §6: the assumptions in a dialog; an empty field keeps its default, which its hint gives. */
export function AssumptionsDialog({ property, assumptions, onClose }: { property: PropertyDetail; assumptions: Assumptions; onClose: () => void }) {
  const { t } = useTranslation();
  const dialog = useRef<{ close: () => void }>(null);
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<AssumptionsForm>({ defaultValues: Object.fromEntries(Object.keys(EMPTY).map((k) => [k, typed(assumptions, k as Key)])) as unknown as AssumptionsForm });
  const errors = useSubmitErrors(form.setError, PATHS);
  const save = $api.useMutation('put', RETURN_ASSUMPTIONS);
  const reset = $api.useMutation('delete', RETURN_ASSUMPTIONS);
  const done = () => { void invalidateProperties(queryClient); dialog.current?.close(); };
  const path = { params: { path: { id: property.id } } };

  const submit = form.handleSubmit((values) => {
    errors.clear();
    save.mutate({ ...path, body: assumptionsBody(values) }, { onSuccess: done, onError: (error) => errors.onError(error) });
  });
  const hint = (key: Key, value: string) => (assumptions.defaulted.includes(key) ? t('property_return.by_default', { value }) : undefined);
  const e = form.formState.errors;
  const text = (key: Key, label: string, defaultText: string) => (
    <Controller key={key} control={form.control} name={key}
      rules={COUNTS.includes(key) ? { pattern: { value: /^\s*\d*\s*$/, message: t('property_return.dialog.whole_number') } } : undefined}
      render={({ field }) => (
      <Field label={label} hint={hint(key, defaultText)} error={e[key]?.message}>
        {(c) => <Input {...c} inputMode="decimal" value={field.value} onChange={field.onChange} />}
      </Field>
    )} />
  );
  const problem = errors.problem ? presentRefusal(t, save.error) : null;

  return (
    <ModalDialog title={t('property_return.dialog.title')} onClose={onClose} dialogRef={dialog}>
      {(titleId) => (
        <form aria-labelledby={titleId} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
          {problem ? <ProblemMessage problem={problem} /> : null}
          <div className="grid gap-3 sm:grid-cols-2">
            {text('yearsAfterPinel', t('property_return.dialog.years_after_pinel'), String(assumptions.yearsAfterPinel))}
            {text('resalePrice', t('property_return.dialog.resale_price'), t('property_return.dialog.estimated'))}
            {text('valueGrowth', t('property_return.dialog.value_growth'), percent(t, assumptions.valueGrowth))}
            {text('rentGrowth', t('property_return.dialog.rent_growth'), percent(t, assumptions.rentGrowth))}
            {text('chargesGrowth', t('property_return.dialog.charges_growth'), percent(t, assumptions.chargesGrowth))}
            {text('vacancyMonths', t('property_return.dialog.vacancy_months'), String(assumptions.vacancyMonths))}
            {text('rentAfterPinel', t('property_return.dialog.rent_after_pinel'), t('property_return.dialog.current_rent'))}
            <Controller control={form.control} name="marginalRate" render={({ field }) => (
              <Field label={t('property_return.dialog.marginal_rate')} error={e.marginalRate?.message}>
                {(c) => (
                  <SelectField {...c} value={field.value} onValueChange={field.onChange}
                    options={[{ value: '', label: t('property_return.by_default', { value: percent(t, assumptions.marginalRate) }) },
                      ...MARGINAL.map((m) => ({ value: m, label: percent(t, m) }))]} />
                )}
              </Field>
            )} />
            {text('resaleFees', t('property_return.dialog.resale_fees'), percent(t, assumptions.resaleFees))}
          </div>
          <div className="flex flex-wrap justify-end gap-2">
            <Button type="button" variant="ghost" disabled={reset.isPending} onClick={() => reset.mutate(path, { onSuccess: done })}>
              {t('property_return.dialog.reset')}
            </Button>
            <Button type="button" variant="outline" onClick={onClose}>{t('property_return.dialog.cancel')}</Button>
            <Button type="submit" disabled={save.isPending}>{t('property_return.dialog.save')}</Button>
          </div>
        </form>
      )}
    </ModalDialog>
  );
}
