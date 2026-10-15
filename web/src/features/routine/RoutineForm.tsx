import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState, type KeyboardEvent } from 'react';
import { useForm, useWatch } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { messageFor } from '../subscriptions/fieldErrors';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney } from '@/components/Money';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { invalidateInvestments } from '../investments/api';
import { AMOUNT, toWireAmount } from '../subscriptions/edits/amount';
import { ROUTINE, type RoutineLine, type RoutineRecap, type RoutineView } from './api';
import { clearDraft, saveDraft } from './draft';

export type RoutineValues = { lines: { id: string; value: string; contribution: string; skip: boolean }[] };

const POSITIVE = /^(?!0+(?:[.,]0+)?$)\d+([.,]\d{1,4})?$/;
const input = 'w-32 rounded-md border px-3 py-2 text-right tabular-nums';
const filled = (l: RoutineValues['lines'][number]) => !l.skip && (l.value.trim() !== '' || l.contribution.trim() !== '');

/** The values a line starts with: the draft's if any, else the last value for a due envelope, empty otherwise. */
export function startValues(view: RoutineView, draft: RoutineValues | null): RoutineValues {
  return {
    lines: view.lines.map((l) => draft?.lines.find((d) => d.id === l.id)
      ?? { id: l.id, value: l.due && l.lastValue ? l.lastValue.amount : '', contribution: '', skip: false }),
  };
}

/** screens.md §5: one form, keyboard first (Enter moves on, never submits), one save at the end. */
export function RoutineForm({ view, month, initial, onSaved }: {
  view: RoutineView; month: string; initial: RoutineValues; onSaved: (recap: RoutineRecap, values: RoutineValues) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<RoutineValues>({ defaultValues: initial });
  const values = useWatch({ control: form.control });
  const [problem, setProblem] = useState<ReturnType<typeof present> | null>(null);
  const save = $api.useMutation('post', ROUTINE, { onSuccess: () => {
    void invalidateInvestments(queryClient);
    // The list of the month: what was just recorded is no longer due.
    void queryClient.invalidateQueries({ queryKey: ['get', ROUTINE] });
  } });

  // The draft exists only once the form differs from what the server proposes; a pristine form must not pin stale values.
  const fresh = useMemo(() => JSON.stringify(startValues(view, null)), [view]);
  useEffect(() => {
    const now = form.getValues();
    if (JSON.stringify(now) === fresh) clearDraft(view.on); else saveDraft(view.on, now);
  }, [values, view.on, form, fresh]);

  const indexed = useMemo(() => view.lines.map((line, index) => ({ line, index })), [view.lines]);
  const due = indexed.filter((x) => x.line.due);
  const others = indexed.filter((x) => !x.line.due);
  const count = (values.lines ?? []).filter((l) => l && filled(l as RoutineValues['lines'][number])).length;

  const onKeyDown = (event: KeyboardEvent<HTMLFormElement>) => {
    if (event.key !== 'Enter' || !(event.target instanceof HTMLInputElement)) return;
    event.preventDefault();
    const fields = Array.from(event.currentTarget.querySelectorAll<HTMLInputElement>('input')).filter((el) => el.closest('details:not([open])') === null);
    fields[fields.indexOf(event.target) + 1]?.focus();
  };

  const submit = form.handleSubmit((v) => {
    setProblem(null);
    let invalid = false;
    v.lines.forEach((l, i) => {
      if (!filled(l)) return;
      if (l.value.trim() !== '' && !AMOUNT.test(l.value.trim())) { form.setError(`lines.${i}.value`, { message: t('routine.amount_format') }); invalid = true; }
      if (l.contribution.trim() !== '' && !POSITIVE.test(l.contribution.trim())) {
        form.setError(`lines.${i}.contribution`, { message: AMOUNT.test(l.contribution.trim()) ? t('routine.positive') : t('routine.amount_format') });
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
            form.setError(`lines.${at.i}.${m?.[2] === 'contribution' ? 'contribution' : 'value'}`, { message: messageFor(t, e) });
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
      <li key={line.id} className="grid gap-2 py-3 md:grid-cols-[1fr_auto_auto_auto] md:items-end">
        <div>
          <p className="font-medium">{line.name}</p>
          <p className="text-sm text-muted-foreground">
            {line.lastValue && line.lastAsOf ? t('routine.last', { amount: formatMoney(line.lastValue), date: formatLocalDate(line.lastAsOf) }) : t('routine.no_last')}
          </p>
        </div>
        <div className="grid gap-1 text-sm">
          <label className="grid gap-1">
            <span>{t('routine.value', { name: line.name })}</span>
            <input inputMode="decimal" aria-invalid={e?.value ? 'true' : undefined} aria-describedby={e?.value ? `${line.id}-value` : undefined} {...form.register(`lines.${index}.value`)} className={input} />
          </label>
          {e?.value ? <span id={`${line.id}-value`} className="text-destructive">{e.value.message}</span> : null}
        </div>
        <div className="grid gap-1 text-sm">
          <label className="grid gap-1">
            <span>{t('routine.contribution', { name: line.name })}</span>
            <input inputMode="decimal" aria-invalid={e?.contribution ? 'true' : undefined} aria-describedby={e?.contribution ? `${line.id}-contribution` : undefined} {...form.register(`lines.${index}.contribution`)} className={input} />
          </label>
          {e?.contribution ? <span id={`${line.id}-contribution`} className="text-destructive">{e.contribution.message}</span> : null}
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" {...form.register(`lines.${index}.skip`)} />
          <span>{t('routine.skip', { name: line.name })}</span>
        </label>
      </li>
    );
  };

  return (
    <form noValidate onKeyDown={onKeyDown} onSubmit={(ev) => void submit(ev)} className="space-y-4">
      {problem ? <ProblemMessage problem={problem} /> : null}
      <section aria-label={t('routine.due')} className="space-y-1">
        <h2 className="text-lg font-semibold">{t('routine.due')}</h2>
        {due.length === 0 ? <p className="text-muted-foreground">{t('routine.all_done', { month })}</p> : <ul className="divide-y">{due.map(row)}</ul>}
      </section>
      {others.length > 0 ? (
        <details open={due.length === 0} className="rounded-md border p-3">
          <summary className="cursor-pointer font-medium">{t('routine.others')}</summary>
          <ul className="divide-y">{others.map(row)}</ul>
        </details>
      ) : null}
      <Button type="submit" disabled={count === 0 || save.isPending}>
        {t('routine.save')}
      </Button>
    </form>
  );
}
