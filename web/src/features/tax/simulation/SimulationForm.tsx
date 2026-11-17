import { useState, type FormEvent, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { Field } from '@/components/form/Field';
import { Folded } from '@/components/form/Folded';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { SectionCard } from '@/components/page/SectionCard';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { Toggle } from '@/components/ui/toggle';
import { STATUSES, TAX_SIMULATE, type TaxEstimate, type TaxInputs, type TaxResult } from '@/api/tax';
import { REGIMES, regimeText } from '../format';
import { DeclarantGrid, type GridRow } from '../profile/DeclarantGrid';
import {
  applyScenario, draftErrors, FIELDS, groupsWithErrors, scenarios, toBody, toDraft,
  type FieldKey, type GroupId, type ScenarioId, type SimulationDraft,
} from './simulationDraft';
import '@/locales/tax';

const LABELS = { activity: 'tax.profile.activity', per: 'tax.profile.retirement', household: 'tax.profile.household', rental: 'tax.profile.rental', capital: 'tax.profile.capital', expenses: 'tax.profile.expenses' } as const;
const FORMATS = { amount: 'tax.amount_format', rate: 'tax.rate_format', count: 'tax.count_format' } as const;
const HOUSEHOLD_ORDER: Record<GroupId, FieldKey[]> = {
  activity: [], per: [], household: ['dependents', 'childrenUnder6', 'nonTaxableIncome'],
  rental: ['rentalGross', 'rentalCharges', 'rentalInterest', 'rentalDeficitCarried', 'pinelReduction'],
  capital: ['capitalIncome', 'capitalDividends', 'deductibleCsg', 'capitalLossesCarried'],
  expenses: ['childcareOutsideHome', 'homeEmployment', 'donations', 'donationsAid'],
};
const DECLARANT_ORDER: Record<GroupId, FieldKey[]> = {
  activity: ['salaryGross', 'otherTaxable', 'salaryTaxable', 'contributionRate', 'actualExpenses', 'exemptIncome'],
  per: ['perContributions', 'perCeiling', 'perUnusedCeiling', 'perExpiring', 'previousYearIncome'],
  household: [], rental: [], capital: [], expenses: [],
};
const DEFAULT_OPEN: GroupId[] = ['activity'];
const COUNTS = ['dependents', 'childrenUnder6'] as const;
type Count = (typeof COUNTS)[number];
const isCount = (k: FieldKey): k is Count => (COUNTS as readonly string[]).includes(k);

/**
 * Tax estimate spec §8: the estimate's inputs, prefilled and editable, grouped like the profile with the rare fields folded;
 * a scenario starts the form from the estimate with one change. The server computes, nothing is saved.
 */
export function SimulationForm({ estimate, scope, onResult }: {
  estimate: TaxEstimate; scope: 'me' | 'household'; onResult: (r: TaxResult | null) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [draft, setDraft] = useState<SimulationDraft>(() => toDraft(estimate.inputs));
  const [open, setOpen] = useState<GroupId[]>(DEFAULT_OPEN);
  const [more, setMore] = useState<GroupId[]>([]);
  const [scenario, setScenario] = useState<ScenarioId | null>(null);
  const simulate = $api.useMutation('post', TAX_SIMULATE);
  const errors = draftErrors(draft);
  const folded = groupsWithErrors(errors);
  const foldedRare = new Set(Object.keys(errors).filter((k) => FIELDS[k.split('.')[0] as FieldKey].rare).map((k) => FIELDS[k.split('.')[0] as FieldKey].group));
  const message = (key: string) => (errors[key] ? t(FORMATS[errors[key]]) : undefined);

  const change = (next: SimulationDraft) => { setDraft(next); setScenario(null); simulate.reset(); onResult(null); };
  const setInput = (patch: Partial<TaxInputs>) => change({ ...draft, inputs: { ...draft.inputs, ...patch } });
  const setDeclarant = (i: number, patch: Partial<TaxInputs['declarants'][number]>) =>
    setInput({ declarants: draft.inputs.declarants.map((d, j) => (j === i ? { ...d, ...patch } : d)) });
  const start = (next: SimulationDraft, groups: GroupId[], id: ScenarioId | null) => {
    setDraft(next); setOpen(groups); setMore([]); setScenario(id); simulate.reset(); onResult(null);
  };
  const choose = (id: ScenarioId) => {
    if (scenario === id) return start(toDraft(estimate.inputs), open, null);
    const applied = applyScenario(id, estimate);
    start(applied.draft, applied.open, id);
  };
  const reset = () => start(toDraft(estimate.inputs), DEFAULT_OPEN, null);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (Object.keys(errors).length > 0) return;
    simulate.mutate(
      { body: toBody(draft), params: { query: { scope } } },
      { onSuccess: (r) => { onResult(r); toast.success(t('tax.simulation.done')); } },
    );
  };

  const adults = draft.inputs.declarants.map((d, i) => ({ id: String(i), displayName: d.name || t('tax.simulation.declarant') }));
  const input = (k: FieldKey, i: number) => draft.inputs.declarants[i]![k as 'salaryGross'] ?? '';
  const declarantRow = (k: FieldKey): GridRow => ({
    key: k, label: t(`tax.input.${k as 'salaryGross'}`),
    hint: k === 'contributionRate' || k === 'actualExpenses' || k === 'perCeiling' ? t(`tax.input.${k}_hint`) : undefined,
    error: (id) => message(`${k}.${id}`),
    cell: (a, c) => <Input inputMode="decimal" {...c} value={input(k, Number(a.id))} onChange={(e) => setDeclarant(Number(a.id), { [k]: e.target.value })} />,
  });
  const householdField = (k: FieldKey) => (isCount(k) ? (
    <Field key={k} label={t(`tax.input.${k}`)} error={message(k)}>
      {(c) => <Input inputMode="numeric" {...c} value={draft.counts[k]} onChange={(e) => change({ ...draft, counts: { ...draft.counts, [k]: e.target.value } })} />}
    </Field>
  ) : (
    <Field key={k} label={t(`tax.input.${k as 'rentalGross'}`)} error={message(k)}>
      {(c) => <Input inputMode="decimal" {...c} value={draft.inputs[k as 'rentalGross']} onChange={(e) => setInput({ [k]: e.target.value })} />}
    </Field>
  ));
  const grid = (group: GroupId, keys: FieldKey[], extra: GridRow[] = []) => (
    <DeclarantGrid label={t(LABELS[group])} adults={adults} rows={[...extra, ...keys.map(declarantRow)]} />
  );
  const statusRow: GridRow = {
    key: 'status', label: t('tax.input.status'),
    cell: (a, c) => <SelectField {...c} value={draft.inputs.declarants[Number(a.id)]!.status} onValueChange={(v) => setDeclarant(Number(a.id), { status: v })}
      options={STATUSES.map((s) => ({ value: s, label: t(`tax.status.${s}`) }))} />,
  };
  const rare = (group: GroupId, content: ReactNode) => (
    <Folded label={t('tax.simulation.more')} hasError={foldedRare.has(group)} open={more.includes(group)}
      onOpenChange={(o) => setMore(o ? [...more, group] : more.filter((g) => g !== group))}>
      {content}
    </Folded>
  );

  const body = (group: GroupId): ReactNode => {
    const decl = DECLARANT_ORDER[group];
    const house = HOUSEHOLD_ORDER[group];
    const common = (keys: FieldKey[]) => keys.filter((k) => !FIELDS[k].rare);
    const rarer = (keys: FieldKey[]) => keys.filter((k) => FIELDS[k].rare);
    const cols = 'grid gap-3 sm:grid-cols-2 lg:grid-cols-3';
    return (
      <>
        {decl.length ? grid(group, common(decl), group === 'activity' ? [statusRow] : []) : null}
        {house.length ? <div className={cols}>{common(house).map(householdField)}</div> : null}
        {group === 'per' && adults.length > 1 ? (
          <Field inline label={t('tax.input.perPooling')}>
            {(c) => <Checkbox {...c} checked={draft.inputs.perPooling} onCheckedChange={(v) => setInput({ perPooling: v === true })} />}
          </Field>
        ) : null}
        {group === 'capital' ? (
          <Field inline label={t('tax.input.capitalScale')}>
            {(c) => <Checkbox {...c} checked={draft.inputs.capitalScale} onCheckedChange={(v) => setInput({ capitalScale: v === true })} />}
          </Field>
        ) : null}
        {group === 'rental' ? (
          <div className="space-y-1">
            <span className="block text-sm">{t('tax.profile.regime')}</span>
            <Segmented aria-label={t('tax.profile.regime')} value={draft.inputs.rentalRegime ?? 'auto'}
              onValueChange={(v) => setInput({ rentalRegime: v === 'auto' ? null : v })}
              options={REGIMES.map((r) => ({ value: r, label: regimeText(t, r) }))} />
          </div>
        ) : null}
        {rarer(decl).length + rarer(house).length ? rare(group, (
          <>
            {rarer(decl).length ? grid(group, rarer(decl)) : null}
            {rarer(house).length ? <div className={cols}>{rarer(house).map(householdField)}</div> : null}
          </>
        )) : null}
      </>
    );
  };

  const available = scenarios(estimate);
  return (
    <SectionCard title={t('tax.simulation.title')}>
      <div className="space-y-4">
        <div role="group" aria-label={t('tax.simulation.scenarios')} className="flex flex-wrap gap-2">
          {available.map((id) => (
            <Toggle key={id} variant="outline" size="sm" pressed={scenario === id} onPressedChange={() => choose(id)}>
              {t(`tax.simulation.scenario.${id}`)}
            </Toggle>
          ))}
        </div>
        <p className="text-sm text-muted-foreground">{t('tax.simulation.scenarios_hint')}</p>
        {simulate.error ? <ProblemMessage problem={present(toProblem(simulate.error))} /> : null}
        <form noValidate aria-label={t('tax.simulation.title')} onSubmit={submit} className="space-y-4">
          {(Object.keys(LABELS) as GroupId[]).map((group) => (
            <Folded key={group} label={t(LABELS[group])} hasError={folded.has(group)} open={open.includes(group)}
              onOpenChange={(o) => setOpen(o ? [...open, group] : open.filter((g) => g !== group))}>
              <fieldset className="space-y-3">
                <legend className="sr-only">{t(LABELS[group])}</legend>
                {body(group)}
              </fieldset>
            </Folded>
          ))}
          <div className="flex gap-2">
            <Button type="submit" disabled={simulate.isPending}>{t('tax.simulation.submit')}</Button>
            <Button type="button" variant="outline" onClick={reset}>{t('tax.simulation.reset')}</Button>
          </div>
        </form>
      </div>
    </SectionCard>
  );
}
