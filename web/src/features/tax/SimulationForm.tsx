import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Section } from '@/components/page/Section';
import { REGIMES, regimeText } from './format';
import { AMOUNT, toWireAmount } from '../subscriptions/edits/amount';
import { STATUSES, TAX_SIMULATE, type TaxEstimate, type TaxInputs, type TaxResult } from './api';
import { RATE } from './ProfileForm';
import { ResultTable } from './ResultTable';
import '@/locales/tax';

const AMOUNTS = ['rentalGross', 'rentalInterest', 'rentalCharges', 'capitalIncome', 'nonTaxableIncome', 'pinelReduction',
  'childcareOutsideHome', 'homeEmployment', 'donations', 'donationsAid', 'rentalDeficitCarried'] as const;
const DECLARANT_AMOUNTS = ['salaryGross', 'salaryTaxable', 'otherTaxable', 'perContributions', 'perUnusedCeiling', 'previousYearIncome', 'perExpiring', 'exemptIncome'] as const;
const COUNT = /^\d{1,2}$/;

/**
 * Tax estimate spec §8: the estimate's inputs, prefilled and editable (« et si je passais cadre ? », « et si on versait 5 000 €
 * sur le PER ? »); the server computes, nothing is saved, and the result sits beside the estimate.
 */
export function SimulationForm({ estimate }: { estimate: TaxEstimate }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [inputs, setInputs] = useState<TaxInputs>(estimate.inputs);
  const [counts, setCounts] = useState({ dependents: String(estimate.inputs.dependents), childrenUnder6: String(estimate.inputs.childrenUnder6) });
  const [result, setResult] = useState<TaxResult | null>(null);
  const simulate = $api.useMutation('post', TAX_SIMULATE);
  const change = (next: TaxInputs) => { setInputs(next); setResult(null); };
  const valid = AMOUNTS.every((k) => AMOUNT.test(inputs[k].trim()))
    && inputs.declarants.every((d) => DECLARANT_AMOUNTS.every((k) => AMOUNT.test(d[k].trim()))
      && (!d.contributionRate?.trim() || RATE.test(d.contributionRate.trim())) && (!d.actualExpenses?.trim() || AMOUNT.test(d.actualExpenses.trim())) && (!d.perCeiling?.trim() || AMOUNT.test(d.perCeiling.trim())))
    && COUNT.test(counts.dependents) && COUNT.test(counts.childrenUnder6);
  const error = (text: string) => (AMOUNT.test(text.trim()) ? undefined : t('tax.amount_format'));

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    if (!valid) return;
    const wire = (text: string) => toWireAmount(text);
    simulate.mutate(
      {
        body: {
          ...inputs,
          ...Object.fromEntries(AMOUNTS.map((k) => [k, wire(inputs[k])])),
          dependents: Number(counts.dependents), childrenUnder6: Number(counts.childrenUnder6),
          declarants: inputs.declarants.map((d) => ({
            ...d, ...Object.fromEntries(DECLARANT_AMOUNTS.map((k) => [k, wire(d[k])])),
            contributionRate: d.contributionRate?.trim() ? wire(d.contributionRate) : null,
            actualExpenses: d.actualExpenses?.trim() ? wire(d.actualExpenses) : null,
            perCeiling: d.perCeiling?.trim() ? wire(d.perCeiling) : null,
          })),
        },
      },
      { onSuccess: setResult },
    );
  };

  return (
    <Section title={t('tax.simulation.title')} announcement={result ? t('tax.simulation.done') : ''}>
      <p className="text-sm text-muted-foreground">{t('tax.simulation.help')}</p>
      {simulate.error ? <ProblemMessage problem={present(toProblem(simulate.error))} /> : null}
      <form noValidate onSubmit={submit} className="space-y-6">
        {inputs.declarants.map((d, i) => (
          <fieldset key={d.name || i} className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <legend className="mb-2 font-medium">{d.name || t('tax.simulation.declarant')}</legend>
            {DECLARANT_AMOUNTS.map((k) => (
              <Field key={k} label={t(`tax.input.${k}`)} error={error(d[k])}>
                {(c) => <Input inputMode="decimal" {...c} value={d[k]}
                  onChange={(e) => change({ ...inputs, declarants: inputs.declarants.map((x, j) => (j === i ? { ...x, [k]: e.target.value } : x)) })} />}
              </Field>
            ))}
            {(['contributionRate', 'actualExpenses', 'perCeiling'] as const).map((k) => (
              <Field key={k} label={t(`tax.input.${k}`)} hint={t(`tax.input.${k}_hint`)}
                error={!d[k]?.trim() || (k === 'contributionRate' ? RATE : AMOUNT).test(d[k].trim()) ? undefined : t(k === 'contributionRate' ? 'tax.rate_format' : 'tax.amount_format')}>
                {(c) => <Input inputMode="decimal" {...c} value={d[k] ?? ''}
                  onChange={(e) => change({ ...inputs, declarants: inputs.declarants.map((x, j) => (j === i ? { ...x, [k]: e.target.value } : x)) })} />}
              </Field>
            ))}
            <Field label={t('tax.input.status')}>
              {(c) => <SelectField {...c} value={d.status}
                onValueChange={(v) => change({ ...inputs, declarants: inputs.declarants.map((x, j) => (j === i ? { ...x, status: v } : x)) })}
                options={STATUSES.map((s) => ({ value: s, label: t(`tax.status.${s}`) }))} />}
            </Field>
          </fieldset>
        ))}
        <fieldset className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
          <legend className="mb-2 font-medium">{t('tax.simulation.household')}</legend>
          {(['dependents', 'childrenUnder6'] as const).map((k) => (
            <Field key={k} label={t(`tax.input.${k}`)} error={COUNT.test(counts[k]) ? undefined : t('tax.count_format')}>
              {(c) => <Input inputMode="numeric" {...c} value={counts[k]} onChange={(e) => { setCounts({ ...counts, [k]: e.target.value }); setResult(null); }} />}
            </Field>
          ))}
          {AMOUNTS.map((k) => (
            <Field key={k} label={t(`tax.input.${k}`)} error={error(inputs[k])}>
              {(c) => <Input inputMode="decimal" {...c} value={inputs[k]} onChange={(e) => change({ ...inputs, [k]: e.target.value })} />}
            </Field>
          ))}
          {inputs.declarants.length > 1 ? (
            <Field inline label={t('tax.input.perPooling')}>
              {(c) => <Checkbox {...c} checked={inputs.perPooling} onCheckedChange={(v) => change({ ...inputs, perPooling: v === true })} />}
            </Field>
          ) : null}
          <div className="space-y-1">
            <span className="block text-sm">{t('tax.profile.regime')}</span>
            <Segmented aria-label={t('tax.profile.regime')} value={inputs.rentalRegime ?? 'auto'}
              onValueChange={(v) => change({ ...inputs, rentalRegime: v === 'auto' ? null : v })}
              options={REGIMES.map((r) => ({ value: r, label: regimeText(t, r) }))} />
          </div>
        </fieldset>
        <div className="flex gap-2">
          <Button type="submit" disabled={simulate.isPending}>{t('tax.simulation.submit')}</Button>
          <Button type="button" variant="outline" onClick={() => { setInputs(estimate.inputs); setResult(null); }}>{t('tax.simulation.reset')}</Button>
        </div>
      </form>
      {result ? (
        <ResultTable label={t('tax.simulation.result')}
          columns={[{ title: t('tax.estimate'), result: estimate.result }, { title: t('tax.simulation.column'), result }]} />
      ) : null}
    </Section>
  );
}
