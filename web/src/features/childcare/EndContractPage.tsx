import { useNavigate, useParams } from '@tanstack/react-router';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { present, toProblem } from '@/api/errors';
import { DateField } from '@/components/form/DateField';
import { Field } from '@/components/form/Field';
import { fieldErrorsOf } from '@/components/form/fieldErrors';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { ErrorPage } from '@/components/ErrorPage';
import { formatLocalDate } from '@/components/LocalDate';
import { InfoTip } from '@/components/InfoTip';
import { FormPage } from '@/components/page/FormPage';
import { PageSkeleton } from '@/components/page/skeletons';
import { StepForm } from '@/components/page/StepForm';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Input } from '@/components/ui/input';
import { AMOUNT, ISO_DATE, toWireAmount } from '@/lib/amount';
import { useChildcare, useCloseChildcare, useContractEnd } from './api';
import { endReasonLabel } from './statementText';
import '@/locales/childcare';

/** IDCC 3239 art. 118–121: who ends the contract, and the reasons each side has. */
const REASONS = { employer: ['withdrawal', 'misconduct', 'approval', 'child_death', 'trial'], carer: ['resignation', 'trial'] } as const;
type Who = keyof typeof REASONS;
const STEP_FIELDS = { why: ['reason'], when: ['notified'], end: ['on', 'gross'] } as const;

/** A field's explanation, named after the field (`childcare_end.<name>` and its `_tip`). */
function Tip({ name }: { name: 'why' | 'notified' | 'notice_end' | 'indemnity' | 'on' | 'gross' }) {
  const { t } = useTranslation();
  return <InfoTip label={t(`childcare_end.${name}`)}>{t(`childcare_end.${name}_tip`)}</InfoTip>;
}

/**
 * The end of a childminder's contract in three plain questions: who ends it and why, when the letter was handed over, the last day
 * (the notice's end by default, from the server). The settlement's figures come with the last month's statement.
 */
export function EndContractPage() {
  const { t } = useTranslation();
  const { id } = useParams({ from: '/childcare/$id/end' });
  const navigate = useNavigate();
  const answer = useChildcare(id);
  const [who, setWho] = useState<Who>('employer');
  const [reason, setReason] = useState<string>('withdrawal');
  const [notified, setNotified] = useState('');
  const [on, setOn] = useState('');
  const [gross, setGross] = useState('');
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [reveal, setReveal] = useState<{ step: string } | null>(null);
  const preview = useContractEnd(id, reason, notified);
  const close = useCloseChildcare();
  const end = preview.data;

  // The notice's end is the default last day, until the person picks another.
  const [picked, setPicked] = useState(false);
  useEffect(() => { if (end && !picked) setOn(end.noticeEnd); }, [end, picked]);

  const crumbs = [{ label: t('childcare_list.title'), to: '/childcare' as const }];
  if (answer.error) return <ErrorPage problem={present(toProblem(answer.error))} crumbs={crumbs} onRetry={() => void answer.refetch()} />;
  if (!answer.data) return <PageSkeleton label={t('childcare_detail.loading')} stats={0} />;
  const { summary } = answer.data;
  const needsGross = Boolean(end?.indemnityDue && !end.grossRateKnown);

  const check = (field: string): string | null => {
    switch (field) {
      case 'notified': return ISO_DATE.test(notified) ? (notified < summary.start ? t('childcare_end.error.notified_before_start') : null) : t('childcare_form.error.date_format');
      case 'on': return ISO_DATE.test(on) ? (on < notified ? t('childcare_end.error.on_before_notified') : null) : t('childcare_form.error.date_format');
      case 'gross': return needsGross && !AMOUNT.test(gross.trim()) ? t('childcare_end.error.gross_required') : null;
      default: return null;
    }
  };
  const trigger = (fields: readonly string[]) => {
    const found: Record<string, string> = {};
    for (const f of fields) { const message = check(f); if (message) found[f] = message; }
    setErrors(found);
    return Promise.resolve(Object.keys(found).length === 0);
  };

  const submit = () => {
    void trigger([...STEP_FIELDS.when, ...STEP_FIELDS.end]).then((ok) => {
      if (!ok) return;
      close.mutate({
        params: { path: { id } },
        body: { on, reason, notified, ...(needsGross ? { grossHourlyRate: toWireAmount(gross) } : {}) },
      }, {
        onSuccess: () => void navigate({ to: '/childcare/$id/settlement', params: { id } }),
        onError: (e) => {
          const fields = fieldErrorsOf(e);
          setErrors(Object.fromEntries(fields.map((f) => [f.path === 'grossHourlyRate' ? 'gross' : f.path, t(`childcare_form.error.${f.code.replaceAll('.', '_')}`, { defaultValue: f.message })])));
          if (fields.some((f) => f.path === 'notified')) setReveal({ step: 'when' });
          else if (fields.length > 0) setReveal({ step: 'end' });
        },
      });
    });
  };

  const options = (side: Who) => REASONS[side].map((r) => ({ value: r, label: endReasonLabel(t, r) }));
  return (
    <FormPage crumbs={[...crumbs, { label: summary.name, to: '/childcare/$id', params: { id } }]} title={t('childcare_end.title')}>
      {close.error && fieldErrorsOf(close.error).length === 0 ? <ProblemMessage problem={present(toProblem(close.error))} /> : null}
      <StepForm trigger={trigger} onSubmit={submit} submitLabel={t('childcare_end.submit')} pending={close.isPending} reveal={reveal}
        steps={[
          {
            id: 'why', title: t('childcare_end.step_why'), fields: [...STEP_FIELDS.why], content: (
              <>
                <Field label={t('childcare_end.who')}>
                  {() => <Segmented aria-label={t('childcare_end.who')} value={who} options={[{ value: 'employer', label: t('childcare_end.who_employer') }, { value: 'carer', label: t('childcare_end.who_carer') }]}
                    onValueChange={(v) => { setWho(v as Who); setReason(REASONS[v as Who][0]); }} />}
                </Field>
                <Field label={t('childcare_end.why')} tip={<Tip name="why" />}>
                  {(c) => <SelectField {...c} value={reason} onValueChange={setReason} options={options(who)} />}
                </Field>
              </>
            ),
          },
          {
            id: 'when', title: t('childcare_end.step_when'), fields: [...STEP_FIELDS.when], content: (
              <Field label={t('childcare_end.notified')} tip={<Tip name="notified" />} hint={t('childcare_end.notified_hint')} error={errors.notified}>
                {(c) => <DateField {...c} value={notified} onChange={setNotified} />}
              </Field>
            ),
          },
          {
            id: 'end', title: t('childcare_end.step_end'), fields: [...STEP_FIELDS.end], content: (
              <>
                {end ? (
                  <dl className="space-y-1 text-sm">
                    <div className="flex flex-wrap gap-x-2"><dt className="text-muted-foreground">{t('childcare_end.seniority')}</dt>
                      <dd>{t('childcare_end.months', { count: end.seniorityMonths })}</dd></div>
                    <div className="flex flex-wrap gap-x-2"><dt className="text-muted-foreground">{t('childcare_end.notice_end')} <Tip name="notice_end" /></dt>
                      <dd>{end.noticeEnd === notified ? t('childcare_end.no_notice') : formatLocalDate(end.noticeEnd)}</dd></div>
                    <div className="flex flex-wrap gap-x-2"><dt className="text-muted-foreground">{t('childcare_end.indemnity')}</dt>
                      <dd>{end.indemnityDue ? t('childcare_end.indemnity_due') : t('childcare_end.indemnity_not_due')} <Tip name="indemnity" /></dd></div>
                  </dl>
                ) : null}
                <Field label={t('childcare_end.on')} tip={<Tip name="on" />} error={errors.on}>
                  {(c) => <DateField {...c} value={on} onChange={(v) => { setPicked(true); setOn(v); }} />}
                </Field>
                {needsGross ? (
                  <Field label={t('childcare_end.gross')} tip={<Tip name="gross" />} hint={t('childcare_end.gross_hint')} error={errors.gross}>
                    {(c) => <Input {...c} inputMode="decimal" className="w-32" value={gross} onChange={(e) => setGross(e.target.value)} />}
                  </Field>
                ) : null}
                <p className="text-sm text-muted-foreground">{t('childcare_end.next')}</p>
              </>
            ),
          },
        ]}
      />
    </FormPage>
  );
}
