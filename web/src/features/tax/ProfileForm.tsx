import { useQueryClient } from '@tanstack/react-query';
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
import { Folded } from '../loans/new/Folded';
import { percentText, REGIMES, regimeText, ZERO } from './format';
import { AMOUNT, toWireAmount } from '../subscriptions/edits/amount';
import { DASHBOARD } from '../dashboard/api';
import { invalidateGoals } from '../goals/api';
import { STATUSES, TAX, TAX_2044, TAX_PROFILE, type PinelCommitment, type TaxEstimate } from './api';
import '@/locales/tax';

/** A Pinel commitment as one choice: the initial years, then the total after the extensions. */
const ENGAGEMENTS = ['6-6', '9-9', '6-9', '6-12', '9-12'] as const;
const COUNT = /^\d{1,2}$/;
/** A percent as typed (`23,5`): empty keeps the status's rate. */
export const RATE = /^\d{1,2}([.,]\d{1,2})?$/;
const optional = (re: RegExp, text: string) => text.trim() === '' || re.test(text.trim());

/** Tax estimate spec §7: the household's declarations for the year, saved for both adults (a joint declaration). */
export function ProfileForm({ estimate, scope }: { estimate: TaxEstimate; scope: 'me' | 'household' }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const p = estimate.profile;
  const [statuses, setStatuses] = useState<Record<string, string>>(
    Object.fromEntries(estimate.adults.map((a) => [a.id, p.statuses[a.id] ?? 'non_executive'])));
  const [dependents, setDependents] = useState(p.dependents === null || p.dependents === undefined ? '' : String(p.dependents));
  const [under6, setUnder6] = useState(String(p.childrenUnder6));
  const [regime, setRegime] = useState(p.rentalRegime ?? 'auto');
  const [amounts, setAmounts] = useState({
    childcareOutsideHome: p.childcareOutsideHome, homeEmployment: p.homeEmployment, donations: p.donations, donationsAid: p.donationsAid,
    rentalDeficitCarried: p.rentalDeficitCarried,
  });
  const [pinel, setPinel] = useState<PinelCommitment[]>(p.pinel);
  const [rates, setRates] = useState<Record<string, string>>(p.contributionRates);
  const [expenses, setExpenses] = useState<Record<string, string>>(p.actualExpenses);
  const [unused, setUnused] = useState<Record<string, string>>(p.perUnusedCeilings);
  // The unused ceilings are a fallback for a missing tax notice: folded unless already typed.
  const [unusedOpen, setUnusedOpen] = useState<Record<string, boolean>>({});
  const [ceilings, setCeilings] = useState<Record<string, string>>(p.perCeilings);
  const [expiring, setExpiring] = useState<Record<string, string>>(p.perCeilingsExpiring);
  const [pooling, setPooling] = useState(p.perPooling);
  const [exempt, setExempt] = useState<Record<string, string>>(p.exemptIncomes);
  const [exemptOpen, setExemptOpen] = useState(Object.values(p.exemptIncomes).some((v) => !ZERO.test(v)));
  // Only the adults' typed values go back: an empty one keeps the default.
  const filled = (values: Record<string, string>) =>
    Object.fromEntries(Object.entries(values).filter(([, v]) => v.trim() !== '').map(([k, v]) => [k, toWireAmount(v)]));
  const [announcement, setAnnouncement] = useState('');
  const save = $api.useMutation('put', TAX_PROFILE);

  const amountOk = Object.values(amounts).every((a) => AMOUNT.test(a.trim()));
  const countsOk = (dependents === '' || COUNT.test(dependents)) && COUNT.test(under6)
    && Object.values(rates).every((v) => optional(RATE, v)) && Object.values(expenses).every((v) => optional(AMOUNT, v))
    && Object.values(unused).every((v) => optional(AMOUNT, v)) && Object.values(ceilings).every((v) => optional(AMOUNT, v))
    && Object.values(expiring).every((v) => optional(AMOUNT, v)) && Object.values(exempt).every((v) => optional(AMOUNT, v));
  const amountField = (key: keyof typeof amounts, hint?: string) => (
    <Field label={t(`tax.profile.${key}`)} hint={hint} error={AMOUNT.test(amounts[key].trim()) ? undefined : t('tax.amount_format')}>
      {(c) => <Input inputMode="decimal" {...c} value={amounts[key]} onChange={(e) => setAmounts({ ...amounts, [key]: e.target.value })} />}
    </Field>
  );
  const engagementOf = (property: string) => {
    const c = pinel.find((x) => x.property === property);
    return c ? `${c.initialYears}-${c.totalYears}` : '';
  };
  const setEngagement = (property: string, value: string) => {
    const rest = pinel.filter((x) => x.property !== property);
    if (!value) { setPinel(rest); return; }
    const [initialYears, totalYears] = value.split('-');
    const plus = pinel.find((x) => x.property === property)?.plus ?? false;
    setPinel([...rest, { property, initialYears: Number(initialYears), totalYears: Number(totalYears), startYear: null, plus }]);
  };

  const submit = (event: React.FormEvent) => {
    event.preventDefault();
    setAnnouncement('');
    if (!amountOk || !countsOk) return;
    save.mutate(
      {
        params: { path: { year: estimate.year }, query: { scope } },
        body: {
          statuses, dependents: dependents === '' ? null : Number(dependents), childrenUnder6: Number(under6),
          rentalRegime: regime === 'auto' ? null : regime, pinel, contributionRates: filled(rates), actualExpenses: filled(expenses),
          perUnusedCeilings: filled(unused),
          perCeilings: filled(ceilings), perCeilingsExpiring: filled(expiring), perPooling: pooling, exemptIncomes: filled(exempt),
          rentalDeficitCarried: toWireAmount(amounts.rentalDeficitCarried),
          childcareOutsideHome: toWireAmount(amounts.childcareOutsideHome), homeEmployment: toWireAmount(amounts.homeEmployment),
          donations: toWireAmount(amounts.donations), donationsAid: toWireAmount(amounts.donationsAid),
        },
      },
      {
        onSuccess: () => {
          // Every read the profile feeds: the estimate, the 2044 (regime, carried deficit), the dashboard's and the goals' tax commitment.
          for (const path of [TAX, TAX_2044, DASHBOARD]) void queryClient.invalidateQueries({ queryKey: ['get', path] });
          void invalidateGoals(queryClient);
          setAnnouncement(t('tax.profile.saved'));
        },
      },
    );
  };

  return (
    <Section title={t('tax.profile.title')} announcement={announcement}>
      <p className="text-sm text-muted-foreground">
        {estimate.profileFrom ? t('tax.profile.from', { year: estimate.profileFrom }) : t('tax.profile.help')}
      </p>
      {save.error ? <ProblemMessage problem={present(toProblem(save.error))} /> : null}
      <form noValidate onSubmit={submit} className="space-y-6">
        <fieldset className="grid gap-3 sm:grid-cols-2">
          <legend className="mb-2 font-medium">{t('tax.profile.household')}</legend>
          {estimate.adults.map((a) => (
            <div key={a.id} className="grid gap-3 sm:col-span-2 sm:grid-cols-2 lg:grid-cols-4">
              <Field label={t('tax.profile.status', { name: a.displayName })} hint={t('tax.profile.status_hint')}>
                {(c) => <SelectField {...c} value={statuses[a.id] ?? 'non_executive'} onValueChange={(v) => setStatuses({ ...statuses, [a.id]: v })}
                  options={STATUSES.map((s) => ({ value: s, label: t(`tax.status.${s}`) }))} />}
              </Field>
              <Field label={t('tax.profile.rate', { name: a.displayName })} hint={estimate.observedRates[a.id] ? `${t('tax.profile.rate_hint')} ${t('tax.profile.observed_rate', { rate: percentText(t, estimate.observedRates[a.id]!) })}` : t('tax.profile.rate_hint')}
                error={optional(RATE, rates[a.id] ?? '') ? undefined : t('tax.rate_format')}>
                {(c) => <Input inputMode="decimal" {...c} value={rates[a.id] ?? ''} onChange={(e) => setRates({ ...rates, [a.id]: e.target.value })} />}
              </Field>
              <Field label={t('tax.profile.expenses_actual', { name: a.displayName })} hint={t('tax.profile.expenses_actual_hint')}
                error={optional(AMOUNT, expenses[a.id] ?? '') ? undefined : t('tax.amount_format')}>
                {(c) => <Input inputMode="decimal" {...c} value={expenses[a.id] ?? ''} onChange={(e) => setExpenses({ ...expenses, [a.id]: e.target.value })} />}
              </Field>
            </div>
          ))}
          {estimate.adults.map((a) => (
            <fieldset key={a.id} className="grid gap-3 sm:col-span-2 sm:grid-cols-2">
              <legend className="mb-2 text-sm text-muted-foreground">{t('tax.profile.per_legend', { name: a.displayName })}</legend>
              <Field label={t('tax.profile.per_ceiling')} hint={t('tax.profile.per_ceiling_hint')}
                error={optional(AMOUNT, ceilings[a.id] ?? '') ? undefined : t('tax.amount_format')}>
                {(c) => <Input inputMode="decimal" {...c} value={ceilings[a.id] ?? ''} onChange={(e) => setCeilings({ ...ceilings, [a.id]: e.target.value })} />}
              </Field>
              <Field label={t('tax.profile.per_expiring')} error={optional(AMOUNT, expiring[a.id] ?? '') ? undefined : t('tax.amount_format')}>
                {(c) => <Input inputMode="decimal" {...c} value={expiring[a.id] ?? ''} onChange={(e) => setExpiring({ ...expiring, [a.id]: e.target.value })} />}
              </Field>
              <div className="sm:col-span-2">
                <Folded label={t('tax.profile.per_no_notice')} hasError={!optional(AMOUNT, unused[a.id] ?? '')}
                  open={unusedOpen[a.id] ?? Boolean(p.perUnusedCeilings[a.id])} onOpenChange={(open) => setUnusedOpen({ ...unusedOpen, [a.id]: open })}>
                  <div className="grid gap-3 sm:grid-cols-2">
                    <Field label={t('tax.profile.per_unused', { name: a.displayName })} hint={t('tax.profile.per_unused_hint')}
                      error={optional(AMOUNT, unused[a.id] ?? '') ? undefined : t('tax.amount_format')}>
                      {(c) => <Input inputMode="decimal" {...c} value={unused[a.id] ?? ''} onChange={(e) => setUnused({ ...unused, [a.id]: e.target.value })} />}
                    </Field>
                  </div>
                </Folded>
              </div>
            </fieldset>
          ))}
          {estimate.adults.length > 1 ? (
            <Field inline label={t('tax.profile.per_pooling')}>
              {(c) => <Checkbox {...c} checked={pooling} onCheckedChange={(v) => setPooling(v === true)} />}
            </Field>
          ) : null}
          {/* Rare: folded unless a value is saved. */}
          <div className="sm:col-span-2">
            <Folded label={t('tax.profile.exempt_toggle')} hasError={!Object.values(exempt).every((v) => optional(AMOUNT, v))}
              open={exemptOpen} onOpenChange={setExemptOpen}>
              <div className="grid gap-3 sm:grid-cols-2">
                {estimate.adults.map((a) => (
                  <Field key={a.id} label={t('tax.profile.exempt', { name: a.displayName })} hint={t('tax.profile.exempt_hint')}
                    error={optional(AMOUNT, exempt[a.id] ?? '') ? undefined : t('tax.amount_format')}>
                    {(c) => <Input inputMode="decimal" {...c} value={exempt[a.id] ?? ''} onChange={(e) => setExempt({ ...exempt, [a.id]: e.target.value })} />}
                  </Field>
                ))}
              </div>
            </Folded>
          </div>
          <Field label={t('tax.profile.dependents')} hint={t('tax.profile.dependents_hint', { count: estimate.dependents })}
            error={dependents === '' || COUNT.test(dependents) ? undefined : t('tax.count_format')}>
            {(c) => <Input inputMode="numeric" {...c} value={dependents} onChange={(e) => setDependents(e.target.value)} />}
          </Field>
          <Field label={t('tax.profile.under6')} error={COUNT.test(under6) ? undefined : t('tax.count_format')}>
            {(c) => <Input inputMode="numeric" {...c} value={under6} onChange={(e) => setUnder6(e.target.value)} />}
          </Field>
        </fieldset>
        <fieldset className="grid gap-3 sm:grid-cols-2">
          <legend className="mb-2 font-medium">{t('tax.profile.expenses')}</legend>
          {amountField('childcareOutsideHome', t('tax.profile.childcare_hint'))}
          {amountField('homeEmployment', t('tax.profile.home_hint'))}
          {amountField('donations')}
          {amountField('donationsAid', t('tax.profile.aid_hint'))}
        </fieldset>
        {estimate.rentalProperties.length ? (
          <fieldset className="space-y-3">
            <legend className="mb-2 font-medium">{t('tax.profile.rental')}</legend>
            <Segmented aria-label={t('tax.profile.regime')} value={regime} onValueChange={setRegime}
              options={REGIMES.map((r) => ({ value: r, label: regimeText(t, r) }))} />
            <div className="grid gap-3 sm:grid-cols-2">{amountField('rentalDeficitCarried', t('tax.profile.deficit_carried_hint'))}</div>
            {estimate.rentalProperties.map((property) => (
              <div key={property.id} className="grid items-end gap-3 sm:grid-cols-2">
                <Field label={t('tax.profile.pinel', { name: property.name, year: property.acquiredIn })}>
                  {(c) => <SelectField {...c} value={engagementOf(property.id)} onValueChange={(v) => setEngagement(property.id, v)}
                    options={[{ value: '', label: t('tax.engagement.none') }, ...ENGAGEMENTS.map((e) => ({ value: e, label: t(`tax.engagement.${e}`) }))]} />}
                </Field>
                {engagementOf(property.id) ? (
                  <Field inline label={t('tax.profile.pinel_plus')}>
                    {(c) => <Checkbox {...c} checked={pinel.find((x) => x.property === property.id)?.plus ?? false}
                      onCheckedChange={(v) => setPinel(pinel.map((x) => (x.property === property.id ? { ...x, plus: v === true } : x)))} />}
                  </Field>
                ) : null}
              </div>
            ))}
          </fieldset>
        ) : null}
        <Button type="submit" disabled={save.isPending}>{t('tax.profile.save')}</Button>
      </form>
    </Section>
  );
}
