import { zodResolver } from '@hookform/resolvers/zod';
import { useIsMutating, useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Controller, FormProvider, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { SelectField } from '@/components/form/SelectField';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { Section } from '@/components/page/Section';
import { presentRefusal } from '../../loans/refusal';
import { Field } from '@/components/form/Field';
import { Reload } from '../../subscriptions/edits/Reload';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { invalidateProperties, PROPERTY, type PropertyDetail } from '../api';
import { PropertyFields } from '../new/PropertyFields';
import { settingsPath, settingsSchema, settingsValuesOf, toEditBody, toWireBody, type SettingsValues } from './settingsBody';
import '@/locales/loans';

const ENERGY_CLASSES = ['A', 'B', 'C', 'D', 'E', 'F', 'G'] as const;

/** The settings form's id: its submit sits beside the tab list (`DetailTabs`' action), outside the form. */
const FORM_ID = 'property-settings';

/** « Enregistrer les réglages », at the right of the tabs; disabled while a save is on its way. */
export function SettingsSubmit() {
  const { t } = useTranslation();
  const saving = useIsMutating({ mutationKey: ['patch', PROPERTY] }) > 0;
  return <Button type="submit" form={FORM_ID} disabled={saving}>{t('loan_detail.settings.submit')}</Button>;
}

/**
 * 3D1 design §6: « Réglages », its live region and its focus. The form remounts on the stored property — its new values
 * and version — after a save, « nothing to save » or « Recharger » (precision 8).
 */
export function SettingsSection({ property }: { property: PropertyDetail }) {
  const { t } = useTranslation();
  const [announcement, setAnnouncement] = useState('');
  const queryClient = useQueryClient();
  const [generation, setGeneration] = useState(0);
  // The form opens on this snapshot: re-taken from the cache when `done`, as the page's own copy renders a tick later.
  const [opened, setOpened] = useState(property);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const done = (message?: string) => {
    setOpened(queryClient.getQueryData<PropertyDetail>(['get', PROPERTY, { params: { path: { id: property.id } } }]) ?? property);
    setGeneration((g) => g + 1);
    if (message) setAnnouncement(message);
    headingRef.current?.focus();
  };
  return (
    <Section title={t('loan_detail.settings.title')} headingRef={headingRef} announcement={announcement}>
      <SettingsForm key={generation} property={opened} onStart={() => setAnnouncement('')} onDone={done} />
    </Section>
  );
}

/**
 * Only what changed, an emptied optional field with its `clear*` flag, and the version of the property the form opened
 * on: a refetch meanwhile must not turn the expected 409 into an overwrite (3B2 ruling).
 */
function SettingsForm({ property, onStart, onDone }: { property: PropertyDetail; onStart: () => void; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [opened] = useState(property);
  const form = useForm<SettingsValues>({ resolver: zodResolver(settingsSchema(t)), defaultValues: settingsValuesOf(opened) });
  const errors = useSubmitErrors(form.setError, settingsPath);
  const save = $api.useMutation('patch', PROPERTY);

  const submit = form.handleSubmit((values) => {
    onStart();
    errors.clear();
    const body = toEditBody(opened, values);
    if (!body) { onDone(t('loan_detail.settings.nothing')); return; }
    save.mutate(
      { params: { path: { id: opened.id } }, body: toWireBody(body, opened.version) },
      {
        // The section remounts this form once the property is read again, on its new values and version.
        onSuccess: () => void invalidateProperties(queryClient).then(() => onDone(t('loan_detail.settings.saved'))),
        onError: (error) => errors.onError(error),
      },
    );
  }, onStart); // an invalid submit clears the previous status too

  const e = form.formState.errors;
  // Every refusal reads in French: a conflict by its own sentence, a 422 no field owns by its code's.
  const problem = errors.problem ? presentRefusal(t, save.error) : null;

  return (
    <FormProvider {...form}>
      <form id={FORM_ID} aria-label={t('loan_detail.settings.title')} noValidate onSubmit={(ev) => void submit(ev)} className="max-w-md space-y-3">
        {problem ? (
          <>
            <ProblemMessage problem={problem} />
            {problem.kind === 'conflict' ? <Reload onReload={() => invalidateProperties(queryClient).then(() => onDone())} /> : null}
          </>
        ) : null}
        <Field label={t('property_form.name')} error={e.name?.message}>
          {(c) => <Input {...c} {...form.register('name')} />}
        </Field>
        <PropertyFields />
        {/* Spec 3D2 §5: the rental start lives here, not in « Location »; emptied, it is cleared. */}
        <Controller control={form.control} name="rentalStartsOn" render={({ field }) => (
          <Field label={t('property_form.rental_starts_on')} hint={t('property_form.rental_starts_on_help')} error={e.rentalStartsOn?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
        <Controller control={form.control} name="energyClass" render={({ field }) => (
          <Field label={t('property_form.energy_class')} hint={t('property_form.energy_class_help')} error={e.energyClass?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={[{ value: '', label: t('property_form.energy_class_none') }, ...ENERGY_CLASSES.map((v) => ({ value: v, label: v }))]} />}
          </Field>
        )} />
        <Field label={t('property_form.postal_address')} hint={t('property_form.postal_address_help')} error={e.postalAddress?.message}>
          {(c) => <Textarea {...c} {...form.register('postalAddress')} />}
        </Field>
        <Field label={t('property_form.note')} error={e.note?.message}>
          {(c) => <Textarea {...c} {...form.register('note')} />}
        </Field>
      </form>
    </FormProvider>
  );
}
