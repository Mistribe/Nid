import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState, type KeyboardEvent } from 'react';
import { Controller, useForm, useWatch } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { messageFor } from '@/components/form/fieldErrors';
import { directionOf, AMOUNT, toFieldAmount, toWireAmount } from '@/lib/amount';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ChevronDown } from 'lucide-react';
import { Field } from '@/components/form/Field';
import { FormActions } from '@/components/page/FormPage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { Input } from '@/components/ui/input';
import { invalidateInvestments } from '../investments/api';
import { ROUTINE, type RoutineLine, type RoutineRecap, type RoutineView } from './api';
import { clearDraft, saveDraft } from './draft';
import { dayMonth } from '../settings/banks/api';
import '@/locales/banks';

export type RoutineValues = { lines: { id: string; value: string; contribution: string; skip: boolean }[] };
/** The envelopes whose bank account writes their values, each with the day of its last balance. */
export type Banked = Record<string, string>;

const POSITIVE = /^(?!0+(?:[.,]0+)?$)\d+([.,]\d{1,4})?$/;
const input = 'text-right tabular-nums';
/** The fields Enter moves between: Radix adds a hidden input beside each checkbox, which is not a stop. */
const STOPS = 'input:not([aria-hidden="true"]), button[role="checkbox"]';
const filled = (l: RoutineValues['lines'][number]) => !l.skip && (l.value.trim() !== '' || l.contribution.trim() !== '');

/**
 * The values a line starts with: the draft's if any, else the last value for a due envelope, empty otherwise. A banked
 * envelope's value stays empty whatever the draft: its bank writes it.
 */
export function startValues(view: RoutineView, draft: RoutineValues | null, banked: Banked = {}): RoutineValues {
  return {
    lines: view.lines.map((l) => {
      const line = draft?.lines.find((d) => d.id === l.id) ?? { id: l.id, value: l.due && l.lastValue ? toFieldAmount(l.lastValue.amount) : '', contribution: '', skip: false };
      return l.id in banked ? { ...line, value: '' } : line;
    }),
  };
}

/** screens.md §5: one form, keyboard first (Enter moves on, never submits), one save at the end. */
export function RoutineForm({ view, month, initial, banked, onSaved }: {
  view: RoutineView; month: string; initial: RoutineValues; banked: Banked; onSaved: (recap: RoutineRecap, values: RoutineValues) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<RoutineValues>({ defaultValues: initial });
  const values = useWatch({ control: form.control });
  const [problem, setProblem] = useState<ReturnType<typeof present> | null>(null);
  const [othersOpen, setOthersOpen] = useState<boolean | null>(null);
  const [nothing, setNothing] = useState(false);
  const save = $api.useMutation('post', ROUTINE, { onSuccess: () => {
    void invalidateInvestments(queryClient);
    // The list of the month: what was just recorded is no longer due.
    void queryClient.invalidateQueries({ queryKey: ['get', ROUTINE] });
  } });

  // The draft exists only once the form differs from what the server proposes; a pristine form must not pin stale values.
  const fresh = useMemo(() => JSON.stringify(startValues(view, null, banked)), [view, banked]);
  useEffect(() => {
    const now = form.getValues();
    if (JSON.stringify(now) === fresh) clearDraft(view.on); else saveDraft(view.on, now);
  }, [values, view.on, form, fresh]);

  const indexed = useMemo(() => view.lines.map((line, index) => ({ line, index })), [view.lines]);
  const due = indexed.filter((x) => x.line.due);
  const others = indexed.filter((x) => !x.line.due);
  // An error on a folded row opens the fold: otherwise the save would seem to do nothing.
  const place = (index: number, field: 'value' | 'contribution', message: string) => {
    form.setError(`lines.${index}.${field}`, { message });
    if (others.some((x) => x.index === index)) setOthersOpen(true);
  };
  const count = (values.lines ?? []).filter((l) => l && filled(l as RoutineValues['lines'][number])).length;

  const onKeyDown = (event: KeyboardEvent<HTMLFormElement>) => {
    if (event.key !== 'Enter' || !(event.target instanceof HTMLElement) || !event.target.matches(STOPS)) return;
    event.preventDefault();
    const fields = Array.from(event.currentTarget.querySelectorAll<HTMLElement>(STOPS));
    fields[fields.indexOf(event.target) + 1]?.focus();
  };

  const submit = form.handleSubmit((v) => {
    setProblem(null);
    if (!v.lines.some(filled)) { setNothing(true); return; }
    let invalid = false;
    v.lines.forEach((l, i) => {
      if (!filled(l)) return;
      if (l.value.trim() !== '' && !AMOUNT.test(l.value.trim())) { place(i, 'value', t('routine.amount_format')); invalid = true; }
      if (l.contribution.trim() !== '' && !POSITIVE.test(l.contribution.trim())) {
        place(i, 'contribution', AMOUNT.test(l.contribution.trim()) ? t('routine.positive') : t('routine.amount_format'));
        invalid = true;
      }
    });
    if (invalid) return;
    const sent = v.lines.map((l, i) => ({ l, i })).filter((x) => filled(x.l));
    const money = (text: string, currency: string) => (text.trim() === '' ? null : { amount: toWireAmount(text), currency });
    save.mutate(
      { body: { on: view.on, lines: sent.map(({ l, i }) => ({
        envelopeId: l.id,
        value: money(l.value, view.lines[i]!.currency),
        contribution: money(l.contribution, view.lines[i]!.currency),
      })) } },
      {
        onSuccess: (recap) => onSaved(recap, v),
        onError: (error) => {
          const p = toProblem(error);
          const errors = p.errors ?? [];
          const placed = errors.filter((e) => {
            const m = /^lines\[(\d+)\](?:\.(value|contribution))?$/.exec(e.path);
            const at = m ? sent[Number(m[1])] : undefined;
            if (!at) return false;
            place(at.i, m?.[2] === 'contribution' ? 'contribution' : 'value', messageFor(t, e));
            return true;
          });
          if (placed.length < errors.length || errors.length === 0) setProblem(present(p));
        },
      },
    );
  });

  const row = ({ line, index }: { line: RoutineLine; index: number }) => {
    const e = form.formState.errors.lines?.[index];
    return (
      // One grid for every row (issue 346): the name in its column, the two amounts aligned from row to row, « Passer » last.
      // The labels are short, the row names the envelope; each control's accessible name still carries it.
      <li key={line.id} className="flex flex-col gap-3 p-4 md:grid md:grid-cols-[minmax(0,1fr)_9rem_9rem_6rem] md:items-end md:gap-x-4">
          <div className="min-w-0">
            <p className="font-medium break-words">{line.name}</p>
            <p className="text-sm text-muted-foreground">
              {line.lastValue && line.lastAsOf ? t('routine.last', { amount: formatMoney(line.lastValue), date: formatLocalDate(line.lastAsOf) }) : t('routine.no_last')}
            </p>
            {line.ceilingRoom ? (
              <p className="text-sm text-muted-foreground">
                {directionOf(line.ceilingRoom.amount) === 'flat' ? t('routine.ceiling_reached') : t('routine.ceiling_room', { amount: formatMoney(line.ceilingRoom) })}
              </p>
            ) : null}
          </div>
          {banked[line.id] ? (
            <p className="text-sm text-muted-foreground md:pb-2">{t('banks.routine_balance', { date: dayMonth(banked[line.id]!) })}</p>
          ) : (
            <Field label={t('routine.value_short')} error={e?.value?.message}>
              {(c) => <Input inputMode="decimal" {...c} aria-label={t('routine.value', { name: line.name })} {...form.register(`lines.${index}.value`)} className={input} />}
            </Field>
          )}
          <Field label={t('routine.contribution_short')} error={e?.contribution?.message}>
            {(c) => <Input inputMode="decimal" {...c} aria-label={t('routine.contribution', { name: line.name })} {...form.register(`lines.${index}.contribution`)} className={input} />}
          </Field>
          <Controller control={form.control} name={`lines.${index}.skip`} render={({ field }) => (
            <Field inline label={t('routine.skip_short')}>
              {(c) => <Checkbox {...c} aria-label={t('routine.skip', { name: line.name })} checked={field.value} onCheckedChange={(v) => field.onChange(v === true)} />}
            </Field>
          )} />
      </li>
    );
  };

  return (
    <form noValidate onKeyDown={onKeyDown} onSubmit={(ev) => void submit(ev)} className="space-y-4">
      {problem ? <ProblemMessage problem={problem} /> : null}
      <section aria-label={t('routine.due')} data-guide="routine.due" className="space-y-3">
        <h2 className="text-title">{t('routine.due')}</h2>
        {due.length === 0 ? <p className="text-muted-foreground">{t('routine.all_done', { month })}</p> : <ul className="divide-y rounded-lg border">{due.map(row)}</ul>}
      </section>
      {others.length > 0 ? (
        <Collapsible open={othersOpen ?? due.length === 0} onOpenChange={setOthersOpen} className="space-y-3">
          <CollapsibleTrigger className="flex items-center gap-1 font-medium">
            <ChevronDown aria-hidden="true" className="size-4" />{t('routine.others')}
          </CollapsibleTrigger>
          <CollapsibleContent><ul className="divide-y rounded-lg border">{others.map(row)}</ul></CollapsibleContent>
        </Collapsible>
      ) : null}
      {/* At the foot of the form, never in a fixed bar (design-system.md §8.5); with nothing filled the click says so (§6). */}
      <FormActions>
        {nothing && count === 0 ? <p role="status" className="mr-auto text-sm text-muted-foreground">{t('routine.nothing')}</p> : null}
        <Button type="submit" disabled={save.isPending} data-guide="routine.save">
          {t('routine.save')}
        </Button>
      </FormActions>
    </form>
  );
}
