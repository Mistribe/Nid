import { useTranslation } from 'react-i18next';
import { Field } from '../../edits/Field';
import { addDuration } from '../offers';
import { AmountField, DateField, DurationFields, input, type Form } from './fields';

/**
 * Leaving the trial step: the free trial's purchase is labelled "Essai"; the launch price's normal price starts
 * after its cycles, unless the person already chose that date.
 */
export function leaveTrial(form: Form, trialLabel: string) {
  const v = form.getValues();
  if (v.trialVariant === 'free') {
    form.setValue('offerLabel', trialLabel);
    return;
  }
  if (!form.getFieldState('nextStartsOn').isDirty)
    form.setValue('nextStartsOn', addDuration(v.offerDate, Number(v.launchCycles) * Number(v.everyCount), v.everyUnit));
}

/** 1E2b1 design §4.1: a free trial, or a reduced price for a few cycles. */
export function Trial({ form }: { form: Form }) {
  const { t } = useTranslation();
  const variant = form.watch('trialVariant');
  const cyclesError = form.formState.errors.launchCycles?.message;
  return (
    <div className="space-y-4">
      <fieldset className="space-y-1">
        <legend className="text-sm">{t('wizard.offer.trial_variant')}</legend>
        {(['free', 'launch'] as const).map((v) => (
          <label key={v} className="flex items-center gap-2">
            <input type="radio" value={v} {...form.register('trialVariant')} />{t(`wizard.offer.trial_${v}`)}
          </label>
        ))}
      </fieldset>
      <DateField form={form} name="offerDate" label={t('wizard.offer.start')} />
      {variant === 'free' ? (
        <DurationFields form={form} count="durationCount" unit="durationUnit" label={t('wizard.offer.trial_duration')} />
      ) : (
        <>
          <AmountField form={form} name="launchAmount" label={t('wizard.offer.launch_price')} withCurrency />
          <DurationFields form={form} count="everyCount" unit="everyUnit" label={t('wizard.every')} />
          <Field label={t('wizard.offer.launch_cycles')} error={cyclesError}>
            {(c) => <input inputMode="numeric" {...c} {...form.register('launchCycles')} className={`w-20 ${input}`} />}
          </Field>
        </>
      )}
    </div>
  );
}
