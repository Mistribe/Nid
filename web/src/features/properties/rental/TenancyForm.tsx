import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ComboboxField } from '@/components/form/ComboboxField';
import { DateField } from '@/components/form/DateField';
import { ModalDialog } from '@/components/ModalDialog';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Segmented } from '@/components/form/Segmented';
import { SwitchField } from '@/components/page/SwitchField';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { presentRefusal } from '../../loans/refusal';
import { Field } from '@/components/form/Field';
import { PERSONS } from '@/api/persons';
import { Reload } from '@/components/form/Reload';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { invalidateProperties, TENANCIES, TENANCY, type PropertyDetail, type PropertyTenancy } from '@/api/properties';
import { monthText } from '@/components/LocalDate';
import { todayIso } from '@/lib/today';
import { chainStart, TENANCY_PATHS, tenancySchema, tenancyValuesOf, toTenancyBody, trackedMonths, type TenancyValues } from './tenancy';
import '@/locales/loans';
import '@/locales/properties';

/**
 * Spec 3D2 §3: a tenancy added (`editing` absent: POST) or corrected (PUT, with the version read) in a dialog — ending a
 * tenancy is correcting it with an end. A save hands the dialog back (`close()`) and the section announces and places the
 * focus; « Annuler » and Escape just call `onClose`.
 */
export function TenancyDialog({ property, editing, onClose, onDone }: {
  property: PropertyDetail; editing?: PropertyTenancy; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const dialog = useRef<{ close: () => void }>(null);
  return (
    <ModalDialog title={editing ? t('property_rental.edit_title') : t('property_rental.add_title')} onClose={onClose} dialogRef={dialog}>
      {(titleId) => (
        <TenancyForm property={property} editing={editing} titleId={titleId}
          onDone={(message) => { if (message) { dialog.current?.close(); onDone(message); } else onClose(); }} />
      )}
    </ModalDialog>
  );
}

function TenancyForm({ property, editing, titleId, onDone }: { property: PropertyDetail; editing?: PropertyTenancy; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  // The version the form opened on: a refetch meanwhile must not turn the expected 409 into an overwrite.
  const [version] = useState(editing?.version);
  const form = useForm<TenancyValues>({ resolver: zodResolver(tenancySchema(t)), defaultValues: tenancyValuesOf(editing) });
  // A new lease proposes the owners as landlord (rental receipts spec §2); typed text is never overwritten.
  const people = $api.useQuery('get', PERSONS, {}, { enabled: !editing });
  useEffect(() => {
    if (editing || !people.data || form.getValues('landlordName') !== '') return;
    const names = property.owners.flatMap((o) => people.data.find((p) => p.id === o.personId)?.displayName ?? []);
    if (names.length > 0 && !form.formState.dirtyFields.landlordName) form.setValue('landlordName', names.join(' et '));
  }, [editing, people.data, property.owners, form]);
  const errors = useSubmitErrors(form.setError, TENANCY_PATHS);
  const add = $api.useMutation('post', TENANCIES);
  const correct = $api.useMutation('put', TENANCY);

  const submit = form.handleSubmit((values) => {
    errors.clear();
    const callbacks = {
      onSuccess: () => {
        void invalidateProperties(queryClient);
        onDone(editing ? t('property_rental.corrected') : t('property_rental.added'));
      },
      onError: (error: unknown) => errors.onError(error),
    };
    if (editing) correct.mutate({ params: { path: { id: property.id, tenancyId: editing.id } }, body: toTenancyBody(values, version) }, callbacks);
    else add.mutate({ params: { path: { id: property.id } }, body: toTenancyBody(values) }, callbacks);
  });

  const e = form.formState.errors;
  // A revision's rent and start are the chain's (rental spec §3.2): shown, sent back unchanged; the end stays, to end the lease.
  const revised = !!editing?.revisedFrom;
  const problem = errors.problem ? presentRefusal(t, editing ? correct.error : add.error) : null;
  const amount = (name: 'rentMonthly' | 'chargesMonthly' | 'deposit', label: string, hint?: string) => (
    <Field label={label} hint={hint} error={e[name]?.message}>
      {(c) => <Input inputMode="decimal" {...c} {...form.register(name)} readOnly={revised && name === 'rentMonthly'} />}
    </Field>
  );
  const date = (name: 'startsOn' | 'endsOn', label: string, hint?: string) => (
    <Controller control={form.control} name={name} render={({ field }) => (
      <Field label={label} hint={hint} error={e[name]?.message}>
        {(c) => <DateField {...c} value={field.value} onChange={field.onChange} readOnly={revised && name === 'startsOn'} />}
      </Field>
    )} />
  );

  return (
    <form aria-labelledby={titleId} noValidate onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3">
      {problem ? (
        <>
          <ProblemMessage problem={problem} />
          {/* The row changed or went: reading it again closes the dialog onto what is now stored. */}
          {problem.kind === 'conflict' || problem.kind === 'not_found'
            ? <Reload onReload={() => invalidateProperties(queryClient).then(() => onDone())} />
            : null}
        </>
      ) : null}
      <Field label={t('property_rental.label')} hint={t('property_rental.label_help')} error={e.label?.message}>
        {(c) => <Input {...c} {...form.register('label')} />}
      </Field>
      <div className="grid gap-3 sm:grid-cols-2">
        {date('startsOn', t('property_rental.starts'))}
        {date('endsOn', t('property_rental.ends'), t('loan_form.optional'))}
        {amount('rentMonthly', t('property_rental.rent'))}
        {amount('chargesMonthly', t('property_rental.charges'))}
        {amount('deposit', t('property_rental.deposit'), t('loan_form.optional'))}
      </div>
      <Controller control={form.control} name="indexed" render={({ field }) => (
        <SwitchField label={t('property_rental.indexed')} checked={field.value} onCheckedChange={(on) => { field.onChange(on); if (!on) form.setValue('irlQuarter', ''); }} />
      )} />
      {form.watch('indexed') ? (
        <Controller control={form.control} name="irlQuarter" render={({ field }) => (
          <Field label={t('property_rental.irl_quarter')} hint={t('property_rental.irl_quarter_help')} error={e.irlQuarter?.message}>
            {(c) => <Segmented {...c} aria-label={t('property_rental.irl_quarter')} value={field.value} onValueChange={field.onChange}
              options={[1, 2, 3, 4].map((q) => ({ value: String(q), label: `T${q}` }))} />}
          </Field>
        )} />
      ) : null}
      <Field label={t('property_rental.tenant_names')} hint={t('property_rental.identity_help')} error={e.tenantNames?.message}>
        {(c) => <Input {...c} {...form.register('tenantNames')} />}
      </Field>
      <Field label={t('property_rental.landlord_name')} error={e.landlordName?.message}>
        {(c) => <Input {...c} {...form.register('landlordName')} />}
      </Field>
      <Field label={t('property_rental.landlord_address')} error={e.landlordAddress?.message}>
        {(c) => <Textarea {...c} {...form.register('landlordAddress')} />}
      </Field>
      <Controller control={form.control} name="rentsTrackedFrom" render={({ field }) => {
        // From the lease's month — a revised link's chain's first one: the server sets it on every link — to the current one.
        const months = trackedMonths(revised && editing ? chainStart(property.tenancies, editing) : form.watch('startsOn'), todayIso());
        if (field.value && !months.includes(field.value)) months.push(field.value);
        return (
          <Field label={t('property_rental.rents_tracked_from')} hint={t('property_rental.rents_tracked_from_help')} error={e.rentsTrackedFrom?.message}>
            {(c) => <ComboboxField {...c} value={field.value} onValueChange={field.onChange}
              searchPlaceholder={t('property_rental.rents_tracked_search')} empty={t('property_rental.rents_tracked_empty')}
              options={[{ value: '', label: t('property_rental.rents_tracked_auto') }, ...months.map((m) => ({ value: m, label: monthText(m) }))]} />}
          </Field>
        );
      }} />
      <Field label={t('property_rental.note')} error={e.note?.message}>
        {(c) => <Textarea {...c} {...form.register('note')} />}
      </Field>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('loan_detail.cancel')}</Button>
        <Button type="submit" disabled={add.isPending || correct.isPending}>{editing ? t('loan_detail.save') : t('property_rental.submit')}</Button>
      </div>
    </form>
  );
}
