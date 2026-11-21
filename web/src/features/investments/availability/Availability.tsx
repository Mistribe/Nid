import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Plus, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { Controller, useFieldArray, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import type { TFunction } from 'i18next';
import { toast } from 'sonner';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { ENVELOPE, invalidateInvestments, type EnvelopeDetail } from '@/api/investments';
import { FormDialog } from '@/components/FormDialog';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { InfoTip } from '@/components/InfoTip';
import { LocalDate } from '@/components/LocalDate';
import { Money } from '@/components/Money';
import { Section } from '@/components/page/Section';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { POSITIVE_AMOUNT, toFieldAmount, toWireAmount, trimAmount } from '@/lib/amount';
import '@/locales/investments';

// The edit body's flags the wire requires: nothing else of the envelope changes.
const KEEP = { clearInstitution: false, clearAccountHint: false, clearOpenedOn: false, clearStaleAfterDays: false, clearNote: false };
const ISO = /^\d{4}-\d{2}-\d{2}$/;

const schema = (t: TFunction) => z.object({
  tranches: z.array(z.object({
    availableOn: z.string().regex(ISO, t('envelope.availability.date_format')),
    amount: z.string().regex(POSITIVE_AMOUNT, t('investments_form.positive_amount_format')),
  })).superRefine((rows, ctx) => {
    const seen = new Set<string>();
    rows.forEach((row, i) => {
      if (seen.has(row.availableOn)) ctx.addIssue({ code: 'custom', path: [i, 'availableOn'], message: t('envelope.availability.duplicate') });
      seen.add(row.availableOn);
    });
  }),
});

/**
 * #184: a PEE's availability, on its overview. The server splits the latest value by the tranches its statement prints (date and amount):
 * what is available today and the releases to come. Without tranches, the whole plan counts as locked.
 */
export function AvailabilitySection({ envelope, writable }: { envelope: EnvelopeDetail; writable: boolean }) {
  const { t } = useTranslation();
  const [editing, setEditing] = useState(false);
  const availability = envelope.availability;
  return (
    <Section title={t('envelope.availability.title')} tip={<InfoTip label={t('envelope.availability.title')}>{t('envelope.availability.tip')}</InfoTip>}
      actions={writable ? (
        <Button type="button" variant="outline" size="sm" onClick={() => setEditing(true)}>
          {availability ? t('envelope.availability.edit') : t('envelope.availability.enter')}
        </Button>
      ) : null}>
      {!availability ? <p className="text-sm text-muted-foreground">{t('envelope.availability.none')}</p>
        : availability.releases.length === 0 ? <p className="text-sm">{t('envelope.availability.all_available')}</p> : (
          <Table aria-label={t('envelope.availability.releases')}>
            <TableHeader>
              <TableRow>
                <TableHead>{t('envelope.availability.on')}</TableHead>
                <TableHead className="text-right">{t('envelope.availability.released')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {availability.releases.map((r) => (
                <TableRow key={r.on}>
                  <TableCell><LocalDate value={r.on} /></TableCell>
                  <TableCell className="text-right"><Money value={r.amount} tabular /></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      {editing ? (
        <FormDialog title={t('envelope.availability.dialog')} onClose={() => setEditing(false)} onDone={(message) => { setEditing(false); toast.success(message); }}>
          {({ titleId, done }) => <AvailabilityForm envelope={envelope} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
    </Section>
  );
}

function AvailabilityForm({ envelope, titleId, onDone }: { envelope: EnvelopeDetail; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const printed = envelope.availability?.tranches ?? [];
  const form = useForm({
    resolver: zodResolver(schema(t)),
    defaultValues: {
      tranches: printed.length > 0
        ? printed.map((p) => ({ availableOn: p.availableOn, amount: toFieldAmount(trimAmount(p.amount.amount)) }))
        : [{ availableOn: '', amount: '' }],
    },
  });
  const rows = useFieldArray({ control: form.control, name: 'tranches' });
  // A row's field the server names lands on that row; a refusal of the whole list shows above the form.
  const errors = useSubmitErrors(form.setError, (path) => {
    const m = /^availability\[(\d+)\]\.(availableOn|amount)$/.exec(path);
    return m ? (`tranches.${Number(m[1])}.${m[2] as 'availableOn' | 'amount'}` as const) : undefined;
  });
  const save = $api.useMutation('patch', ENVELOPE, {
    onSuccess: () => { errors.clear(); void invalidateInvestments(queryClient); onDone(t('envelope.availability.saved')); },
    onError: errors.onError,
  });
  const submit = form.handleSubmit((values) => save.mutate({
    params: { path: { id: envelope.id } },
    body: {
      ...KEEP, availability: values.tranches.map((r) => ({ availableOn: r.availableOn, amount: toWireAmount(r.amount) })),
    },
  }));
  const e = form.formState.errors.tranches;

  return (
    <form aria-labelledby={titleId} onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3" noValidate>
      <p className="text-sm text-muted-foreground">{t('envelope.availability.how')}</p>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      {rows.fields.map((row, i) => (
        <div key={row.id} className="grid items-end gap-3 sm:grid-cols-[1fr_1fr_auto]">
          <Controller control={form.control} name={`tranches.${i}.availableOn`} render={({ field }) => (
            <Field label={t('envelope.availability.on')} error={e?.[i]?.availableOn?.message}>
              {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
            </Field>
          )} />
          <Field label={t('envelope.availability.amount', { currency: envelope.currency })} error={e?.[i]?.amount?.message}>
            {(c) => <Input inputMode="decimal" {...c} {...form.register(`tranches.${i}.amount`)} />}
          </Field>
          <Button type="button" variant="ghost" size="icon" aria-label={t('envelope.availability.remove', { n: i + 1 })} onClick={() => rows.remove(i)}>
            <Trash2 aria-hidden="true" />
          </Button>
        </div>
      ))}
      <Button type="button" variant="outline" size="sm" onClick={() => rows.append({ availableOn: '', amount: '' })}>
        <Plus aria-hidden="true" />{t('envelope.availability.add')}
      </Button>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('envelope.cancel')}</Button>
        <Button type="submit" disabled={save.isPending}>{t('envelope.save')}</Button>
      </div>
    </form>
  );
}
