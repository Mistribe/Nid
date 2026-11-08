import { PlusIcon } from 'lucide-react';
import { useId, useRef, useState, type ReactNode } from 'react';
import { Controller, useFieldArray, useFormContext, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { ISO_DATE } from '@/lib/amount';
import { FIRST_PERIODS, ROUNDINGS } from '../format';
import { Folded } from '@/components/form/Folded';
import { InsuranceFields } from './InsuranceFields';
import { MAX_INSURANCES, newInsurance, newPhase, PLAIN_HIDDEN, plusOneMonth, type ContractValues } from './loanForm';
import { PhaseFields } from './PhaseFields';
import '@/locales/loans';

/**
 * The contract's state (3B1 design §6, 3B2 design §5), shared by the creation's steps and the contract page: owned by
 * the screen, not by the fields, so it survives a step that unmounts them. `stored` marks a stored contract: its first
 * payment is the bank's and never follows the disbursement. `startListed` opens on the phase list (a stored contract that
 * is not a plain loan).
 */
export function useContract(form: UseFormReturn<ContractValues>, { startListed = false, stored = false } = {}) {
  const addPhaseRef = useRef<HTMLButtonElement>(null);
  const addInsuranceRef = useRef<HTMLButtonElement>(null);
  // The first payment follows the disbursement until the person types in it (precision 12); a stored one never does.
  const firstTouched = useRef(stored);
  const phases = useFieldArray({ control: form.control, name: 'phases' });
  const insurances = useFieldArray({ control: form.control, name: 'insurances' });
  // The phase list shows from the second line on, and stays while the one line left is a deferral (its mode must be seen).
  const [listed, setListed] = useState(startListed);
  const [phasesOpen, setPhasesOpen] = useState(startListed);
  const plain = phases.fields.length === 1 && !listed;

  const addPhase = () => { setListed(true); setPhasesOpen(true); phases.append(newPhase()); };
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
  const followDisbursement = (value: string) => {
    if (firstTouched.current || !ISO_DATE.test(value)) return;
    const first = plusOneMonth(value);
    form.setValue('firstPaymentDate', first);
    followFirst(first);
  };
  const typeFirst = (value: string) => { firstTouched.current = true; followFirst(value); };

  return { form, phases, insurances, plain, phasesOpen, setPhasesOpen, addPhase, removePhase, removeInsurance, addPhaseRef, addInsuranceRef, followDisbursement, typeFirst };
}
type Contract = ReturnType<typeof useContract>;

/** « Le prêt »: the figures on a three-column grid (`children` take its last cell), the phases and calculation options folded. */
export function LoanFields({ contract: c, children }: { contract: Contract; children?: ReactNode }) {
  const { t } = useTranslation();
  const { form } = c;
  const e = form.formState.errors;
  return (
    <>
      <div className="grid gap-3 sm:grid-cols-3">
        <Field label={t('loan_form.principal')} error={e.principal?.message}>
          {(f) => <Input inputMode="decimal" {...f} {...form.register('principal')} />}
        </Field>
        {c.plain ? (
          <Field label={t('loan_form.rate')} error={e.phases?.[0]?.rate?.message}>
            {(f) => <Input inputMode="decimal" {...f} {...form.register('phases.0.rate')} />}
          </Field>
        ) : null}
        <Field label={t('loan_form.duration')} error={e.totalPayments?.message}>
          {(f) => <Input inputMode="numeric" {...f} {...form.register('totalPayments')} />}
        </Field>
        <Controller control={form.control} name="disbursedOn" render={({ field }) => (
          <Field label={t('loan_form.disbursed_on')} error={e.disbursedOn?.message}>
            {(f) => <DateField {...f} value={field.value} onChange={(v) => { field.onChange(v); c.followDisbursement(v); }} />}
          </Field>
        )} />
        <Controller control={form.control} name="firstPaymentDate" render={({ field }) => (
          <Field label={t('loan_form.first_payment')} hint={t('loan_form.first_payment_help')} error={e.firstPaymentDate?.message}>
            {(f) => <DateField {...f} value={field.value} onChange={(v) => { field.onChange(v); c.typeFirst(v); }} />}
          </Field>
        )} />
        {children}
      </div>

      <Folded label={t('loan_form.phases')} hasError={!c.plain && Boolean(e.phases)} open={c.phasesOpen} onOpenChange={c.setPhasesOpen}>
        <p className="text-sm text-muted-foreground">{t('loan_form.phases_help')}</p>
        {c.plain ? null : c.phases.fields.map((row, i) => (
          <fieldset key={row.id} className="space-y-3 rounded-md border p-3">
            <legend className="px-1 text-sm font-medium">{t('loan_form.phase_n', { n: i + 1 })}</legend>
            <PhaseFields form={form} index={i} last={i === c.phases.fields.length - 1} />
            {c.phases.fields.length > 1 ? (
              <Button type="button" variant="ghost" size="sm" onClick={() => c.removePhase(i)}>{t('loan_form.remove_phase', { n: i + 1 })}</Button>
            ) : null}
          </fieldset>
        ))}
        <Button ref={c.addPhaseRef} type="button" variant="outline" size="sm" onClick={c.addPhase}>
          <PlusIcon aria-hidden="true" />{t('loan_form.add_phase')}
        </Button>
      </Folded>

      <Folded label={t('loan_form.options')} hasError={Boolean(e.rounding || e.firstPeriod)}>
        <div className="grid gap-3 sm:grid-cols-2">
          <Controller control={form.control} name="rounding" render={({ field }) => (
            <Field label={t('loan_form.rounding')} error={e.rounding?.message}>
              {(f) => <SelectField {...f} value={field.value} onValueChange={field.onChange}
                options={ROUNDINGS.map((r) => ({ value: r, label: t(`loan_form.rounding_option.${r}`) }))} />}
            </Field>
          )} />
          <Controller control={form.control} name="firstPeriod" render={({ field }) => (
            <Field label={t('loan_form.first_period')} error={e.firstPeriod?.message}>
              {(f) => <SelectField {...f} value={field.value} onValueChange={field.onChange}
                options={FIRST_PERIODS.map((p) => ({ value: p, label: t(`loan_form.first_period_option.${p}`) }))} />}
            </Field>
          )} />
        </div>
      </Folded>
    </>
  );
}

/** « Les assurances »: one block per insurance, at most twenty. */
export function InsuranceSection({ contract: c }: { contract: Contract }) {
  const { t } = useTranslation();
  const { form } = c;
  return (
    <div className="space-y-3">
      {c.insurances.fields.map((row, i) => (
        <fieldset key={row.id} className="space-y-3 rounded-md border p-3">
          <legend className="px-1 text-sm font-medium">{t('loan_form.insurance_n', { n: i + 1 })}</legend>
          <InsuranceFields form={form} index={i} />
          <Button type="button" variant="ghost" size="sm" onClick={() => c.removeInsurance(i)}>{t('loan_form.remove_insurance', { n: i + 1 })}</Button>
        </fieldset>
      ))}
      {c.insurances.fields.length < MAX_INSURANCES ? (
        <Button ref={c.addInsuranceRef} type="button" variant="outline" size="sm"
          onClick={() => c.insurances.append(newInsurance(form.getValues('firstPaymentDate')))}>
          <PlusIcon aria-hidden="true" />{t('loan_form.add_insurance')}
        </Button>
      ) : null}
    </div>
  );
}

/** The whole contract on one page, the contract page's: it reads its form from a `FormProvider`. */
export function ContractFields({ startListed = false, stored = false }: { startListed?: boolean; stored?: boolean }) {
  const { t } = useTranslation();
  const contract = useContract(useFormContext<ContractValues>(), { startListed, stored });
  const termsId = useId();
  const insurancesId = useId();
  return (
    <>
      <section aria-labelledby={termsId} className="space-y-3">
        <h2 id={termsId} className="text-lg font-semibold">{t('loan_form.terms')}</h2>
        <LoanFields contract={contract} />
      </section>
      <section aria-labelledby={insurancesId} className="space-y-3">
        <h2 id={insurancesId} className="text-lg font-semibold">{t('loan_form.insurances')}</h2>
        <InsuranceSection contract={contract} />
      </section>
    </>
  );
}
