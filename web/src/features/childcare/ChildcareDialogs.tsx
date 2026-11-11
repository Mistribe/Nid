import { zodResolver } from '@hookform/resolvers/zod';
import { useEffect, useRef, useState } from 'react';
import { useForm, type Path, type Resolver } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { FormDialog } from '@/components/FormDialog';
import { DateField } from '@/components/form/DateField';
import { formatLocalDate } from '@/components/LocalDate';
import { Field } from '@/components/form/Field';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { useCloseChildcare, useEditChildcare, useReplaceGaps, useReviseChildcare, useSetUnpaidAbsences, type ChildcareDetail, type ChildcareTerms } from './api';
import {
  carerToWire, childcarePath, editSchema, gapsDefaults, gapsSchema, gapsToWire, termsDefaults, termsSchema, termsToWire,
  type EditValues, type GapRow, type TermsValues,
} from './childcareSchema';
import { GapFields } from './GapFields';
import { RateFields, UnpaidAbsencesField } from './RateFields';
import { WeekTypesFields } from './WeekTypesFields';
import { useChildcareErrors } from './useChildcareErrors';
import '@/locales/childcare';

type DialogProps = { detail: ChildcareDetail; onClose: () => void; onDone: (message: string) => void };

function Actions({ submit, pending, onCancel }: { submit: string; pending: boolean; onCancel: () => void }) {
  const { t } = useTranslation();
  return (
    <div className="flex justify-end gap-2">
      <Button type="button" variant="outline" onClick={onCancel}>{t('childcare_detail.cancel')}</Button>
      <Button type="submit" disabled={pending}>{submit}</Button>
    </div>
  );
}

function Unplaced({ messages }: { messages: string[] }) {
  return messages.length === 0 ? null : (
    <ul role="alert" className="space-y-1 rounded-md border border-destructive p-3 text-sm">{messages.map((m) => <li key={m}>{m}</li>)}</ul>
  );
}

const current = (detail: ChildcareDetail) => detail.terms[detail.terms.length - 1]!;

/** Corrects the name, the carer and the current terms in place, with the version read; a conflict reloads the form. */
export function EditChildcareDialog({ detail, onClose, onDone }: DialogProps) {
  const { t } = useTranslation();
  const edit = useEditChildcare();
  const defaults = (d: ChildcareDetail): EditValues => ({
    ...termsDefaults(current(d)), name: d.summary.name, carerName: d.summary.carerName, carerPajemploi: d.carerPajemploi ?? '', carerApproval: d.carerApproval ?? '',
  });
  const form = useForm<EditValues>({ resolver: zodResolver(editSchema(t)) as unknown as Resolver<EditValues>, defaultValues: defaults(detail) });
  // `gaps` has no field here: its error falls through to the banner.
  const errors = useChildcareErrors(form.setError, (p) => (p === 'gaps' ? undefined : childcarePath(p, form.getValues('weekTypes')) as Path<EditValues> | undefined));
  // A conflict refetches the arrangement: the form takes the fresh values with the fresh version, so a retry never overwrites blindly.
  const seen = useRef(detail.version);
  useEffect(() => {
    if (detail.version === seen.current) return;
    seen.current = detail.version;
    form.reset(defaults(detail));
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the version is the event.
  }, [detail.version]);
  return (
    <FormDialog title={t('childcare_detail.edit_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.stopPropagation();
          void form.handleSubmit((v) => {
            errors.clear();
            edit.mutate({
              params: { path: { id: detail.summary.id } },
              body: { name: v.name.trim(), carer: carerToWire(v), terms: termsToWire(v, current(detail).from), version: detail.version },
            }, { onSuccess: () => done(t('childcare_detail.saved')), onError: (e) => errors.onError(e) });
          })(event);
        }}>
          {errors.stale ? <p role="alert" className="rounded-md border border-destructive p-3">{t('childcare_detail.conflict')}</p> : null}
          {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
          <Unplaced messages={errors.unplaced} />
          <Field label={t('childcare_form.name')} error={form.formState.errors.name?.message}>{(c) => <Input {...c} {...form.register('name')} />}</Field>
          <Field label={t('childcare_form.carer')} error={form.formState.errors.carerName?.message}>{(c) => <Input {...c} {...form.register('carerName')} />}</Field>
          <Field label={t('childcare_form.pajemploi')} error={form.formState.errors.carerPajemploi?.message}>{(c) => <Input {...c} {...form.register('carerPajemploi')} />}</Field>
          <RateFields form={form} withFrom={false} />
          <WeekTypesFields form={form} />
          <Actions submit={t('childcare_detail.save')} pending={edit.isPending} onCancel={() => done()} />
        </form>
      )}
    </FormDialog>
  );
}

/** Spec §1.8: new terms from a date after the current ones' start, prefilled from them. */
export function ReviseDialog({ detail, onClose, onDone }: DialogProps) {
  const { t } = useTranslation();
  const revise = useReviseChildcare();
  const form = useForm<TermsValues>({ resolver: zodResolver(termsSchema(t, true)) as unknown as Resolver<TermsValues>, defaultValues: termsDefaults(current(detail)) });
  const errors = useChildcareErrors(form.setError, (p) => (p === 'gaps' ? undefined : childcarePath(p, form.getValues('weekTypes')) as Path<TermsValues> | undefined));
  return (
    <FormDialog title={t('childcare_detail.revise_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.stopPropagation();
          void form.handleSubmit((v) => {
            errors.clear();
            revise.mutate({ params: { path: { id: detail.summary.id } }, body: { terms: termsToWire(v, v.from) } },
              { onSuccess: () => done(t('childcare_detail.revised')), onError: (e) => errors.onError(e) });
          })(event);
        }}>
          {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
          <Unplaced messages={errors.unplaced} />
          <p className="text-sm text-muted-foreground">{t('childcare_detail.revision_month_hint')}</p>
          <RateFields form={form} withFrom fromLabel={t('childcare_detail.revision_from')} />
          <WeekTypesFields form={form} />
          <Actions submit={t('childcare_detail.revise_submit')} pending={revise.isPending} onCancel={() => done()} />
        </form>
      )}
    </FormDialog>
  );
}

export function GapsDialog({ detail, onClose, onDone }: DialogProps) {
  const { t } = useTranslation();
  const replace = useReplaceGaps();
  const form = useForm<{ gaps: GapRow[] }>({ resolver: zodResolver(gapsSchema(t)) as unknown as Resolver<{ gaps: GapRow[] }>, defaultValues: { gaps: gapsDefaults(detail) } });
  const errors = useChildcareErrors(form.setError, (p) => {
    const gap = /^gaps\[(\d+)\]\.(monday|weeks)$/.exec(p);
    return gap ? `gaps.${Number(gap[1])}.${gap[2] as 'monday' | 'weeks'}` : p === 'gaps' ? 'gaps' : undefined;
  });
  return (
    <FormDialog title={t('childcare_detail.gaps_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.stopPropagation();
          void form.handleSubmit((v) => {
            errors.clear();
            replace.mutate({ params: { path: { id: detail.summary.id } }, body: { gaps: gapsToWire(v.gaps) } },
              { onSuccess: () => done(t('childcare_detail.saved')), onError: (e) => errors.onError(e) });
          })(event);
        }}>
          {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
          <Unplaced messages={errors.unplaced} />
          <GapFields form={form} />
          <Actions submit={t('childcare_detail.save')} pending={replace.isPending} onCancel={() => done()} />
        </form>
      )}
    </FormDialog>
  );
}

/** Ends the current terms on the last day of care and archives the arrangement; it can be reopened. */
export function EndDialog({ detail, onClose, onDone }: DialogProps) {
  const { t } = useTranslation();
  const close = useCloseChildcare();
  const [on, setOn] = useState('');
  const [error, setError] = useState('');
  const errors = useChildcareErrors<{ on: string }>((_, e) => setError(e.message ?? ''), (p) => (p === 'on' ? 'on' : undefined));
  return (
    <FormDialog title={t('childcare_detail.end_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          if (!/^\d{4}-\d{2}-\d{2}$/.test(on)) { setError(t('childcare_form.error.date_format')); return; }
          errors.clear();
          close.mutate({ params: { path: { id: detail.summary.id } }, body: { on } },
            { onSuccess: () => done(t('childcare_detail.ended')), onError: (e) => errors.onError(e) });
        }}>
          {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
          <Unplaced messages={errors.unplaced} />
          <p className="text-sm text-muted-foreground">{t('childcare_detail.end_body', { name: detail.summary.name })}</p>
          <Field label={t('childcare_detail.end_on')} error={error || undefined}>{(c) => <DateField {...c} value={on} onChange={setOn} />}</Field>
          <Actions submit={t('childcare_detail.end_submit')} pending={close.isPending} onCancel={() => done()} />
        </form>
      )}
    </FormDialog>
  );
}

/** #192: corrects the absence reasons of one revision, a past one included; draft months recompute, validated ones keep their figures. */
export function UnpaidAbsencesDialog({ detail, revision, onClose, onDone }: DialogProps & { revision: ChildcareTerms }) {
  const { t } = useTranslation();
  const save = useSetUnpaidAbsences();
  const [unpaid, setUnpaid] = useState<string[]>(revision.unpaidAbsences);
  const errors = useChildcareErrors<{ unpaid: string }>(() => undefined, () => undefined);
  const period = revision.to
    ? t('childcare_detail.unpaid_period', { from: formatLocalDate(revision.from, 'short'), to: formatLocalDate(revision.to, 'short') })
    : t('childcare_detail.unpaid_period_open', { from: formatLocalDate(revision.from, 'short') });
  return (
    <FormDialog title={t('childcare_detail.unpaid_title')} onClose={onClose} onDone={onDone}>
      {({ titleId, done }) => (
        <form aria-labelledby={titleId} noValidate className="space-y-3" onSubmit={(event) => {
          event.preventDefault();
          event.stopPropagation();
          errors.clear();
          save.mutate({ params: { path: { id: detail.summary.id, from: revision.from } }, body: { unpaidAbsences: unpaid } },
            { onSuccess: () => done(t('childcare_detail.saved')), onError: (e) => errors.onError(e) });
        }}>
          {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
          <Unplaced messages={errors.unplaced} />
          <p className="text-sm text-muted-foreground">{period} {t('childcare_detail.unpaid_body')}</p>
          <UnpaidAbsencesField value={unpaid} onChange={setUnpaid} />
          <Actions submit={t('childcare_detail.save')} pending={save.isPending} onCancel={() => done()} />
        </form>
      )}
    </FormDialog>
  );
}
