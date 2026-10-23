import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Controller, useForm, type Path } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { FormDialog } from '@/components/FormDialog';
import { formatLocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Input } from '@/components/ui/input';
import { AMOUNT, ISO_DATE, toWireAmount } from '../../subscriptions/edits/amount';
import { messageFor } from '../../subscriptions/fieldErrors';
import { todayIso } from '../../subscriptions/today';
import { invalidateInvestments, SNAPSHOTS, type EnvelopeDetail, type Holding, type Snapshot } from '../api';
import { hasUnits, trimUnits, UNITS } from './holdingFormat';
import '@/locales/investments';

type Line = { units: string; unitPrice: string; value: string };
type Values = { asOf: string; sourceLabel: string; recordValuation: boolean; lines: Line[] };

/** Each open holding starts from its units in the latest snapshot (as the routine starts from the last value); prices and values are new. */
export function startLines(holdings: Holding[], previous: Snapshot | undefined): Line[] {
  return holdings.map((h) => {
    const units = previous?.lines.find((l) => l.holdingId === h.id)?.units;
    return { units: units && hasUnits(h.kind) ? trimUnits(units) : '', unitPrice: '', value: '' };
  });
}

/**
 * Issue #34 §7: a snapshot as printed on a statement, one row per open holding. A row left without a value is not
 * sent, and the snapshot is then partial; « Enregistrer aussi la valeur de l'enveloppe » records the lines' sum as its value (D7).
 */
export function SnapshotDialog({ envelope, holdings, onClose, onDone }: {
  envelope: EnvelopeDetail; holdings: Holding[]; onClose: () => void; onDone: (message: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const snapshots = $api.useQuery('get', SNAPSHOTS, { params: { path: { id: envelope.id } } });
  return (
    <FormDialog title={t('holdings.snapshot.title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (snapshots.error ? <ProblemMessage problem={present(toProblem(snapshots.error))} />
        : !snapshots.data ? <div role="status" aria-busy="true" aria-label={t('holdings.loading')} className="h-20 shimmer rounded-md bg-muted" />
          : <SnapshotForm envelope={envelope} holdings={holdings} previous={snapshots.data.items[0]} titleId={titleId} done={done} />)}
    </FormDialog>
  );
}

function SnapshotForm({ envelope, holdings, previous, titleId, done }: {
  envelope: EnvelopeDetail; holdings: Holding[]; previous: Snapshot | undefined; titleId: string; done: (message?: string) => void;
}) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<Values>({ defaultValues: { asOf: todayIso(), sourceLabel: '', recordValuation: false, lines: startLines(holdings, previous) } });
  const [problem, setProblem] = useState<Presentation | null>(null);
  const save = $api.useMutation('post', SNAPSHOTS, { onSuccess: () => { void invalidateInvestments(queryClient); } });
  const e = form.formState.errors;

  const submit = form.handleSubmit((v) => {
    setProblem(null);
    let invalid = false;
    const fail = (name: Path<Values>, message: string) => { form.setError(name, { message }); invalid = true; };
    if (!ISO_DATE.test(v.asOf)) fail('asOf', t('edit.required'));
    else if (v.asOf > todayIso()) fail('asOf', t('field_error.core_date_in_future'));
    if (v.sourceLabel.length > 200) fail('sourceLabel', t('holdings.form.label_too_long'));
    v.lines.forEach((l, i) => {
      const value = l.value.trim();
      if (value === '' && (l.unitPrice.trim() !== '')) fail(`lines.${i}.value`, t('holdings.snapshot.value_required'));
      if (value !== '' && !AMOUNT.test(value)) fail(`lines.${i}.value`, t('investments_form.amount_format'));
      for (const field of ['units', 'unitPrice'] as const)
        if (l[field].trim() !== '' && !UNITS.test(l[field].trim())) fail(`lines.${i}.${field}`, t('holdings.form.units_format'));
    });
    const sent = v.lines.map((l, i) => ({ l, i })).filter((x) => x.l.value.trim() !== '');
    if (!invalid && sent.length === 0) { setProblem({ kind: 'validation', detail: t('holdings.snapshot.empty') }); return; }
    if (invalid) return;
    const text = (s: string) => (s.trim() === '' ? null : toWireAmount(s));
    save.mutate({
      params: { path: { id: envelope.id } },
      body: {
        asOf: v.asOf, sourceLabel: v.sourceLabel.trim() || null, complete: sent.length === holdings.length, recordValuation: v.recordValuation,
        lines: sent.map(({ l, i }) => ({
          holdingId: holdings[i]!.id, units: text(l.units), unitPrice: text(l.unitPrice),
          value: { amount: toWireAmount(l.value), currency: envelope.currency },
        })),
      },
    }, {
      onSuccess: () => done(t('holdings.snapshot.saved')),
      onError: (error) => {
        const p = toProblem(error);
        const errors = p.errors ?? [];
        const placed = errors.filter((fe) => {
          if (fe.path === 'asOf' || fe.path === 'sourceLabel') { form.setError(fe.path, { message: messageFor(t, fe) }); return true; }
          const m = /^lines\[(\d+)\]\.(units|unitPrice|value)$/.exec(fe.path);
          const at = m ? sent[Number(m[1])] : undefined;
          if (!at) return false;
          form.setError(`lines.${at.i}.${m![2] as keyof Line}`, { message: messageFor(t, fe) });
          return true;
        });
        if (placed.length < errors.length || errors.length === 0) setProblem(present(p));
      },
    });
  });

  return (
    <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }}>
      {problem ? <ProblemMessage problem={problem} /> : null}
      <div className="grid gap-3 sm:grid-cols-2">
        <Controller control={form.control} name="asOf" render={({ field }) => (
          <Field label={t('envelope.date')} error={e.asOf?.message}>
            {(c) => <DateField {...c} max={todayIso()} value={field.value} onChange={field.onChange} />}
          </Field>
        )} />
        <Field label={t('holdings.snapshot.source')} hint={t('holdings.snapshot.source_hint')} error={e.sourceLabel?.message}>
          {(c) => <Input {...c} {...form.register('sourceLabel')} />}
        </Field>
      </div>
      {previous ? <p className="text-sm text-muted-foreground">{t('holdings.snapshot.prefilled', { date: formatLocalDate(previous.asOf) })}</p> : null}
      <ul className="space-y-3">
        {holdings.map((h, i) => (
          <li key={h.id} className="space-y-2 rounded-lg border p-3">
            <p className="font-medium">{h.name}</p>
            <div className="grid gap-2 sm:grid-cols-3">
              {hasUnits(h.kind) ? (
                <>
                  <Field label={t('holdings.snapshot.units', { name: h.name })} error={e.lines?.[i]?.units?.message}>
                    {(c) => <Input inputMode="decimal" className="text-right tabular-nums" {...c} {...form.register(`lines.${i}.units`)} />}
                  </Field>
                  <Field label={t('holdings.snapshot.unit_price', { name: h.name })} error={e.lines?.[i]?.unitPrice?.message}>
                    {(c) => <Input inputMode="decimal" className="text-right tabular-nums" {...c} {...form.register(`lines.${i}.unitPrice`)} />}
                  </Field>
                </>
              ) : null}
              <Field label={t('holdings.snapshot.value', { name: h.name })} error={e.lines?.[i]?.value?.message}>
                {(c) => <Input inputMode="decimal" className="text-right tabular-nums" {...c} {...form.register(`lines.${i}.value`)} />}
              </Field>
            </div>
          </li>
        ))}
      </ul>
      <Controller control={form.control} name="recordValuation" render={({ field }) => (
        <Field inline label={t('holdings.snapshot.record_valuation')} hint={t('holdings.snapshot.record_valuation_hint')}>
          {(c) => <Checkbox {...c} checked={field.value} onCheckedChange={(v) => field.onChange(v === true)} />}
        </Field>
      )} />
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => done()}>{t('envelope.cancel')}</Button>
        <Button type="submit" disabled={save.isPending}>{t('holdings.snapshot.submit')}</Button>
      </div>
    </form>
  );
}
