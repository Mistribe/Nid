import { useId, useState } from 'react';
import { Controller, FormProvider, useForm, useFormContext, useWatch, type FieldErrors, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { ComboboxField } from '@/components/form/ComboboxField';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { FormDialog } from '@/components/FormDialog';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { PERSONS } from '../../subscriptions/api';
import { ISO_DATE } from '../../subscriptions/edits/amount';
import type { EventKind, ProjectionEvent } from './api';
import { eventField, fromEvent, toEvent, type EventDraft } from './eventDraft';
import { usePickOptions } from './pickOptions';
import type strings from '@/locales/fr.scenarios.json';
import '@/locales/forms';
import '@/locales/scenarios';

type EventKey = keyof typeof strings.scenarios.projection.event;

/** The dialog's labels (`scenarios.projection.event.*`). */
function useEventText() {
  const { t } = useTranslation();
  return (key: EventKey) => t(`scenarios.projection.event.${key}`);
}

/** An amount as typed (`1 200,50`, `-800`): digits, a comma or a dot and at most four decimals; `signed` lets a minus lead. */
const AMOUNT = /^\d+([.,]\d{1,4})?$/;
const SIGNED = /^-?\d+([.,]\d{1,4})?$/;
const AGE = /^\d{1,3}$/;
const bare = (text: string) => text.replace(/\s/g, '');

/** A refusal the server named inside one event: `amount`, `start.age`… (the page strips `events[i].`). */
export type EventFieldError = { path: string; message: string };

/**
 * Spec §8: one event in a `FormDialog`, its fields by kind (§4). `onSave` gets the wire event; `errors` are the server's
 * refusals of this event, each shown on its field. The dialog validates what it can read (a date, an amount, a pick) and leaves
 * the rules (ages, one sale per property…) to the server.
 */
export function EventDialog({ kind, event, errors, scope, onSave, onClose, onDone }: {
  kind: EventKind; event?: ProjectionEvent; errors?: EventFieldError[]; scope: 'me' | 'household';
  onSave: (event: ProjectionEvent) => void; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  return (
    <FormDialog title={t(`scenarios.projection.kind.${kind}`)} onClose={onClose} onDone={onDone}>
      {({ done }) => (
        <EventForm kind={kind} event={event} errors={errors} scope={scope} onCancel={() => done()}
          onSave={(saved) => { onSave(saved); done(t(event ? 'scenarios.projection.events.saved' : 'scenarios.projection.events.added')); }} />
      )}
    </FormDialog>
  );
}

function EventForm({ kind, event, errors, scope, onSave, onCancel }: {
  kind: EventKind; event?: ProjectionEvent; errors?: EventFieldError[]; scope: 'me' | 'household';
  onSave: (event: ProjectionEvent) => void; onCancel: () => void;
}) {
  const { t } = useTranslation();
  // The server's refusals are on their fields from the first paint (the dialog mounts with them); one no field shows stays above.
  const [initial] = useState(() => {
    const draft = fromEvent(event);
    const placed: FieldErrors<EventDraft> = {};
    for (const e of errors ?? []) {
      const name = eventField(e.path, draft);
      if (!name) continue;
      const [root, part] = name.split('.') as [keyof EventDraft, string | undefined];
      const error = { type: 'server', message: e.message };
      if (part) Object.assign(((placed as Record<string, Record<string, unknown>>)[root] ??= {}), { [part]: error });
      else Object.assign(placed, { [root]: error });
    }
    return { draft, placed };
  });
  const form = useForm<EventDraft>({ defaultValues: initial.draft, errors: initial.placed });
  const unplaced = (errors ?? []).filter((e) => !eventField(e.path, initial.draft));

  return (
    <FormProvider {...form}>
      {/* The dialog is portalled, but React bubbles its submit to the page's own form: it stops here. */}
      <form noValidate className="space-y-4" onSubmit={(e) => {
        e.stopPropagation();
        void form.handleSubmit((d) => onSave(toEvent(kind, d, event)))(e);
      }}>
        {unplaced.length > 0 ? (
          <ul role="alert" className="space-y-1 text-sm text-destructive">{unplaced.map((e) => <li key={e.path}>{e.message}</li>)}</ul>
        ) : null}
        <KindFields kind={kind} scope={scope} />
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onCancel}>{t('common.cancel')}</Button>
          <Button type="submit">{t(event ? 'scenarios.projection.event.save' : 'scenarios.projection.event.add')}</Button>
        </div>
      </form>
    </FormProvider>
  );
}

function KindFields({ kind, scope }: { kind: EventKind; scope: 'me' | 'household' }) {
  const e = useEventText();
  switch (kind) {
    case 'income_change':
    case 'savings_change':
      return (
        <>
          <TextField name="label" label={e('label')} text />
          <DateFields name="start" label={e('start')} onLabel={e('start_on')} />
          <DateFields name="until" label={e('until')} onLabel={e('until_on')} personLabel={e('until_person')} ageLabel={e('until_age')} optional />
          <TextField name="monthlyAmount" label={e('monthly_amount')} hint={e('monthly_hint')} pattern={SIGNED} required />
        </>
      );
    case 'early_repayment':
      return <RepaymentFields scope={scope} />;
    case 'property_sale':
      return (
        <>
          <PickField name="property" label={e('property')} scope={scope} list="properties" />
          <DateFields name="start" label={e('date')} onLabel={e('sale_on')} />
          <div className="grid gap-3 sm:grid-cols-2">
            <TextField name="price" label={e('price')} hint={e('price_hint')} pattern={AMOUNT} />
            <TextField name="costs" label={e('costs')} pattern={AMOUNT} required />
          </div>
        </>
      );
    case 'property_purchase':
      return <PickField name="scenario" label={e('scenario')} hint={e('scenario_hint')} scope={scope} list="purchases" />;
  }
}

function RepaymentFields({ scope }: { scope: 'me' | 'household' }) {
  const e = useEventText();
  const form = useFormContext<EventDraft>();
  const split = useWatch({ control: form.control, name: 'split' });
  const effectId = useId();
  return (
    <>
      <DateFields name="start" label={e('date')} onLabel={e('repayment_on')} />
      <TextField name="amount" label={e('amount')} pattern={AMOUNT} required />
      <Controller control={form.control} name="split" render={({ field, fieldState }) => (
        <Field label={e('split')} error={fieldState.error?.message}>
          {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
            options={(['highest_rate', 'smallest_balance', 'chosen'] as const).map((v) => ({ value: v, label: e(`split_${v}`) }))} />}
        </Field>
      )} />
      {split === 'chosen' ? <PickField name="loan" label={e('loan')} scope={scope} list="loans" /> : null}
      <div className="space-y-1">
        <span id={effectId} className="block text-sm">{e('effect')}</span>
        <Controller control={form.control} name="effect" render={({ field }) => (
          <Segmented aria-labelledby={effectId} value={field.value} onValueChange={field.onChange}
            options={(['reduce_term', 'reduce_payment'] as const).map((v) => ({ value: v, label: e(`effect_${v}`) }))} />
        )} />
      </div>
      <TextField name="penalty" label={e('penalty')} pattern={AMOUNT} required />
    </>
  );
}

/** A text field: an amount unless `text`; `pattern` is what it must read as, `required` refuses it empty. */
function TextField({ name, label, hint, text, pattern, required, numeric }: {
  name: Path<EventDraft>; label: string; hint?: string; text?: boolean; pattern?: RegExp; required?: boolean; numeric?: boolean;
}) {
  const { t } = useTranslation();
  const form = useFormContext<EventDraft>();
  const error = form.getFieldState(name, form.formState).error?.message;
  return (
    <Field label={label} hint={hint} error={typeof error === 'string' ? error : undefined}>
      {(c) => <Input inputMode={text ? undefined : numeric ? 'numeric' : 'decimal'} {...c} {...form.register(name, {
        validate: (v) => {
          const typed = bare(typeof v === 'string' ? v : '');
          if (!typed) return required ? t('edit.required') : true;
          if (!pattern || pattern.test(typed)) return true;
          return numeric ? t('scenarios.projection.event.invalid_age') : t('scenarios.projection.event.invalid_amount');
        },
      })} />}
    </Field>
  );
}

/**
 * A date of the event (spec §4): a day, or « à l'âge de » a person; an end may also be none. Only the fields of the chosen mode
 * are shown, and validated.
 */
function DateFields({ name, label, onLabel, personLabel, ageLabel, optional }: {
  name: 'start' | 'until'; label: string; onLabel: string; personLabel?: string; ageLabel?: string; optional?: boolean;
}) {
  const { t } = useTranslation();
  const e = useEventText();
  const { $api } = useApi();
  const form = useFormContext<EventDraft>();
  const mode = useWatch({ control: form.control, name: `${name}.mode` });
  const persons = $api.useQuery('get', PERSONS, {}, { enabled: mode === 'age' });
  const modes = [...(optional ? (['none'] as const) : []), 'on', 'age'] as const;
  return (
    <div className="space-y-3">
      <Controller control={form.control} name={`${name}.mode`} render={({ field }) => (
        <Segmented aria-label={label} value={field.value} onValueChange={field.onChange}
          options={modes.map((m) => ({ value: m, label: e(m === 'none' ? 'no_end' : `mode_${m}`) }))} />
      )} />
      {mode === 'on' ? (
        <Controller control={form.control} name={`${name}.on`}
          rules={{ validate: (v) => (!v ? t('edit.required') : ISO_DATE.test(v) || t('edit.invalid_date')) }}
          render={({ field, fieldState }) => (
            <Field label={onLabel} error={fieldState.error?.message}>
              {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
            </Field>
          )} />
      ) : mode === 'age' ? (
        <div className="grid gap-3 sm:grid-cols-2">
          <Controller control={form.control} name={`${name}.person`} rules={{ required: t('edit.required') }}
            render={({ field, fieldState }) => (
              <Field label={personLabel ?? e('person')} error={fieldState.error?.message}>
                {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                  options={(persons.data ?? []).map((p) => ({ value: p.id, label: p.displayName }))} />}
              </Field>
            )} />
          <TextField name={`${name}.age`} label={ageLabel ?? e('age')} pattern={AGE} required numeric />
        </div>
      ) : null}
    </div>
  );
}

/** A searchable pick among the reader's loans (not ended), properties or saved purchase scenarios, in the scenario's scope. */
function PickField({ name, label, hint, scope, list }: {
  name: 'loan' | 'property' | 'scenario'; label: string; hint?: string; scope: 'me' | 'household'; list: 'loans' | 'properties' | 'purchases';
}) {
  const { t } = useTranslation();
  const options = usePickOptions(list, scope);
  const form = useFormContext<EventDraft>();
  return (
    <Controller control={form.control} name={name} rules={{ required: t('edit.required') }} render={({ field, fieldState }) => (
      <Field label={label} hint={hint} error={fieldState.error?.message}>
        {(c) => <ComboboxField {...c} value={field.value} onValueChange={field.onChange} options={options}
          searchPlaceholder={t('scenarios.projection.event.search')} empty={t('scenarios.projection.event.none_found')} />}
      </Field>
    )} />
  );
}
