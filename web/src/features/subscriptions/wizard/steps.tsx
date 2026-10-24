import { Controller, type UseFormReturn } from 'react-hook-form';
import { Fragment, useId } from 'react';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { formatLocalDate } from '@/components/LocalDate';
import { OwnershipFields } from '@/components/ownership/OwnershipFields';
import { Field } from '@/components/form/Field';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Textarea } from '@/components/ui/textarea';
import { durationText } from '../format';
import { ProviderPicker } from '../ProviderPicker';
import { ISO_DATE } from '../edits/amount';
import { addDuration } from './offers';
import { AmountField, Choice, DateField, PersonSelect, UnitSelect } from './offers/fields';
import type { Unit, WizardValues } from './schema';
import '@/locales/forms';

type Person = components['schemas']['PersonDto'];
type Form = UseFormReturn<WizardValues>;

export function CommonStep({ form, persons }: { form: Form; persons: Person[] }) {
  const { t } = useTranslation();
  const e = form.formState.errors;
  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="provider" render={({ field, fieldState }) => (
          <ProviderPicker label={t('edit.provider')} value={field.value} error={fieldState.error?.message} onChange={(p) => {
            field.onChange(p);
            if (p && form.getValues('displayName').trim() === '') form.setValue('displayName', p.name, { shouldValidate: true });
          }} />
        )} />
        <Field label={t('wizard.name')} error={e.displayName?.message}>{(c) => <Input {...c} {...form.register('displayName')} />}</Field>
      </div>
      <OwnershipFields form={form} persons={persons} />
      <fieldset className="space-y-1">
        <legend className="text-sm">{t('wizard.users')}</legend>
        <Controller control={form.control} name="users" render={({ field }) => (
          <div className="flex flex-wrap gap-x-4 gap-y-1">
            {persons.map((p) => (
              <label key={p.id} className="flex items-center gap-2 text-sm">
                <Checkbox checked={field.value.includes(p.id)}
                  onCheckedChange={(on) => field.onChange(on === true ? [...field.value, p.id] : field.value.filter((id) => id !== p.id))} />
                {p.displayName}
              </label>
            ))}
          </div>
        )} />
      </fieldset>
      <Field label={t('wizard.note')}>{(c) => <Textarea {...c} {...form.register('note')} />}</Field>
    </div>
  );
}

/** The 1E2a phase fields: the recurring offer, and the phase that follows another offer (without its own start there). */
export function RecurringOffer({ form, persons, withStart = true }: { form: Form; persons: Person[]; withStart?: boolean }) {
  const { t } = useTranslation();
  const e = form.formState.errors;
  const sameCovers = form.watch('sameCovers');
  const payerType = form.watch('payerType');
  return (
    <div className="space-y-4">
      {withStart ? <DateField form={form} name="startsOn" label={t('wizard.starts')} /> : null}
      <AmountField form={form} name="amount" label={t('wizard.amount')} withCurrency />
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('wizard.every')} error={e.everyCount?.message}>{(c) => <Input inputMode="numeric" {...c} {...form.register('everyCount')} />}</Field>
        <Field label={t('wizard.unit')}>{(c) => <UnitSelect form={form} name="everyUnit" control={c} />}</Field>
      </div>
      <Field label={t('wizard.same_covers')} inline>
        {(c) => <Controller control={form.control} name="sameCovers" render={({ field }) => (
          <Checkbox {...c} checked={field.value} onCheckedChange={(on) => field.onChange(on === true)} />
        )} />}
      </Field>
      {sameCovers ? null : (
        <div className="grid gap-3 sm:grid-cols-2">
          <Field label={t('wizard.covers')} error={e.coversCount?.message}>{(c) => <Input inputMode="numeric" {...c} {...form.register('coversCount')} />}</Field>
          <Field label={t('wizard.unit')}>{(c) => <UnitSelect form={form} name="coversUnit" control={c} />}</Field>
        </div>
      )}
      <Field label={t('wizard.cycles')} error={e.maxCycles?.message}>{(c) => <Input inputMode="numeric" {...c} {...form.register('maxCycles')} />}</Field>
      <Choice label={t('wizard.payer')} value={payerType} onValueChange={(v) => form.setValue('payerType', v as typeof payerType)}
        options={(['household', 'person'] as const).map((p) => ({ value: p, label: t(`wizard.payer_${p}`) }))} />
      {payerType === 'person' ? <PersonSelect form={form} name="payerPerson" persons={persons} /> : null}
    </div>
  );
}

type Values = WizardValues;
const valid = (date: string) => ISO_DATE.test(date);
const plus = (date: string, count: string, unit: Unit) => (/^[1-9]\d*$/.test(count) ? addDuration(date, Number(count), unit) : date);

/** When the offer's covered term ends: the earliest the following phase may start (1E2b1 design §4.1 step 4). */
function coverageEnd(v: Values): string | null {
  if (!valid(v.offerDate)) return null;
  if (v.offer === 'prepaid') return plus(plus(v.offerDate, v.paidCount, v.paidUnit), v.bonusCount, v.bonusUnit);
  return plus(v.offerDate, v.durationCount, v.durationUnit);
}

/**
 * Leaving the offer step for an offer that may be followed by a phase: keep that phase's start in step with
 * the coverage end, unless the person already typed their own — final review, finding 2. `isTouched` (not
 * `isDirty`, which leaveTrial uses for `nextStartsOn`) is the field the person actually edited: `isDirty`
 * also turns true here once any other field changes, since it compares the whole form to its defaults.
 */
export function fillFollowingStart(form: Form): void {
  if (form.getFieldState('startsOn').isTouched) return;
  const end = coverageEnd(form.getValues());
  if (end !== null) form.setValue('startsOn', end);
}

/**
 * 1E2b1 design §4.1 step 4: when the first charge falls, by offer; nothing when no phase follows the offer.
 * The following phase's real start (`startsOn`) may already sit later than the coverage end (the person
 * typed their own date): the first charge is then the later of the two (final review, finding 2).
 */
export function firstCharge(v: Values): string | null {
  if (v.offer === 'recurring') return v.startsOn;
  if (v.offer === 'trial' && v.trialVariant === 'launch') return v.offerDate;
  if (!v.then) return null;
  const end = coverageEnd(v);
  if (end === null) return null;
  return valid(v.startsOn) && v.startsOn > end ? v.startsOn : end;
}

export function SummaryStep({ form, persons }: { form: Form; persons: Person[] }) {
  const { t } = useTranslation();
  const v = form.getValues();
  const name = (id: string) => persons.find((p) => p.id === id)?.displayName ?? id;
  const id = useId();
  const date = (d: string) => (valid(d) ? formatLocalDate(d) : d);
  const money = (amount: string) => `${amount} ${v.currency.toUpperCase()}`;
  const duration = (count: string, unit: Unit) => durationText(t, { count: Number(count), unit });
  const phase = (amount: string, start: string) =>
    t('wizard.summary_phase', { price: money(amount), period: duration(v.everyCount, v.everyUnit), date: date(start) });

  const launch = v.offer === 'trial' && v.trialVariant === 'launch';
  const phases = v.offer === 'recurring' ? [phase(v.amount, v.startsOn)]
    : launch ? [phase(v.launchAmount, v.offerDate), phase(v.amount, v.nextStartsOn)]
      : v.then ? [phase(v.amount, v.startsOn)] : [];
  const purchase = (() => {
    switch (v.offer) {
      case 'prepaid': {
        const paid = duration(v.paidCount, v.paidUnit);
        const span = v.bonusCount.trim() === '' ? paid : t('wizard.summary_durations', { paid, bonus: duration(v.bonusCount, v.bonusUnit) });
        return `${v.offerLabel} · ${money(v.offerAmount)} · ${span}`;
      }
      case 'trial':
        return launch ? null : `${t('wizard.offer.trial_label')} · ${duration(v.durationCount, v.durationUnit)}`;
      case 'gift':
        return `${v.offerLabel} · ${money(v.giftReceived ? '0' : v.offerAmount)} · ${duration(v.durationCount, v.durationUnit)}`;
      case 'bundle':
        return `${v.offerLabel} · ${money(v.offerAmount)} · ${duration(v.durationCount, v.durationUnit)}`;
      case 'recurring':
        return null;
    }
  })();
  const first = firstCharge(v);
  const offerName = v.offer === 'trial' ? `${t('wizard.offer.trial')} · ${t(`wizard.offer.trial_${v.trialVariant}`)}` : t(`wizard.offer.${v.offer}`);
  return (
    <section aria-labelledby={id} className="space-y-2">
      <h2 id={id} className="text-lg font-semibold">{t('wizard.step_summary')}</h2>
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1">
        <dt>{t('wizard.name')}</dt><dd>{v.displayName}</dd>
        <dt>{t('wizard.summary_provider')}</dt><dd>{v.provider?.name ?? t('wizard.none')}</dd>
        <dt>{t('wizard.visibility')}</dt><dd>{t(`wizard.${v.visibility}`)}</dd>
        <dt>{t('wizard.owners')}</dt><dd>{v.owners.map((o) => `${name(o.personId)} (${o.share} %)`).join(', ')}</dd>
        <dt>{t('wizard.summary_offer')}</dt><dd>{offerName}</dd>
        {purchase === null ? null : <><dt>{t('wizard.summary_purchase')}</dt><dd>{purchase}</dd></>}
        {phases.map((p, i) => <Fragment key={i}><dt>{t('wizard.summary_price')}</dt><dd>{p}</dd></Fragment>)}
        {phases.length > 0
          ? <><dt>{t('wizard.payer')}</dt><dd>{v.payerType === 'person' ? name(v.payerPerson) : t('wizard.payer_household')}</dd></>
          : null}
        {first === null ? null : <><dt>{t('wizard.summary_first_charge')}</dt><dd>{date(first)}</dd></>}
      </dl>
    </section>
  );
}
