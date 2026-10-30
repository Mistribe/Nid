import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Controller, useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { z } from 'zod';
import { useApi } from '@/api/ApiProvider';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { SelectField } from '@/components/form/SelectField';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { asEditBody, DETAIL, invalidateSubscriptions, type Detail } from '../api';
import { ISO_DATE } from '../edits/amount';
import { Reload } from '../edits/Reload';
import { useSubmitErrors } from '../edits/useSubmitErrors';
import '@/locales/forms';
import '@/locales/subscriptions';

const UNITS = ['day', 'week', 'month', 'year'] as const;
type DateName = 'anniversaryOn' | 'commitmentEndsOn' | 'cancellableFrom';
const PATHS = {
  anniversaryOn: 'anniversaryOn', commitmentEndsOn: 'commitmentEndsOn', cancellableFrom: 'cancellableFrom',
  notice: 'noticeCount', 'notice.count': 'noticeCount', 'notice.unit': 'noticeUnit',
} as const;

/** Spec §5: the contract's dates and notice in a dialog. Only what changed is sent; an emptied field sends its `clear…` flag. */
export function ContractTermsForm({ detail, titleId, onDone }: { detail: Detail; titleId: string; onDone: (message?: string) => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const date = z.string().refine((v) => v === '' || ISO_DATE.test(v), t('edit.invalid_date'));
  const schema = z.object({
    anniversaryOn: date, commitmentEndsOn: date, cancellableFrom: date,
    noticeCount: z.string().trim().regex(/^([1-9]\d{0,3})?$/, t('edit.invalid_count')),
    noticeUnit: z.string(),
  });
  const terms = detail.contractTerms;
  const form = useForm({
    resolver: zodResolver(schema),
    defaultValues: {
      anniversaryOn: terms?.anniversaryOn ?? '', commitmentEndsOn: terms?.commitmentEndsOn ?? '', cancellableFrom: terms?.cancellableFrom ?? '',
      noticeCount: terms?.notice ? String(terms.notice.count) : '', noticeUnit: terms?.notice?.unit ?? 'month',
    },
  });
  // Read while rendering, so the form state tracks it.
  const dirty = form.formState.dirtyFields;
  const errors = useSubmitErrors(form.setError, PATHS);
  const save = $api.useMutation('patch', DETAIL, {
    onSuccess: () => { void invalidateSubscriptions(queryClient); onDone(t('edit.saved')); },
    onError: (error) => errors.onError(error),
  });

  const submit = form.handleSubmit((values) => {
    errors.clear();
    const noticeDirty = !!dirty.noticeCount || !!dirty.noticeUnit;
    const emptied = (f: DateName) => !!dirty[f] && values[f] === '';
    const set = (f: DateName) => (dirty[f] && values[f] !== '' ? { [f]: values[f] } : {});
    const body = {
      clearAnniversaryOn: emptied('anniversaryOn'),
      clearNotice: noticeDirty && values.noticeCount === '' && !!terms?.notice,
      clearCommitmentEndsOn: emptied('commitmentEndsOn'),
      clearCancellableFrom: emptied('cancellableFrom'),
      ...set('anniversaryOn'), ...set('commitmentEndsOn'), ...set('cancellableFrom'),
      ...(noticeDirty && values.noticeCount !== '' ? { notice: { count: Number(values.noticeCount), unit: values.noticeUnit } } : {}),
    };
    if (Object.values(body).every((v) => v === false)) { onDone(); return; }
    save.mutate({ params: { path: { id: detail.id } }, body: asEditBody(body) });
  });

  const e = form.formState.errors;
  const dateField = (name: DateName, label: string) => (
    <Controller control={form.control} name={name} render={({ field }) => (
      <Field label={label} error={e[name]?.message}>
        {(c) => <DateField {...c} value={field.value} onChange={field.onChange} />}
      </Field>
    )} />
  );
  return (
    <form aria-labelledby={titleId} onSubmit={(ev) => { ev.stopPropagation(); void submit(ev); }} className="space-y-3" noValidate>
      {errors.problem ? <><ProblemMessage problem={errors.problem} />{errors.problem.kind === 'conflict' ? <Reload /> : null}</> : null}
      {dateField('anniversaryOn', t('contract_terms.anniversary'))}
      <div className="grid gap-3 sm:grid-cols-2">
        <Field label={t('contract_terms.notice')} hint={t('contract_terms.hint_notice')} error={e.noticeCount?.message}>
          {(c) => <Input inputMode="numeric" {...c} {...form.register('noticeCount')} />}
        </Field>
        <Controller control={form.control} name="noticeUnit" render={({ field }) => (
          <Field label={t('contract_terms.notice_unit')} error={e.noticeUnit?.message}>
            {(c) => <SelectField {...c} value={field.value} onValueChange={field.onChange} options={UNITS.map((u) => ({ value: u, label: t(`edit.unit.${u}`) }))} />}
          </Field>
        )} />
      </div>
      {dateField('commitmentEndsOn', t('contract_terms.commitment_ends'))}
      {dateField('cancellableFrom', t('contract_terms.cancellable_from'))}
      <div className="flex justify-end gap-2">
        <Button type="button" variant="outline" onClick={() => onDone()}>{t('common.cancel')}</Button>
        <Button type="submit" disabled={save.isPending}>{t('contract_terms.submit')}</Button>
      </div>
    </form>
  );
}
