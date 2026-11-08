import type { TFunction } from 'i18next';
import { Controller, useFieldArray, useFormContext, useWatch, type Path } from 'react-hook-form';
import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { ComboboxField } from '@/components/form/ComboboxField';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { formatMoney } from '@/components/Money';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { CLASSES, useClassName } from '@/features/dashboard/panels/allocationNames';
import { Folded } from '@/components/form/Folded';
import { ISO_DATE, toFieldAmount, toWireAmount } from '@/lib/amount';
import type { PropertyPurchaseDoc, Scenario, ScenarioDefaults } from '../api';
import '@/locales/forms';
import '@/locales/scenarios';

type Holding = components['schemas']['FundingCandidateView'];

export type LoanDraft = {
  name: string; amount: string; rate: string; months: string;
  deferral: { kind: string; months: string }; insurance: { kind: string; value: string }; fileFees: string; guaranteeFees: string;
};

/** The scenario as typed (spec §3), its name and visibility then its document: every figure a string, sent as typed with a dot; the server validates it. */
export type ScenarioDraft = {
  name: string; visibility: string; use: string; purchaseOn: string; price: string; notaryFees: string; agencyFees: string; otherCosts: string; monthlyCharges: string;
  currentRent: string; expectedRent: string; vacancyMonths: string; rentalRegime: string;
  funding: { item: string; amount: string }[];
  loans: LoanDraft[];
  assumptions: { horizonYears: string; classRates: Record<string, string>; propertyGrowth: string; monthlySavings: string; savingsClass: string };
};

export type ScenarioStep = 'property' | 'funding' | 'loans' | 'assumptions';

/** The roots of the draft each step holds: what « Continuer » checks, and where a refusal sends the person back to. */
export const STEP_FIELDS: Record<ScenarioStep, string[]> = {
  property: ['name', 'visibility', 'use', 'purchaseOn', 'price', 'notaryFees', 'agencyFees', 'otherCosts', 'monthlyCharges', 'currentRent', 'expectedRent', 'vacancyMonths', 'rentalRegime'],
  funding: ['funding'],
  loans: ['loans'],
  assumptions: ['assumptions'],
};

const MAX_LOANS = 4;

export const newLoan = (t: TFunction, n: number): LoanDraft => ({
  name: t('scenarios.form.loan', { n }), amount: '', rate: '', months: '', deferral: { kind: '', months: '' },
  insurance: { kind: 'rate_on_initial', value: '0' }, fileFees: '0', guaranteeFees: '0',
});

/** The server's rates as a French reader types them (`6.0` → `6,0`). */
const fieldRates = (rates: Record<string, string>) => Object.fromEntries(Object.entries(rates).map(([k, v]) => [k, toFieldAmount(v)]));

/** A blank scenario, its rates pre-filled from the defaults (spec §3); the growth defaults to the `real_estate` rate. */
export function newDraft(t: TFunction, defaults: ScenarioDefaults): ScenarioDraft {
  return {
    name: '', visibility: 'shared', use: 'primary', purchaseOn: '', price: '', notaryFees: '0', agencyFees: '0', otherCosts: '0', monthlyCharges: '0',
    currentRent: '0', expectedRent: '0', vacancyMonths: '0', rentalRegime: '',
    funding: [], loans: [newLoan(t, 1)],
    assumptions: {
      horizonYears: '10', classRates: fieldRates(defaults.classRates), propertyGrowth: toFieldAmount(defaults.classRates.real_estate ?? ''),
      monthlySavings: '0', savingsClass: 'cash',
    },
  };
}

// A count (months, years), not an amount: anything unreadable goes as 0 for the server to refuse on its field.
const int = (text: string) => Number.parseInt(text, 10) || 0;

/** The wire document: amounts and rates as typed with a dot, the other use's rent fields at 0, a line without its envelope left out. */
export function toDocument(d: ScenarioDraft): PropertyPurchaseDoc {
  const rental = d.use === 'rental';
  const a = d.assumptions;
  // An unreadable date would be refused whole (400): left out, the server names it on its field.
  return {
    version: 1, use: d.use, ...(ISO_DATE.test(d.purchaseOn) ? { purchaseOn: d.purchaseOn } : {}),
    price: toWireAmount(d.price), notaryFees: toWireAmount(d.notaryFees), agencyFees: toWireAmount(d.agencyFees),
    otherCosts: toWireAmount(d.otherCosts), monthlyCharges: toWireAmount(d.monthlyCharges),
    currentRent: rental ? '0' : toWireAmount(d.currentRent), expectedRent: rental ? toWireAmount(d.expectedRent) : '0',
    vacancyMonths: rental ? int(d.vacancyMonths) : 0, rentalRegime: rental && d.rentalRegime ? d.rentalRegime : null,
    funding: d.funding.filter((f) => f.item).map((f) => ({ item: f.item, amount: toWireAmount(f.amount) })),
    loans: d.loans.map((l) => ({
      name: l.name.trim(), amount: toWireAmount(l.amount), rate: toWireAmount(l.rate), months: int(l.months),
      deferral: l.deferral.kind ? { kind: l.deferral.kind, months: int(l.deferral.months) } : null,
      insurance: { kind: l.insurance.kind, value: toWireAmount(l.insurance.value) },
      fileFees: toWireAmount(l.fileFees), guaranteeFees: toWireAmount(l.guaranteeFees),
    })),
    assumptions: {
      horizonYears: int(a.horizonYears),
      classRates: Object.fromEntries(Object.entries(a.classRates).map(([k, v]) => [k, toWireAmount(v)])),
      propertyGrowth: a.propertyGrowth.trim() ? toWireAmount(a.propertyGrowth) : null,
      monthlySavings: toWireAmount(a.monthlySavings), savingsClass: a.savingsClass,
    },
  } as PropertyPurchaseDoc;
}

/** A saved scenario back as the form's draft: the inverse of `toDocument`, every figure the server's string. */
export function fromDocument(s: Pick<Scenario, 'name' | 'visibility' | 'document'>): ScenarioDraft {
  const d = s.document;
  const a = d.assumptions;
  return {
    name: s.name, visibility: s.visibility, use: d.use, purchaseOn: d.purchaseOn, price: toFieldAmount(d.price), notaryFees: toFieldAmount(d.notaryFees),
    agencyFees: toFieldAmount(d.agencyFees), otherCosts: toFieldAmount(d.otherCosts), monthlyCharges: toFieldAmount(d.monthlyCharges),
    currentRent: toFieldAmount(d.currentRent), expectedRent: toFieldAmount(d.expectedRent), vacancyMonths: String(d.vacancyMonths), rentalRegime: d.rentalRegime ?? '',
    funding: d.funding.map((f) => ({ item: f.item, amount: toFieldAmount(f.amount) })),
    loans: d.loans.map((l) => ({
      name: l.name, amount: toFieldAmount(l.amount), rate: toFieldAmount(l.rate), months: String(l.months),
      deferral: l.deferral ? { kind: l.deferral.kind, months: String(l.deferral.months) } : { kind: '', months: '' },
      insurance: { kind: l.insurance.kind, value: toFieldAmount(l.insurance.value) }, fileFees: toFieldAmount(l.fileFees), guaranteeFees: toFieldAmount(l.guaranteeFees),
    })),
    assumptions: {
      horizonYears: String(a.horizonYears), classRates: fieldRates(a.classRates), propertyGrowth: toFieldAmount(a.propertyGrowth ?? ''),
      monthlySavings: toFieldAmount(a.monthlySavings), savingsClass: a.savingsClass,
    },
  };
}

/** A server path (`loans[0].rate`, `assumptions.classRates.equity`) as the draft's field, when a step holds it. */
export function draftPath(path: string): Path<ScenarioDraft> | undefined {
  const name = path.replace(/\[(\d+)\]/g, '.$1');
  const root = name.split('.')[0]!;
  return Object.values(STEP_FIELDS).some((roots) => roots.includes(root)) ? (name as Path<ScenarioDraft>) : undefined;
}

/** The first step holding one of these fields. */
export const firstStepOf = (names: string[]) =>
  (Object.keys(STEP_FIELDS) as ScenarioStep[]).find((s) => names.some((n) => STEP_FIELDS[s].includes(n.split('.')[0]!)));

/** One step's fields of a scenario (spec §7), read from the surrounding `FormProvider<ScenarioDraft>`. */
export function ScenarioFields({ step, holdings }: { step: ScenarioStep; holdings: Holding[] }) {
  switch (step) {
    case 'property': return <PropertyStep />;
    case 'funding': return <FundingStep holdings={holdings} />;
    case 'loans': return <LoansStep />;
    case 'assumptions': return <AssumptionsStep />;
  }
}

/** A text field: an amount or a rate unless `text`. */
function TextField({ name, label, hint, numeric, text }: { name: Path<ScenarioDraft>; label: string; hint?: string; numeric?: boolean; text?: boolean }) {
  const form = useFormContext<ScenarioDraft>();
  const message = form.getFieldState(name, form.formState).error?.message;
  const error = typeof message === 'string' ? message : undefined;
  return (
    <Field label={label} hint={hint} error={error}>
      {(c) => <Input inputMode={text ? undefined : numeric ? 'numeric' : 'decimal'} {...c} {...form.register(name)} />}
    </Field>
  );
}

function PropertyStep() {
  const { t } = useTranslation();
  const form = useFormContext<ScenarioDraft>();
  const use = useWatch({ control: form.control, name: 'use' });
  const e = form.formState.errors;
  const visibilityId = useId();
  return (
    <>
      <TextField name="name" label={t('scenarios.form.name')} text />
      <div className="space-y-1">
        <span id={visibilityId} className="block text-sm">{t('scenarios.form.visibility')}</span>
        <Controller control={form.control} name="visibility" render={({ field }) => (
          <Segmented aria-labelledby={visibilityId} value={field.value} onValueChange={field.onChange}
            options={(['shared', 'private'] as const).map((v) => ({ value: v, label: t(`scenarios.form.visibility_${v}`) }))} />
        )} />
      </div>
      <Controller control={form.control} name="use" render={({ field }) => (
        <Segmented aria-label={t('scenarios.form.use')} value={field.value} onValueChange={field.onChange}
          options={(['primary', 'rental'] as const).map((v) => ({ value: v, label: t(`scenarios.use.${v}`) }))} />
      )} />
      <div className="grid gap-3 sm:grid-cols-3">
        <Controller control={form.control} name="purchaseOn" render={({ field }) => (
          <Field label={t('scenarios.form.purchase_on')} error={e.purchaseOn?.message}>
            {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
        <TextField name="price" label={t('scenarios.form.price')} />
        <TextField name="notaryFees" label={t('scenarios.form.notary_fees')} />
        <TextField name="agencyFees" label={t('scenarios.form.agency_fees')} />
        <TextField name="otherCosts" label={t('scenarios.form.other_costs')} />
        <TextField name="monthlyCharges" label={t('scenarios.form.monthly_charges')} hint={t('scenarios.form.monthly_charges_hint')} />
      </div>
      {use === 'rental' ? (
        <div className="grid gap-3 sm:grid-cols-3">
          <TextField name="expectedRent" label={t('scenarios.form.expected_rent')} />
          <TextField name="vacancyMonths" label={t('scenarios.form.vacancy_months')} numeric />
          <Controller control={form.control} name="rentalRegime" render={({ field }) => (
            <Field label={t('scenarios.form.rental_regime')} error={e.rentalRegime?.message}>
              {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                options={[{ value: '', label: t('scenarios.form.regime.profile') }, { value: 'micro', label: t('scenarios.form.regime.micro') }, { value: 'real', label: t('scenarios.form.regime.real') }]} />}
            </Field>
          )} />
        </div>
      ) : (
        <div className="grid gap-3 sm:grid-cols-3">
          <TextField name="currentRent" label={t('scenarios.form.current_rent')} hint={t('scenarios.form.current_rent_hint')} />
        </div>
      )}
    </>
  );
}

function FundingStep({ holdings }: { holdings: Holding[] }) {
  const { t } = useTranslation();
  const form = useFormContext<ScenarioDraft>();
  const lines = useFieldArray({ control: form.control, name: 'funding' });
  // A locked envelope is listed, never pickable (spec §3): its label says why.
  const options = holdings.map((h) => (h.liquidity === 'locked'
    ? { value: h.id, label: t('scenarios.form.envelope_locked', { name: h.name }), disabled: true }
    : { value: h.id, label: h.name }));
  return (
    <>
      <p className="text-sm text-muted-foreground">{t('scenarios.form.funding_hint')}</p>
      {lines.fields.map((line, i) => {
        const e = form.formState.errors.funding?.[i];
        return (
          <fieldset key={line.id} className="space-y-3 rounded-md border p-3">
            <legend className="px-1 text-sm font-medium">{t('scenarios.form.funding_line', { n: i + 1 })}</legend>
            <div className="grid gap-3 sm:grid-cols-2">
              <Controller control={form.control} name={`funding.${i}.item`} rules={{ required: t('edit.required') }} render={({ field }) => {
                const picked = holdings.find((h) => h.id === field.value);
                return (
                  <Field label={t('scenarios.form.envelope')} error={e?.item?.message}
                    hint={picked ? t('scenarios.form.envelope_value', { amount: formatMoney(picked.value) }) : undefined}>
                    {(c) => <ComboboxField {...c} value={field.value} onValueChange={field.onChange} options={options}
                      searchPlaceholder={t('scenarios.form.envelope_search')} empty={t('scenarios.form.envelope_empty')} />}
                  </Field>
                );
              }} />
              <TextField name={`funding.${i}.amount`} label={t('scenarios.form.funding_amount')} />
            </div>
            <Button type="button" variant="ghost" size="sm" onClick={() => lines.remove(i)}>
              {t('scenarios.form.remove_funding', { n: i + 1 })}
            </Button>
          </fieldset>
        );
      })}
      <Button type="button" variant="outline" onClick={() => lines.append({ item: '', amount: '' })}>{t('scenarios.form.add_funding')}</Button>
    </>
  );
}

function LoansStep() {
  const { t } = useTranslation();
  const form = useFormContext<ScenarioDraft>();
  const loans = useFieldArray({ control: form.control, name: 'loans' });
  const values = useWatch({ control: form.control, name: 'loans' });
  return (
    <>
      {loans.fields.map((loan, i) => {
        const e = form.formState.errors.loans?.[i];
        const insurance = values[i]?.insurance.kind;
        return (
          <fieldset key={loan.id} className="space-y-3 rounded-md border p-3">
            <legend className="px-1 text-sm font-medium">{t('scenarios.form.loan', { n: i + 1 })}</legend>
            <div className="grid gap-3 sm:grid-cols-2">
              <TextField name={`loans.${i}.name`} label={t('scenarios.form.loan_name')} />
              <TextField name={`loans.${i}.amount`} label={t('scenarios.form.loan_amount')} />
              <TextField name={`loans.${i}.rate`} label={t('scenarios.form.loan_rate')} />
              <TextField name={`loans.${i}.months`} label={t('scenarios.form.loan_months')} numeric />
              <Controller control={form.control} name={`loans.${i}.insurance.kind`} render={({ field }) => (
                <Field label={t('scenarios.form.insurance_kind')} error={e?.insurance?.kind?.message}>
                  {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                    options={(['rate_on_initial', 'fixed_monthly'] as const).map((v) => ({ value: v, label: t(`scenarios.form.insurance.${v}`) }))} />}
                </Field>
              )} />
              <TextField name={`loans.${i}.insurance.value`}
                label={insurance === 'fixed_monthly' ? t('scenarios.form.insurance_amount') : t('scenarios.form.insurance_rate')} />
            </div>
            <Folded label={t('scenarios.form.loan_more')} hasError={Boolean(e?.deferral || e?.fileFees || e?.guaranteeFees)}>
              <div className="grid gap-3 sm:grid-cols-2">
                <Controller control={form.control} name={`loans.${i}.deferral.kind`} render={({ field }) => (
                  <Field label={t('scenarios.form.deferral')} error={e?.deferral?.kind?.message}>
                    {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
                      options={(['', 'partial', 'total'] as const).map((v) => ({ value: v, label: t(`scenarios.form.deferral_kind.${v || 'none'}`) }))} />}
                  </Field>
                )} />
                {values[i]?.deferral.kind ? <TextField name={`loans.${i}.deferral.months`} label={t('scenarios.form.deferral_months')} numeric /> : null}
                <TextField name={`loans.${i}.fileFees`} label={t('scenarios.form.file_fees')} />
                <TextField name={`loans.${i}.guaranteeFees`} label={t('scenarios.form.guarantee_fees')} />
              </div>
            </Folded>
            {loans.fields.length > 1 ? (
              <Button type="button" variant="ghost" size="sm" onClick={() => loans.remove(i)}>{t('scenarios.form.remove_loan', { n: i + 1 })}</Button>
            ) : null}
          </fieldset>
        );
      })}
      {loans.fields.length < MAX_LOANS ? (
        <Button type="button" variant="outline" onClick={() => loans.append(newLoan(t, loans.fields.length + 1))}>{t('scenarios.form.add_loan')}</Button>
      ) : null}
    </>
  );
}

function AssumptionsStep() {
  const { t } = useTranslation();
  const className = useClassName();
  const form = useFormContext<ScenarioDraft>();
  const e = form.formState.errors.assumptions;
  return (
    <>
      <div className="grid gap-3 sm:grid-cols-3">
        <TextField name="assumptions.horizonYears" label={t('scenarios.form.horizon')} numeric />
        <TextField name="assumptions.monthlySavings" label={t('scenarios.form.monthly_savings')} />
        <Controller control={form.control} name="assumptions.savingsClass" render={({ field }) => (
          <Field label={t('scenarios.form.savings_class')} error={e?.savingsClass?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange}
              options={CLASSES.map((v) => ({ value: v, label: className(v) }))} />}
          </Field>
        )} />
      </div>
      <Folded label={t('scenarios.form.rates_more')} hasError={Boolean(e?.classRates || e?.propertyGrowth)}>
        <div className="grid gap-3 sm:grid-cols-3">
          {CLASSES.map((c) => <TextField key={c} name={`assumptions.classRates.${c}`} label={t('scenarios.form.class_rate', { name: className(c) })} />)}
          <TextField name="assumptions.propertyGrowth" label={t('scenarios.form.property_growth')} />
        </div>
      </Folded>
    </>
  );
}
