import { useTranslation } from 'react-i18next';
import { Field } from '@/components/form/Field';
import { Input } from '@/components/ui/input';
import { addDuration } from '../offers';
import { AmountField, Choice, DateField, DurationFields, type Form } from './fields';

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
      <Choice label={t('wizard.offer.trial_variant')} value={variant} onValueChange={(v) => form.setValue('trialVariant', v as typeof variant)}
        options={(['free', 'launch'] as const).map((v) => ({ value: v, label: t(`wizard.offer.trial_${v}`) }))} />
      <DateField form={form} name="offerDate" label={t('wizard.offer.start')} />
      {variant === 'free' ? (
        <DurationFields form={form} count="durationCount" unit="durationUnit" label={t('wizard.offer.trial_duration')} />
      ) : (
        <>
          <AmountField form={form} name="launchAmount" label={t('wizard.offer.launch_price')} withCurrency />
          <DurationFields form={form} count="everyCount" unit="everyUnit" label={t('wizard.every')} />
          <Field label={t('wizard.offer.launch_cycles')} error={cyclesError}>
            {(c) => <Input inputMode="numeric" {...c} {...form.register('launchCycles')} />}
          </Field>
        </>
      )}
    </div>
  );
}
