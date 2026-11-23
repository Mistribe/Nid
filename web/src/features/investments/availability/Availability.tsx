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
import { SelectField } from '@/components/form/SelectField';
import { useSubmitErrors } from '@/components/form/useSubmitErrors';
import { InfoTip } from '@/components/InfoTip';
import { formatLocalDate, LocalDate } from '@/components/LocalDate';
import { formatMoney, Money } from '@/components/Money';
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
  const [editing, setEditing] = useState<'dates' | 'early' | null>(null);
  const availability = envelope.availability;
  const early = availability?.earlyRelease;
  return (
    <Section title={t('envelope.availability.title')} tip={<InfoTip label={t('envelope.availability.title')}>{t('envelope.availability.tip')}</InfoTip>}
      actions={writable ? (
        <div className="flex gap-2">
          <Button type="button" variant="outline" size="sm" onClick={() => setEditing('dates')}>
            {availability?.tranches.length ? t('envelope.availability.edit') : t('envelope.availability.enter')}
          </Button>
          <Button type="button" variant="outline" size="sm" onClick={() => setEditing('early')}>{t('envelope.early_release.action')}</Button>
        </div>
      ) : null}>
      {early ? (
        <p className="mb-2 text-sm">
          {t(early.released ? 'envelope.early_release.stated' : 'envelope.early_release.stated_no_value', {
            case: t(`envelope.early_release.case.${early.case}`, { defaultValue: early.case }), date: formatLocalDate(early.on, 'short'),
            amount: early.released ? formatMoney(early.released) : null,
          })}
          {' '}<InfoTip label={t('envelope.early_release.title')}>{t('envelope.early_release.tip')}</InfoTip>
        </p>
      ) : null}
      {availability?.locked ? <p className="mb-2 text-sm">{t('envelope.early_release.locked', { amount: formatMoney(availability.locked) })}</p> : null}
      {!availability ? <p className="text-sm text-muted-foreground">{t('envelope.availability.none')}</p>
        : availability.releases.length === 0 ? (availability.locked ? null : <p className="text-sm">{t('envelope.availability.all_available')}</p>) : (
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
      {editing === 'dates' ? (
        <FormDialog title={t('envelope.availability.dialog')} onClose={() => setEditing(null)} onDone={(message) => { setEditing(null); toast.success(message); }}>
          {({ titleId, done }) => <AvailabilityForm envelope={envelope} titleId={titleId} onDone={done} />}
        </FormDialog>
      ) : null}
      {editing === 'early' ? (
        <FormDialog title={t('envelope.early_release.title')} onClose={() => setEditing(null)} onDone={(message) => { setEditing(null); toast.success(message); }}>
          {({ titleId, done }) => <EarlyReleaseForm envelope={envelope} titleId={titleId} onDone={done} />}
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

/** #466: the legal cases, in the order the household reads them; the server keeps the same list. */
const CASES = ['home_purchase', 'energy_renovation', 'marriage', 'third_child', 'divorce', 'domestic_violence', 'end_of_contract',
  'business_creation', 'disability', 'death', 'over_indebtedness', 'other'] as const;

const earlySchema = (t: TFunction) => z.object({
  case: z.string().min(1, t('envelope.early_release.case_required')),
  on: z.string().regex(ISO, t('envelope.availability.date_format')),
  // Empty: the whole plan.
  amount: z.string().refine((v) => v === '' || POSITIVE_AMOUNT.test(v), t('investments_form.positive_amount_format')),
});

/** #466: an early-release case and its date; the amount it allows, or the whole plan. Eligibility is the household's to state. */
function EarlyReleaseForm({ envelope, titleId, onDone }: { envelope: EnvelopeDetail; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const stated = envelope.availability?.earlyRelease;
  const form = useForm({
    resolver: zodResolver(earlySchema(t)),
    defaultValues: { case: stated?.case ?? '', on: stated?.on ?? '', amount: stated?.amount ? toFieldAmount(trimAmount(stated.amount.amount)) : '' },
  });
  const errors = useSubmitErrors(form.setError, (path) => {
    const m = /^earlyRelease\.(case|on|amount)$/.exec(path);
    return m ? (m[1] as 'case' | 'on' | 'amount') : undefined;
  });
  const save = $api.useMutation('patch', ENVELOPE, {
    onSuccess: (_, sent) => {
      errors.clear(); void invalidateInvestments(queryClient);
      onDone(t(sent.body.clearEarlyRelease ? 'envelope.early_release.removed' : 'envelope.early_release.saved'));
    },
    onError: errors.onError,
  });
  const submit = form.handleSubmit((values) => save.mutate({
    params: { path: { id: envelope.id } },
    body: { ...KEEP, earlyRelease: { case: values.case, on: values.on, amount: values.amount === '' ? null : toWireAmount(values.amount) } },
  }));
  const e = form.formState.errors;

  return (
    <form aria-labelledby={titleId} onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3" noValidate>
      <p className="text-sm text-muted-foreground">{t('envelope.early_release.tip')}</p>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <Controller control={form.control} name="case" render={({ field }) => (
        <Field label={t('envelope.early_release.case_label')} error={e.case?.message}>
          {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} placeholder={t('envelope.early_release.case_placeholder')}
            options={CASES.map((value) => ({ value, label: t(`envelope.early_release.case.${value}`) }))} />}
        </Field>
      )} />
      <Controller control={form.control} name="on" render={({ field }) => (
        <Field label={t('envelope.early_release.on')} error={e.on?.message}>
          {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
        </Field>
      )} />
      <Field label={t('envelope.availability.amount', { currency: envelope.currency })} hint={t('envelope.early_release.amount_hint')} error={e.amount?.message}>
        {(c) => <Input inputMode="decimal" {...c} {...form.register('amount')} />}
      </Field>
      <div className="flex justify-end gap-2">
        {stated ? (
          <Button type="button" variant="outline" disabled={save.isPending} className="mr-auto"
            onClick={() => save.mutate({ params: { path: { id: envelope.id } }, body: { ...KEEP, clearEarlyRelease: true } })}>
            {t('envelope.early_release.remove')}
          </Button>
        ) : null}
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('envelope.cancel')}</Button>
        <Button type="submit" disabled={save.isPending}>{t('envelope.save')}</Button>
      </div>
    </form>
  );
}
