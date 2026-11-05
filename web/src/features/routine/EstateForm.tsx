import { useQueryClient } from '@tanstack/react-query';
import { ChevronDown, CircleCheck } from 'lucide-react';
import { useEffect, useId, useMemo, useState, type KeyboardEvent, type ReactNode } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { DateField } from '@/components/form/DateField';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Input } from '@/components/ui/input';
import { LOAN_AMOUNT } from '../loans/new/loanForm';
import { invalidateProperties } from '../properties/api';
import { categoryText } from '../properties/format';
import { ISO_DATE, toWireAmount } from '../subscriptions/edits/amount';
import { Field } from '@/components/form/Field';
import { FormActions } from '@/components/page/FormPage';
import { messageFor } from '../subscriptions/fieldErrors';
import { todayIso } from '../subscriptions/today';
import { ESTATE_ROUTINE, type EstateEstimate, type EstateRecap, type EstateRoutineView } from './api';
import { estateDraft } from './draft';
import '@/locales/loans';
import '@/locales/routine';

type Ticked = { key: string; checked: boolean; amount: string; date: string };
type Group = 'costs' | 'rents';
export type EstateValues = { estimates: { property: string; amount: string }[]; costs: Ticked[]; rents: Ticked[] };

const input = 'text-right tabular-nums md:w-36';
/** The fields Enter moves between: Radix adds a hidden input beside each checkbox, which is not a stop. */
const STOPS = 'input:not([aria-hidden="true"]), button[role="checkbox"]';

/** Spec 3E2 §4: estimates empty; costs and rents unticked, with the server's amount and date. */
export function freshEstate(view: EstateRoutineView): EstateValues {
  return {
    estimates: view.estimates.map((e) => ({ property: e.property, amount: '' })),
    costs: view.costs.map((c) => ({ key: c.costId, checked: false, amount: c.amount, date: c.dueOn })),
    rents: view.rents.map((r) => ({ key: `${r.tenancyId}:${r.category}`, checked: false, amount: r.amount, date: r.occurredOn })),
  };
}

/** Each line from the draft when it holds that line (by property, cost or tenancy), else as the server proposes it now. */
export function startEstate(view: EstateRoutineView, draft: EstateValues | null): EstateValues {
  const fresh = freshEstate(view);
  return {
    estimates: fresh.estimates.map((l) => draft?.estimates.find((d) => d.property === l.property) ?? l),
    costs: fresh.costs.map((l) => draft?.costs.find((d) => d.key === l.key) ?? l),
    rents: fresh.rents.map((l) => draft?.rents.find((d) => d.key === l.key) ?? l),
  };
}

const same = (a: object, b: object | undefined) => JSON.stringify(a) === JSON.stringify(b);

/** Only the lines the person changed go to the draft: an untouched line always takes what the server proposes now. */
function changedLines(values: EstateValues, fresh: EstateValues): EstateValues {
  return {
    estimates: values.estimates.filter((l, i) => !same(l, fresh.estimates[i])),
    costs: values.costs.filter((l, i) => !same(l, fresh.costs[i])),
    rents: values.rents.filter((l, i) => !same(l, fresh.rents[i])),
  };
}

/** Spec 3E2 §4: the « Biens » form — keyboard first (Enter moves on, never submits), its own save at the end. */
export function EstateForm({ view, initial, onSaved }: {
  view: EstateRoutineView; initial: EstateValues; onSaved: (recap: EstateRecap, values: EstateValues) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const id = useId();
  const form = useForm<EstateValues>({ defaultValues: initial });
  const values = useWatch({ control: form.control });
  const [problem, setProblem] = useState<ReturnType<typeof present> | null>(null);
  const [othersOpen, setOthersOpen] = useState<boolean | null>(null);
  const [nothing, setNothing] = useState(false);
  const save = $api.useMutation('post', ESTATE_ROUTINE, { onSuccess: () => {
    void invalidateProperties(queryClient);
    // The month's lines: what was just recorded is now « already entered », its estimate no longer due.
    void queryClient.invalidateQueries({ queryKey: ['get', ESTATE_ROUTINE] });
  } });

  const fresh = useMemo(() => freshEstate(view), [view]);
  useEffect(() => {
    const changed = changedLines(form.getValues(), fresh);
    if ([changed.estimates, changed.costs, changed.rents].every((lines) => lines.length === 0)) estateDraft.clear(view.on);
    else estateDraft.save(view.on, changed);
  }, [values, view.on, form, fresh]);

  const indexed = view.estimates.map((line, index) => ({ line, index }));
  const due = indexed.filter((x) => x.line.due);
  const others = indexed.filter((x) => !x.line.due);
  // An error on a folded estimate opens the fold: otherwise the save would seem to do nothing.
  const estimateError = (index: number, message: string) => {
    form.setError(`estimates.${index}.amount`, { message });
    if (others.some((x) => x.index === index)) setOthersOpen(true);
  };
  const ready = (values.estimates ?? []).some((l) => (l?.amount ?? '').trim() !== '')
    || [...(values.costs ?? []), ...(values.rents ?? [])].some((l) => l?.checked === true);
  const inMonth = (date: string) => ISO_DATE.test(date) && date.slice(0, 7) === view.on.slice(0, 7) && date <= todayIso();

  const onKeyDown = (event: KeyboardEvent<HTMLFormElement>) => {
    if (event.key !== 'Enter' || !(event.target instanceof HTMLElement) || !event.target.matches(STOPS)) return;
    event.preventDefault();
    const fields = Array.from(event.currentTarget.querySelectorAll<HTMLElement>(STOPS));
    fields[fields.indexOf(event.target) + 1]?.focus();
  };

  const submit = form.handleSubmit((v) => {
    if (!ready) { setNothing(true); return; }
    setProblem(null);
    let invalid = false;
    v.estimates.forEach((l, i) => {
      if (l.amount.trim() !== '' && !LOAN_AMOUNT.test(l.amount.trim())) { estimateError(i, t('loan_form.amount_format')); invalid = true; }
    });
    for (const group of ['costs', 'rents'] as const)
      v[group].forEach((l, i) => {
        if (!l.checked) return;
        if (!LOAN_AMOUNT.test(l.amount.trim())) { form.setError(`${group}.${i}.amount`, { message: t('loan_form.amount_format') }); invalid = true; }
        if (!inMonth(l.date)) { form.setError(`${group}.${i}.date`, { message: t('routine_estate.date_range') }); invalid = true; }
      });
    if (invalid) return;

    // What is sent, and the form line each sent one comes from: a refusal lands under its own line.
    const estimates = v.estimates.flatMap((l, i) => (l.amount.trim() === ''
      ? [] : [{ i, body: { property: view.estimates[i]!.property, amount: toWireAmount(l.amount) } }]));
    const flows = [
      ...v.costs.flatMap((l, i) => (l.checked ? [{ group: 'costs' as const, i, body: {
        property: view.costs[i]!.property, kind: 'expense', category: view.costs[i]!.category, occurredOn: l.date, amount: toWireAmount(l.amount),
      } }] : [])),
      ...v.rents.flatMap((l, i) => (l.checked ? [{ group: 'rents' as const, i, body: {
        property: view.rents[i]!.property, kind: 'income', category: view.rents[i]!.category, occurredOn: l.date, amount: toWireAmount(l.amount),
      } }] : [])),
    ];
    save.mutate(
      { body: { on: view.on, estimates: estimates.map((e) => e.body), flows: flows.map((f) => f.body) } },
      {
        onSuccess: (recap) => onSaved(recap, v),
        onError: (error) => {
          const p = toProblem(error);
          const errors = p.errors ?? [];
          const placed = errors.filter((e) => {
            const m = /^(estimates|flows)\[(\d+)\](?:\.(\w+))?$/.exec(e.path);
            const estimate = m?.[1] === 'estimates' ? estimates[Number(m[2])] : undefined;
            const flow = m?.[1] === 'flows' ? flows[Number(m[2])] : undefined;
            if (estimate) estimateError(estimate.i, messageFor(t, e));
            else if (flow) form.setError(`${flow.group}.${flow.i}.${m?.[3] === 'occurredOn' ? 'date' : 'amount'}`, { message: messageFor(t, e) });
            return estimate !== undefined || flow !== undefined;
          });
          if (placed.length < errors.length || errors.length === 0) setProblem(present(p));
        },
      },
    );
  });

  const e = form.formState.errors;

  const estimateRow = ({ line, index }: { line: EstateEstimate; index: number }) => (
    <li key={line.property} className="p-4">
      <Field label={t('routine_estate.estimate', { name: line.name })} error={e.estimates?.[index]?.amount?.message}
        hint={line.lastValue && line.lastAsOf
          ? t('routine_estate.last', { value: formatMoney({ amount: line.lastValue, currency: line.currency }), date: formatLocalDate(line.lastAsOf) })
          : t('routine_estate.no_last')}>
        {(c) => <Input inputMode="decimal" {...c} {...form.register(`estimates.${index}.amount`)} className={input} />}
      </Field>
    </li>
  );

  const tickedRow = (group: Group, index: number, key: string, text: string, currency: string, already: string | null | undefined) => {
    const le = e[group]?.[index];
    return (
      <li key={key} className="flex flex-col gap-3 p-4 md:grid md:grid-cols-[1fr_auto_auto] md:items-start">
        <div className="space-y-1">
          <Controller control={form.control} name={`${group}.${index}.checked`} render={({ field }) => (
            <Field inline label={text}>{(c) => <Checkbox {...c} checked={field.value} onCheckedChange={(v) => field.onChange(v === true)} />}</Field>
          )} />
          {already ? (
            <p className="flex items-center gap-1 text-sm text-muted-foreground">
              <CircleCheck aria-hidden="true" className="size-4" />
              <span>{t('routine_estate.already', { amount: formatMoney({ amount: already, currency }) })}</span>
            </p>
          ) : null}
        </div>
        <Field label={t('routine_estate.amount_of', { line: text })} error={le?.amount?.message}>
          {(c) => <Input inputMode="decimal" {...c} {...form.register(`${group}.${index}.amount`)} className={input} />}
        </Field>
        <Controller control={form.control} name={`${group}.${index}.date`} render={({ field }) => (
          <Field label={t('routine_estate.date_of', { line: text })} error={le?.date?.message}>
            {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
      </li>
    );
  };

  const groupOf = (name: Group, title: string, rows: ReactNode[]) => (rows.length === 0 ? null : (
    <section aria-labelledby={`${id}-${name}`} className="space-y-1">
      <div className="flex items-center justify-between gap-2">
        <h3 id={`${id}-${name}`} className="font-semibold">{title}</h3>
        <Button type="button" variant="outline" size="sm"
          onClick={() => form.getValues(name).forEach((_, i) => form.setValue(`${name}.${i}.checked`, true, { shouldDirty: true }))}>
          {t('routine_estate.check_all')}
        </Button>
      </div>
      <ul className="divide-y rounded-lg border">{rows}</ul>
    </section>
  ));

  return (
    <form noValidate onKeyDown={onKeyDown} onSubmit={(ev) => void submit(ev)} className="space-y-4">
      {problem ? <ProblemMessage problem={problem} /> : null}
      <section aria-labelledby={`${id}-estimates`} className="space-y-1">
        <h3 id={`${id}-estimates`} className="font-semibold">{t('routine_estate.estimates')}</h3>
        {due.length > 0 ? <ul className="divide-y rounded-lg border">{due.map(estimateRow)}</ul> : null}
        {others.length > 0 ? (
          <Collapsible open={othersOpen ?? due.length === 0} onOpenChange={setOthersOpen} className="space-y-3">
            <CollapsibleTrigger className="flex items-center gap-1 font-medium">
              <ChevronDown aria-hidden="true" className="size-4" />{t('routine_estate.others')}
            </CollapsibleTrigger>
            <CollapsibleContent><ul className="divide-y rounded-lg border">{others.map(estimateRow)}</ul></CollapsibleContent>
          </Collapsible>
        ) : null}
      </section>
      {groupOf('costs', t('routine_estate.costs'), view.costs.map((c, i) => tickedRow('costs', i, c.costId,
        t('routine_estate.cost_line', { category: categoryText(t, c.category), name: c.name }), c.currency, c.alreadyEntered)))}
      {groupOf('rents', t('routine_estate.rents'), view.rents.map((r, i) => tickedRow('rents', i, `${r.tenancyId}:${r.category}`,
        t(r.category === 'recoverable_charges' ? 'routine_estate.charges_line' : 'routine_estate.rent_line', { label: r.label, name: r.name }), r.currency, r.alreadyEntered)))}
      {/* At the foot of the form, never in a fixed bar (design-system.md §8.5); with nothing entered the click says so (§6). */}
      <FormActions>
        {nothing && !ready ? <p role="status" className="mr-auto text-sm text-muted-foreground">{t('routine_estate.nothing')}</p> : null}
        <Button type="submit" disabled={save.isPending}>{t('routine_estate.save')}</Button>
      </FormActions>
    </form>
  );
}
