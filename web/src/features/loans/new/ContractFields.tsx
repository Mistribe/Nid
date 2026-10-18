import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import { useId, useRef, useState, type ChangeEvent } from 'react';
import { useFieldArray, useFormContext } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { Button } from '@/components/ui/button';
import { ISO_DATE } from '../../subscriptions/edits/amount';
import { Field } from '../../subscriptions/edits/Field';
import { FIRST_PERIODS, ROUNDINGS } from '../format';
import { Folded } from './Folded';
import { InsuranceFields } from './InsuranceFields';
import { MAX_INSURANCES, newInsurance, newPhase, PLAIN_HIDDEN, plusOneMonth, type ContractValues } from './loanForm';
import { PhaseFields } from './PhaseFields';

const input = 'rounded-md border px-3 py-2';

/**
 * The contract's fields (3B1 design §6, 3B2 design §5), shared by the creation and the contract page: they read the
 * form from a `FormProvider` whose values extend `ContractValues`. `stored` marks a stored contract: its first payment is
 * the bank's and never follows the disbursement. `startListed` opens on the phase list (a stored
 * contract that is not a plain loan).
 */
export function ContractFields({ startListed = false, stored = false }: { startListed?: boolean; stored?: boolean }) {
  const { t } = useTranslation();
  const form = useFormContext<ContractValues>();
  const termsId = useId();
  const phasesId = useId();
  const insurancesId = useId();
  const addPhaseRef = useRef<HTMLButtonElement>(null);
  const addInsuranceRef = useRef<HTMLButtonElement>(null);
  // The first payment follows the disbursement until the person types in it (precision 12); a stored one never does.
  const firstTouched = useRef(stored);
  const phases = useFieldArray({ control: form.control, name: 'phases' });
  const insurances = useFieldArray({ control: form.control, name: 'insurances' });
  const e = form.formState.errors;
  // The phase list shows from the second line on, and stays while the one line left is a deferral (its mode must be seen).
  const [listed, setListed] = useState(startListed);
  const plain = phases.fields.length === 1 && !listed;

  const removePhase = (i: number) => {
    const leftMode = form.getValues(`phases.${i === 0 ? 1 : 0}.mode`);
    phases.remove(i);
    // Back to a plain loan (precision 11) when the line left is an annuity: it keeps its mode, rate and type; what a plain loan hides is cleared.
    if (phases.fields.length === 2 && leftMode === 'annuity') {
      setListed(false);
      for (const field of PLAIN_HIDDEN) form.setValue(`phases.0.${field}`, '');
    }
    addPhaseRef.current?.focus();
  };
  const removeInsurance = (i: number) => {
    insurances.remove(i);
    addInsuranceRef.current?.focus();
  };
  // An insurance added here starts with the first payment and follows it until its own start is typed in (3B2 design §4.3).
  const followFirst = (value: string) => {
    if (!ISO_DATE.test(value)) return;
    form.getValues('insurances').forEach((ins, i) => { if (ins.follows) form.setValue(`insurances.${i}.startsOn`, value); });
  };
  const followDisbursement = (ev: ChangeEvent<HTMLInputElement>) => {
    if (firstTouched.current || !ISO_DATE.test(ev.target.value)) return;
    const first = plusOneMonth(ev.target.value);
    form.setValue('firstPaymentDate', first);
    followFirst(first);
  };
  const typeFirst = (ev: ChangeEvent<HTMLInputElement>) => {
    firstTouched.current = true;
    followFirst(ev.target.value);
  };

  return (
    <>
      <section aria-labelledby={termsId} className="space-y-3">
        <h2 id={termsId} className="text-lg font-semibold">{t('loan_form.terms')}</h2>
        <Field label={t('loan_form.principal')} error={e.principal?.message}>
          {(c) => <input inputMode="decimal" {...c} {...form.register('principal')} className={`w-40 ${input}`} />}
        </Field>
        {plain ? (
          <Field label={t('loan_form.rate')} error={e.phases?.[0]?.rate?.message}>
            {(c) => <input inputMode="decimal" {...c} {...form.register('phases.0.rate')} className={`w-24 ${input}`} />}
          </Field>
        ) : null}
        <Field label={t('loan_form.duration')} error={e.totalPayments?.message}>
          {(c) => <input inputMode="numeric" {...c} {...form.register('totalPayments')} className={`w-24 ${input}`} />}
        </Field>
        <Field label={t('loan_form.disbursed_on')} error={e.disbursedOn?.message}>
          {(c) => <input type="date" {...c} {...form.register('disbursedOn', { onChange: followDisbursement })} />}
        </Field>
        <Field label={t('loan_form.first_payment')} hint={t('loan_form.first_payment_help')} error={e.firstPaymentDate?.message}>
          {(c) => <input type="date" {...c} {...form.register('firstPaymentDate', { onChange: typeFirst })} />}
        </Field>
      </section>

      {plain ? null : (
        <section aria-labelledby={phasesId} className="space-y-3">
          <h2 id={phasesId} className="text-lg font-semibold">{t('loan_form.phases')}</h2>
          <p className="text-sm text-muted-foreground">{t('loan_form.phases_help')}</p>
          {phases.fields.map((row, i) => (
            <fieldset key={row.id} className="space-y-2 rounded-md border p-3">
              <legend className="px-1 text-sm font-medium">{t('loan_form.phase_n', { n: i + 1 })}</legend>
              <PhaseFields form={form} index={i} last={i === phases.fields.length - 1} />
              {phases.fields.length > 1 ? (
                <Button type="button" variant="ghost" size="sm" onClick={() => removePhase(i)}>{t('loan_form.remove_phase', { n: i + 1 })}</Button>
              ) : null}
            </fieldset>
          ))}
        </section>
      )}
      <Button ref={addPhaseRef} type="button" variant="outline" size="sm" onClick={() => { setListed(true); phases.append(newPhase()); }}>{t('loan_form.add_phase')}</Button>

      <Folded label={t('loan_form.options')} hasError={Boolean(e.rounding || e.firstPeriod)}>
        <Field label={t('loan_form.rounding')} error={e.rounding?.message}>
          {(c) => (
            <NativeSelect {...c} {...form.register('rounding')}>
              {ROUNDINGS.map((r) => <NativeSelectOption key={r} value={r}>{t(`loan_form.rounding_option.${r}`)}</NativeSelectOption>)}
            </NativeSelect>
          )}
        </Field>
        <Field label={t('loan_form.first_period')} error={e.firstPeriod?.message}>
          {(c) => (
            <NativeSelect {...c} {...form.register('firstPeriod')}>
              {FIRST_PERIODS.map((p) => <NativeSelectOption key={p} value={p}>{t(`loan_form.first_period_option.${p}`)}</NativeSelectOption>)}
            </NativeSelect>
          )}
        </Field>
      </Folded>

      <section aria-labelledby={insurancesId} className="space-y-3">
        <h2 id={insurancesId} className="text-lg font-semibold">{t('loan_form.insurances')}</h2>
        {insurances.fields.map((row, i) => (
          <fieldset key={row.id} className="space-y-2 rounded-md border p-3">
            <legend className="px-1 text-sm font-medium">{t('loan_form.insurance_n', { n: i + 1 })}</legend>
            <InsuranceFields form={form} index={i} />
            <Button type="button" variant="ghost" size="sm" onClick={() => removeInsurance(i)}>{t('loan_form.remove_insurance', { n: i + 1 })}</Button>
          </fieldset>
        ))}
        {insurances.fields.length < MAX_INSURANCES ? (
          <Button ref={addInsuranceRef} type="button" variant="outline" size="sm"
            onClick={() => insurances.append(newInsurance(form.getValues('firstPaymentDate')))}>{t('loan_form.add_insurance')}</Button>
        ) : null}
      </section>
    </>
  );
}
