import { zodResolver } from '@hookform/resolvers/zod';
import { Link } from '@tanstack/react-router';
import type { TFunction } from 'i18next';
import { useId, useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { CartesianGrid, Line, LineChart, XAxis, YAxis } from 'recharts';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { formatLocalDate } from '@/components/LocalDate';
import { formatMoney, type MoneyValue } from '@/components/Money';
import { ModalDialog } from '@/components/ModalDialog';
import { Section } from '@/components/page/Section';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button, buttonVariants } from '@/components/ui/button';
import { ChartContainer, ChartTooltip, type ChartConfig } from '@/components/ui/chart';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group';
import { Table, TableBody, TableCell, TableFooter, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { toPlot } from '@/features/investments/chart/plot';
import { ISO_DATE, toWireAmount } from '@/features/subscriptions/edits/amount';
import { fieldErrorsOf } from '@/features/subscriptions/fieldErrors';
import { useSubmitErrors } from '@/features/subscriptions/edits/useSubmitErrors';
import { todayIso } from '@/features/subscriptions/today';
import { cn } from '@/lib/utils';
import { EARLY, type LoansOverview, type LoansSimulation, type SimulatedLoan } from '../api';
import { EFFECTS, money, percent, signedMoney } from '../format';
import { LOAN_AMOUNT } from '../new/loanForm';
import { monthText, useLoanNames } from '../overview/BalanceChart';
import { loanColor } from '../overview/colors';
import { presentRefusal } from '../refusal';
import { TableToggle } from '@/components/page/TableToggle';
import '@/locales/loans';
import '@/locales/forms';

const SPLITS = ['highest_rate', 'smallest_balance', 'chosen'] as const;
type Effect = (typeof EFFECTS)[number];
type Scope = 'household' | 'me';

const schema = (t: TFunction) => z.object({
  amount: z.string().trim().regex(LOAN_AMOUNT, t('loan_form.amount_format')),
  on: z.string().regex(ISO_DATE, t('edit.required')),
  split: z.enum(SPLITS),
  loanId: z.string(),
  effect: z.enum(EFFECTS),
}).refine((v) => v.split !== 'chosen' || v.loanId !== '', { path: ['loanId'], message: t('loans_early.loan_refused') });
type Values = z.infer<ReturnType<typeof schema>>;

const PATHS: Record<string, keyof Values> = { amount: 'amount', on: 'on', split: 'split', loanId: 'loanId', effect: 'effect' };
const isPenaltyPath = (path: string) => /^penalties\[\d+\]\./.test(path);
const text = (value?: MoneyValue | null) => (value ? formatMoney(value) : '—');

/**
 * Loans dashboard §5.3: « j’ai une somme, où la mettre ? ». The server splits the amount (D9), simulates each loan and answers
 * for the household; the penalties are typed per loan in the result and sent with the next run (D11). Nothing is recorded:
 * « Enregistrer ces remboursements » opens each loan with its repayment form prefilled.
 */
export function EarlyTab({ scope, overview }: { scope: Scope; overview: LoansOverview }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const form = useForm<Values>({
    resolver: zodResolver(schema(t)),
    defaultValues: { amount: '', on: todayIso(), split: 'highest_rate', loanId: '', effect: 'reduce_term' },
  });
  const errors = useSubmitErrors(form.setError, (path) => PATHS[path], (field) =>
    field.path === 'on' && field.code === 'realestate.invalid_loan' ? t('loans_early.on_refused')
      : field.path === 'loanId' && field.code === 'realestate.invalid_loan' ? t('loans_early.loan_refused') : undefined);
  const simulate = $api.useMutation('post', EARLY);
  const [result, setResult] = useState<{ view: LoansSimulation; asked: Values } | null>(null);
  // D11: the penalties typed in the result, by loan; « Relancer » sends those of the loans that took a part.
  const [penalties, setPenalties] = useState<Record<string, string>>({});
  const [penaltyErrors, setPenaltyErrors] = useState<Record<string, string>>({});
  const [stale, setStale] = useState(false);
  const sentRef = useRef<string[]>([]);
  const effectId = useId();
  const chosen = form.watch('split') === 'chosen';
  const reference = overview.loans.filter((l) => l.currency === overview.currency);

  const run = (values: Values, typed: Record<string, string>) => {
    const parts = result?.view.loans.filter((l) => l.amount !== '0.00') ?? [];
    const sent = Object.entries(typed).filter(([id, v]) => v.trim() !== '' && parts.some((l) => l.loanId === id));
    const wrong = Object.fromEntries(sent.filter(([, v]) => !LOAN_AMOUNT.test(v.trim()) && !/^0+([.,]0+)?$/.test(v.trim()))
      .map(([id]) => [id, t('loan_form.amount_format')]));
    setPenaltyErrors(wrong);
    if (Object.keys(wrong).length > 0) return;
    sentRef.current = sent.map(([id]) => id);
    errors.clear();
    simulate.mutate(
      {
        body: {
          scope, on: values.on, amount: toWireAmount(values.amount), split: values.split, effect: values.effect,
          ...(values.split === 'chosen' ? { loanId: values.loanId } : {}),
          ...(sent.length > 0 ? { penalties: sent.map(([loanId, v]) => ({ loanId, amount: toWireAmount(v) })) } : {}),
        },
      },
      {
        onSuccess: (view) => { setResult({ view, asked: values }); setStale(false); toast.success(t('loans_early.announced')); },
        onError: (error) => {
          errors.onError(error);
          // A penalty's refusal sits under its own field; the rest is placed by `useSubmitErrors`.
          const fields = fieldErrorsOf(error);
          const byLoan: Record<string, string> = {};
          for (const f of fields.filter((f) => isPenaltyPath(f.path))) {
            const id = sentRef.current[Number(/\[(\d+)\]/.exec(f.path)?.[1])];
            if (id) byLoan[id] = f.code === 'realestate.penalty_without_repayment' ? t('field_error.realestate_penalty_without_repayment') : f.message;
          }
          setPenaltyErrors(byLoan);
          const placed = (f: { path: string }) => PATHS[f.path] !== undefined
            || (isPenaltyPath(f.path) && sentRef.current[Number(/\[(\d+)\]/.exec(f.path)?.[1])] !== undefined);
          if (fields.length > 0 && fields.every(placed)) errors.clear();
        },
      },
    );
  };
  // A new question starts afresh: its penalties belong to another split.
  const submit = form.handleSubmit((values) => { setPenalties({}); setPenaltyErrors({}); setResult(null); run(values, {}); });
  const problem = errors.problem ? presentRefusal(t, simulate.error) : null;
  const e = form.formState.errors;

  return (
    <div className="grid items-start gap-8 lg:grid-cols-[22rem_1fr]">
      <Section title={t('loans_early.title')}>
        {problem ? <ProblemMessage problem={problem} /> : null}
        <form noValidate onSubmit={(ev) => void submit(ev)} className="space-y-4">
          <Field label={t('loans_early.amount')} hint={overview.currency} error={e.amount?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
          </Field>
          <Controller control={form.control} name="on" render={({ field }) => (
            <Field label={t('loans_early.on')} error={e.on?.message}>
              {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
            </Field>
          )} />
          <Controller control={form.control} name="split" render={({ field }) => (
            <fieldset className="space-y-2">
              <legend className="text-sm font-medium">{t('loans_early.split')}</legend>
              <RadioGroup value={field.value} onValueChange={field.onChange} className="gap-2">
                {SPLITS.map((s) => (
                  <Label key={s} htmlFor={`split-${s}`}
                    className={cn('flex cursor-pointer items-start gap-3 rounded-md border p-3 font-normal', field.value === s && 'border-primary bg-primary/5')}>
                    <RadioGroupItem id={`split-${s}`} value={s} className="mt-0.5" />
                    <span className="space-y-0.5">
                      <span className="block font-medium">{t(`loans_early.split_${s}`)}</span>
                      <span className="block text-sm text-muted-foreground">{t(`loans_early.split_${s}_help`)}</span>
                    </span>
                  </Label>
                ))}
              </RadioGroup>
            </fieldset>
          )} />
          {chosen ? (
            <Controller control={form.control} name="loanId" render={({ field }) => (
              <Field label={t('loans_early.loan')} error={e.loanId?.message}>
                {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} placeholder={t('loans_early.loan_placeholder')}
                  options={reference.map((l) => ({ value: l.id, label: l.name }))} />}
              </Field>
            )} />
          ) : null}
          <Controller control={form.control} name="effect" render={({ field }) => (
            <div className="space-y-2">
              <p id={effectId} className="text-sm font-medium">{t('loans_early.effect')}</p>
              <Segmented value={field.value} onValueChange={field.onChange} aria-labelledby={effectId}
                options={EFFECTS.map((x) => ({ value: x, label: t(`loan_repayments.effect_option.${x}`) }))} />
            </div>
          )} />
          <p className="text-sm text-muted-foreground">{t('loans_early.note')}</p>
          <Button type="submit" disabled={simulate.isPending}>{t('loans_early.submit')}</Button>
        </form>
      </Section>
      <div className="space-y-6">
        {result ? (
          <Result view={result.view} overview={overview} penalties={penalties} penaltyErrors={penaltyErrors} stale={stale}
            onPenalty={(id, v) => { setPenalties((p) => ({ ...p, [id]: v })); setStale(true); }}
            onRerun={() => run(result.asked, penalties)} pending={simulate.isPending} />
        ) : null}
      </div>
    </div>
  );
}

function Result({ view, overview, penalties, penaltyErrors, stale, onPenalty, onRerun, pending }: {
  view: LoansSimulation; overview: LoansOverview; penalties: Record<string, string>; penaltyErrors: Record<string, string>; stale: boolean;
  onPenalty: (loanId: string, value: string) => void; onRerun: () => void; pending: boolean;
}) {
  const { t } = useTranslation();
  const { nameOf, rankOf } = useLoanNames(overview);
  const [recording, setRecording] = useState(false);
  const m = (amount: string) => money(view.currency, amount);
  const date = (iso?: string | null) => (iso ? formatLocalDate(iso) : '—');
  const paymentChanged = view.monthlyPaymentBefore?.amount !== view.monthlyPaymentAfter?.amount;
  const freeChanged = view.freeOnBefore !== view.freeOnAfter;
  const parts = view.loans.filter((l) => l.amount !== '0.00');
  const writable = parts.filter((l) => l.canWrite);
  const asked = view.asked;

  const end = (l: SimulatedLoan) => {
    if (l.code === 'realestate.other_currency' || l.amount === '0.00') return date(l.endBefore);
    if (l.settled) return t('loans_early.settled', { month: l.endAfter ? monthText(l.endAfter.slice(0, 7)) : '—' });
    return l.endAfter === l.endBefore ? date(l.endAfter) : t('loans_early.end_instead', { after: date(l.endAfter), before: date(l.endBefore) });
  };

  return (
    <>
      <StatLine items={[
        { label: t('loans_early.net'),
          value: <>{signedMoney(view.currency, view.netSaving.amount)}<span className="block text-sm font-normal text-muted-foreground">{t('loans_early.net_sub', { interest: formatMoney(view.interestSaved), penalties: formatMoney(view.penalties) })}</span></> },
        { label: t('loans_early.monthly'),
          value: <>{text(view.monthlyPaymentAfter)}<span className="block text-sm font-normal text-muted-foreground">
            {paymentChanged ? t('loans_early.monthly_sub', { before: text(view.monthlyPaymentBefore), month: monthText(view.paymentMonth) }) : t('loans_early.unchanged_f')}</span></> },
        { label: t('loans_early.free'),
          value: <>{view.freeOnAfter ? monthText(view.freeOnAfter.slice(0, 7)) : '—'}<span className="block text-sm font-normal text-muted-foreground">
            {freeChanged && view.freeOnBefore ? t('loans_early.free_sub', { before: monthText(view.freeOnBefore.slice(0, 7)) }) : t('loans_early.unchanged_m')}</span></> },
      ]} />

      <Section title={t('loans_early.where', { amount: m(asked.amount) })}>
        <Table aria-label={t('loans_early.where', { amount: m(asked.amount) })}>
          <TableHeader>
            <TableRow>
              <TableHead>{t('loans_early.col_loan')}</TableHead>
              <TableHead className="text-right">{t('loans_early.col_rate')}</TableHead>
              <TableHead className="text-right">{t('loans_early.col_part')}</TableHead>
              <TableHead>{t('loans_early.col_end')}</TableHead>
              <TableHead className="text-right">{t('loans_early.col_saved')}</TableHead>
              <TableHead className="text-right">{t('loans_early.col_penalty')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {view.loans.map((l) => {
              const other = l.code === 'realestate.other_currency';
              const takes = l.amount !== '0.00';
              return (
                <TableRow key={l.loanId}>
                  <TableCell>
                    <span className="inline-flex items-center gap-2">
                      <span aria-hidden="true" className="size-2.5 shrink-0 rounded-full" style={{ background: loanColor(rankOf(l.loanId)) }} />
                      <Link to="/loans/$id" params={{ id: l.loanId }} className="font-medium hover:underline">{nameOf(l.loanId)}</Link>
                    </span>
                  </TableCell>
                  <TableCell className="text-right tabular-nums">{l.rate ? percent(t, l.rate) : '—'}</TableCell>
                  <TableCell className="text-right tabular-nums">{other ? t('loans_early.other_currency') : takes ? m(l.amount) : '—'}</TableCell>
                  <TableCell>{end(l)}</TableCell>
                  <TableCell className="text-right tabular-nums">{takes ? m(l.interestSaved) : '—'}</TableCell>
                  <TableCell className="text-right">
                    {takes ? (
                      <span className="inline-flex flex-col items-end gap-1">
                        <Input inputMode="decimal" className="h-8 w-28 text-right" aria-label={t('loans_early.penalty_label', { name: nameOf(l.loanId) })}
                          aria-invalid={penaltyErrors[l.loanId] ? true : undefined}
                          value={penalties[l.loanId] ?? (l.penalty === '0.00' ? '' : l.penalty)} onChange={(ev) => onPenalty(l.loanId, ev.target.value)} />
                        {penaltyErrors[l.loanId] ? <span className="text-xs text-destructive">{penaltyErrors[l.loanId]}</span> : null}
                      </span>
                    ) : '—'}
                  </TableCell>
                </TableRow>
              );
            })}
          </TableBody>
          <TableFooter>
            <TableRow>
              <TableHead scope="row">{t('loans_early.total')}</TableHead>
              <TableCell />
              <TableCell className="text-right tabular-nums">{m(asked.amount)}</TableCell>
              <TableCell />
              <TableCell className="text-right tabular-nums">{formatMoney(view.interestSaved)}</TableCell>
              <TableCell className="text-right tabular-nums">{formatMoney(view.penalties)}</TableCell>
            </TableRow>
          </TableFooter>
        </Table>
        {stale ? (
          <div className="flex flex-wrap items-center gap-3">
            <Button type="button" onClick={onRerun} disabled={pending}>{t('loans_early.rerun')}</Button>
            <p className="text-sm text-muted-foreground">{t('loans_early.rerun_help')}</p>
          </div>
        ) : null}
      </Section>

      <Section title={t('loans_early.curve')}><Curve view={view} /></Section>

      {writable.length > 0 ? (
        <Button type="button" variant="outline" onClick={() => setRecording(true)}>{t('loans_early.record')}</Button>
      ) : null}
      {recording ? (
        <ModalDialog title={t('loans_early.record')} onClose={() => setRecording(false)}>
          {() => (
            <div className="space-y-3">
              <p className="text-sm text-muted-foreground">{t('loans_early.record_help')}</p>
              <ul className="space-y-2">
                {writable.map((l) => (
                  <li key={l.loanId}>
                    <Link to="/loans/$id" params={{ id: l.loanId }} className="underline-offset-4 hover:underline"
                      state={{ loanRepayment: { onDate: asked.on, amount: l.amount, effect: asked.effect as Effect, penalty: l.penalty === '0.00' ? '' : l.penalty } }}>
                      {t('loans_early.record_line', { name: nameOf(l.loanId), amount: m(l.amount) })}
                    </Link>
                  </li>
                ))}
              </ul>
              <div className="flex justify-end">
                <button type="button" className={buttonVariants({ variant: 'outline' })} onClick={() => setRecording(false)}>{t('loans_early.close')}</button>
              </div>
            </div>
          )}
        </ModalDialog>
      ) : null}
    </>
  );
}

type Point = { i: number; month: string; before: number | null; after: number | null; beforeText: string; afterText: string };

/** dataviz.md §6: without (dashed, neutral) and with (primary), each named at its end; a table on demand. */
function Curve({ view }: { view: LoansSimulation }) {
  const { t } = useTranslation();
  const [asTable, setAsTable] = useState(false);
  const rows: Point[] = view.months.map((p, i) => ({
    i, month: p.month,
    before: p.before ? toPlot(p.before.amount) : null, after: p.after ? toPlot(p.after.amount) : null,
    beforeText: text(p.before), afterText: text(p.after),
  }));
  const config = {
    before: { label: t('loans_early.without'), color: 'var(--muted-foreground)' },
    after: { label: t('loans_early.with'), color: 'var(--primary)' },
  } satisfies ChartConfig;
  const axis = new Intl.NumberFormat('fr-FR', { style: 'currency', currency: view.currency, notation: 'compact' });
  const ticks = rows.filter((r) => r.month.endsWith('-01')).map((r) => r.i);
  const last = rows.length - 1;
  // Each line named a third of the way in, where they part most clearly: « Sans remboursement » above the higher one,
  // « Avec » under its own (both end on zero, where names would overlap).
  const at = Math.floor(last / 3);
  const endLabel = (key: 'before' | 'after') => (props: { x?: number | string; y?: number | string; index?: number }) =>
    props.index === at && typeof props.x === 'number' && typeof props.y === 'number'
      ? <text x={props.x} y={key === 'after' ? props.y + 16 : props.y - 8} textAnchor="middle"
          className={cn('text-xs', key === 'after' ? 'fill-primary font-medium' : 'fill-muted-foreground')}>{config[key].label}</text>
      : <g />;
  return (
    <div className="space-y-3">
      <div className="flex justify-end">
        <TableToggle asTable={asTable} onToggle={() => setAsTable((v) => !v)} />
      </div>
      {asTable ? (
        <Table aria-label={t('loans_early.curve_table')}>
          <TableHeader>
            <TableRow>
              <TableHead scope="col">{t('loans_early.month')}</TableHead>
              <TableHead scope="col" className="text-right">{t('loans_early.without')}</TableHead>
              <TableHead scope="col" className="text-right">{t('loans_early.with')}</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {rows.map((r) => (
              <TableRow key={r.month}>
                <TableHead scope="row" className="font-normal">{monthText(r.month)}</TableHead>
                <TableCell className="text-right tabular-nums">{r.beforeText}</TableCell>
                <TableCell className="text-right tabular-nums">{r.afterText}</TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : (
        <figure aria-label={t('loans_early.curve')}>
          <ChartContainer config={config} className="h-64 w-full">
            <LineChart data={rows} accessibilityLayer margin={{ top: 16, right: 8 }}>
              <CartesianGrid vertical={false} />
              <XAxis dataKey="i" type="number" domain={[0, Math.max(0, last)]} ticks={ticks.length > 0 ? ticks : [0]}
                tickFormatter={(i: number) => rows[i]?.month.slice(0, 4) ?? ''} tickLine={false} axisLine={false} tickMargin={8} />
              <YAxis width={64} tickLine={false} axisLine={false} tickFormatter={(n: number) => axis.format(n)} />
              <ChartTooltip content={<CurveTooltip />} />
              <Line dataKey="before" stroke="var(--color-before)" strokeWidth={2} strokeDasharray="5 4" dot={false} animationDuration={800}
                label={endLabel('before')} />
              <Line dataKey="after" stroke="var(--color-after)" strokeWidth={2} dot={false} animationDuration={800} label={endLabel('after')} />
            </LineChart>
          </ChartContainer>
        </figure>
      )}
    </div>
  );
}

function CurveTooltip({ active, payload }: { active?: boolean; payload?: ReadonlyArray<{ payload?: Point }> }) {
  const { t } = useTranslation();
  const row = payload?.[0]?.payload;
  if (!active || !row) return null;
  return (
    <div className="grid gap-1 rounded-lg border bg-popover/95 px-3 py-2 text-xs shadow-raised backdrop-blur-sm">
      <div className="font-medium">{monthText(row.month)}</div>
      <div className="tabular-nums">{t('loans_early.without')} : {row.beforeText}</div>
      <div className="tabular-nums">{t('loans_early.with')} : {row.afterText}</div>
    </div>
  );
}
