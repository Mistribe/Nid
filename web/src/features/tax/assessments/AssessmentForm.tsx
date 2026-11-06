import { zodResolver } from '@hookform/resolvers/zod';
import { useNavigate, useParams } from '@tanstack/react-router';
import { useEffect, useRef, useState, type ReactNode } from 'react';
import { Controller, get, useForm, type Path, type Resolver, type UseFormReturn } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Problem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import { ComboboxField } from '@/components/form/ComboboxField';
import { Field } from '@/components/form/Field';
import { Segmented } from '@/components/form/Segmented';
import { SelectField } from '@/components/form/SelectField';
import { FormPage } from '@/components/page/FormPage';
import { StepForm } from '@/components/page/StepForm';
import { PageTrail } from '@/components/page/trail';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Input } from '@/components/ui/input';
import { DOCUMENT, invalidateDocuments, uploadDocument, useDocuments } from '../../documents/api';
import { refusal, UNSUPPORTED, UploadZone } from '../../documents/DocumentUpload';
import { Folded } from '../../loans/new/Folded';
import { DeclarantGrid, type GridRow } from '../profile/DeclarantGrid';
import { PERSONS } from '../../subscriptions/api';
import { fieldErrorsOf } from '../../subscriptions/fieldErrors';
import { useSubmitErrors } from '../../subscriptions/edits/useSubmitErrors';
import { AMOUNT, toFieldAmount, toWireAmount } from '../../subscriptions/edits/amount';
import { todayIso } from '../../subscriptions/today';
import { FIRST_ESTIMATE_YEAR } from '../api';
import {
  assessmentDefaults, assessmentPath, assessmentSchema, fromView, NOTICE_AMOUNTS, PER_TYPED, perLineYear, RATES, stepFields, stepOf, toBody,
  type AssessmentValues, type StepId,
} from './assessmentSchema';
import { SALARY_ALLOWANCE, TAX_NOTICE_PREVIEW, useAddTaxAssessment, useCorrectTaxAssessment, useTaxAssessment, type TaxAssessment } from './api';
import { useQueryClient } from '@tanstack/react-query';
import tax from '@/locales/fr.tax-assessments.json';
import '@/locales/documents';
import '@/locales/tax';
import '@/locales/taxAssessments';
import { PageSkeleton } from '@/components/page/skeletons';

type Form = UseFormReturn<AssessmentValues>;
type FieldKey = keyof typeof tax.tax.assessment.field;
const errorOf = (form: Form, name: string) => get<typeof form.formState.errors, { message?: string } | undefined>(form.formState.errors, name)?.message;
const STEP_IDS: StepId[] = ['household', 'declarants', 'income', 'tax', 'source'];

/** One amount (or rate, or share count) as typed: the label is the notice's own. */
function Amount({ form, name, label, hint }: { form: Form; name: string; label: string; hint?: string }) {
  return (
    <Field label={label} hint={hint} error={errorOf(form, name)}>
      {(c) => <Input inputMode="decimal" {...c} {...form.register(name as Path<AssessmentValues>)} />}
    </Field>
  );
}

function HouseholdStep({ form, adults }: { form: Form; adults: { value: string; label: string }[] }) {
  const { t } = useTranslation();
  const e = form.formState.errors;
  const label = (key: FieldKey) => t(`tax.assessment.field.${key}`);
  const lastYear = Number(todayIso().slice(0, 4)) - 1;
  const years = Array.from({ length: lastYear - 1999 }, (_, i) => String(lastYear - i)).map((y) => ({ value: y, label: y }));
  const rank = (n: number) => t('tax.assessment.declarant', { rank: n });
  return (
    <>
      <Controller control={form.control} name="year" render={({ field: f }) => (
        <Field label={t('tax.assessment_form.field.year')} error={e.year?.message}>{(c) => <SelectField {...c} value={f.value} onValueChange={f.onChange} options={years} />}</Field>
      )} />
      <Field label={t('tax.assessment_form.field.issuedYear')} hint={t('tax.assessment_form.issued_year_hint')} error={e.issuedYear?.message}>
        {(c) => <Input inputMode="numeric" placeholder={String(Number(form.watch('year')) + 1)} {...c} {...form.register('issuedYear')} />}
      </Field>
      <Controller control={form.control} name="declarants.0.personId" render={({ field: f }) => (
        <Field label={rank(1)} error={e.declarants?.[0]?.personId?.message}>{(c) => <SelectField {...c} value={f.value} onValueChange={f.onChange} options={adults} />}</Field>
      )} />
      {adults.length > 1 ? (
        <Controller control={form.control} name="declarants.1.personId" render={({ field: f }) => (
          <Field label={rank(2)} error={e.declarants?.[1]?.personId?.message}>
            {(c) => <SelectField {...c} value={f.value} onValueChange={f.onChange} options={[{ value: '', label: t('tax.assessment_form.none') }, ...adults]} />}
          </Field>
        )} />
      ) : null}
      <Amount form={form} name="shares" label={label('shares')} />
      <Amount form={form} name="referenceIncome" label={label('referenceIncome')} />
      <Folded label={t('tax.assessment_form.rates')} hasError={RATES.some((k) => Boolean(errorOf(form, k)))}>
        {RATES.map((k) => <Amount key={k} form={form} name={k} label={label(k)} />)}
      </Folded>
    </>
  );
}

/**
 * « 10 % » fills the deduction with the server's figure (rules from the revenues of 2025 on), again whenever the salaries change;
 * it stays editable, and the net salaries are the server's. What the step opened on is never refilled: a stored notice keeps the
 * figures it prints.
 */
function SalaryAllowance({ form, index }: { form: Form; index: number }) {
  const { $api } = useApi();
  const year = form.watch('year');
  const gross = toWireAmount(form.watch(`declarants.${index}.grossSalaries`));
  const deduction = form.watch(`declarants.${index}.deduction`);
  // The salaries once the person stops typing: one request, not one per digit.
  const [settled, setSettled] = useState(gross);
  useEffect(() => { const timer = setTimeout(() => setSettled(gross), 300); return () => clearTimeout(timer); }, [gross]);
  const key = `${year}|${settled}|${deduction}`;
  const filled = useRef(key);
  const wanted = deduction === 'allowance' && AMOUNT.test(settled) && Number(year) >= FIRST_ESTIMATE_YEAR && key !== filled.current;
  const allowance = $api.useQuery('get', SALARY_ALLOWANCE, { params: { query: { year: Number(year), grossSalaries: settled } } }, { enabled: wanted });
  useEffect(() => {
    if (!wanted || !allowance.data) return;
    filled.current = key;
    const options = { shouldDirty: true, shouldValidate: true };
    form.setValue(`declarants.${index}.deductionAmount`, toFieldAmount(allowance.data.deductionAmount.amount), options);
  }, [wanted, allowance.data, key, form, index]);
  return null;
}

/** The declarants side by side, as the tax profile's grid: one row per line of the notice, one column per declarant. */
function DeclarantStep({ form, adults }: { form: Form; adults: { value: string; label: string }[] }) {
  const { t } = useTranslation();
  const year = form.watch('year');
  const label = (k: FieldKey) => t(`tax.assessment.field.${k}`);
  // A column per chosen declarant, keyed by its rank: the form's paths are by index.
  const columns = form.watch('declarants').flatMap((d, i) => (d.personId
    ? [{ id: String(i), displayName: adults.find((a) => a.value === d.personId)?.label ?? t('tax.assessment.declarant', { rank: i + 1 }) }] : []));
  const at = (i: string, k: string) => `declarants.${i}.${k}`;
  const amount = (k: FieldKey, name: string = k): GridRow => ({
    key: name, label: label(k), error: (i) => errorOf(form, at(i, name)),
    cell: (a, c) => <Input inputMode="decimal" {...c} {...form.register(at(a.id, name) as Path<AssessmentValues>)} />,
  });
  return (
    <>
      {columns.map((c) => <SalaryAllowance key={c.id} form={form} index={Number(c.id)} />)}
      <DeclarantGrid label={t('tax.assessment_form.steps.declarants')} adults={columns} rows={[
        amount('grossSalaries'),
        { ...amount('overtimeExempt'), hint: t('tax.assessment_form.overtime_hint') },
        { key: 'deduction', label: label('deduction'),
          cell: (a, c) => <Controller control={form.control} name={at(a.id, 'deduction') as `declarants.${number}.deduction`} render={({ field }) => (
            <Segmented aria-labelledby={c['aria-labelledby']} value={field.value} onValueChange={field.onChange}
              options={[{ value: 'allowance', label: t('tax.assessment.deduction_allowance') }, { value: 'actual', label: t('tax.assessment.deduction_actual') }]} />
          )} /> },
        amount('deductionAmount'),
        { ...amount('withholding'), label: label('withholding_declarant') },
        amount('creditsAdvance'),
      ]} />
      <Folded label={t('tax.assessment_form.per')} hasError={columns.some((c) => PER_TYPED.some((k) => Boolean(errorOf(form, at(c.id, k)))))}>
        <DeclarantGrid label={t('tax.assessment_form.per')} adults={columns} rows={PER_TYPED.map((k) => ({
          ...amount(k), label: t(`tax.assessment.field.${k}`, { year: perLineYear(k, year) }),
          hint: k === 'perCeilingYear' ? t('tax.assessment_form.per_year_hint') : undefined,
        }))} />
      </Folded>
    </>
  );
}

function AmountsStep({ form, names, folded }: { form: Form; names: readonly FieldKey[]; folded?: readonly FieldKey[] }) {
  const { t } = useTranslation();
  const label = (key: FieldKey) => t(`tax.assessment.field.${key}`);
  return (
    <>
      {names.map((k) => <Amount key={k} form={form} name={k} label={label(k)} />)}
      {folded ? (
        <Folded label={t('tax.assessment_form.exempt')} hasError={folded.some((k) => Boolean(errorOf(form, k)))}>
          {folded.map((k) => <Amount key={k} form={form} name={k} label={label(k)} />)}
        </Folded>
      ) : null}
    </>
  );
}

function TaxStep({ form }: { form: Form }) {
  const { t } = useTranslation();
  return (
    <>
      <AmountsStep form={form} names={NOTICE_AMOUNTS.tax} />
      <Amount form={form} name="balance" label={t('tax.assessment_form.field.balance')} />
      <Controller control={form.control} name="balanceSign" render={({ field }) => (
        <Segmented aria-label={t('tax.assessment_form.field.balance_sign')} value={field.value} onValueChange={field.onChange}
          options={[{ value: 'due', label: t('tax.assessment_form.balance_due') }, { value: 'refund', label: t('tax.assessment_form.balance_refund') }]} />
      )} />
    </>
  );
}

function SourceStep({ form, stored, onUploading }: { form: Form; stored?: string | null; onUploading: (busy: boolean) => void }) {
  const { t } = useTranslation();
  const { $api, fetchClient } = useApi();
  const queryClient = useQueryClient();
  const documents = useDocuments('me', false, false);
  // The source the notice already has: shown even when the reader's list lacks it, and never removable.
  const current = $api.useQuery('get', DOCUMENT, { params: { path: { id: stored ?? '' } } }, { enabled: Boolean(stored) });
  const [uploaded, setUploaded] = useState<{ id: string; title: string } | null>(null);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [file, setFile] = useState('');
  const documentId = form.watch('documentId');
  const readers = form.watch('declarants').map((d) => d.personId).filter(Boolean);
  const preview = $api.useQuery('get', TAX_NOTICE_PREVIEW, { params: { path: { id: documentId }, query: { declarants: readers } } }, { enabled: Boolean(documentId) && readers.length === 2 });
  const gained = preview.data?.gainedReaders ?? [];
  const items = (documents.data?.items ?? []).map((d) => ({ value: d.id, label: d.title }));
  const kept = stored && !items.some((o) => o.value === stored) ? [{ value: stored, label: current.data?.document.title ?? t('tax.assessment_form.current_document') }] : [];
  const options = [...(stored ? [] : [{ value: '', label: t('tax.assessment_form.none') }]), ...kept, ...items, ...(uploaded && !items.some((o) => o.value === uploaded.id) ? [{ value: uploaded.id, label: uploaded.title }] : [])];

  const upload = async (picked: File) => {
    setProblem(null);
    setFile(picked.name);
    onUploading(true);
    try {
      const { id } = await uploadDocument(fetchClient, picked);
      setUploaded({ id, title: picked.name });
      form.setValue('documentId', id);
      void invalidateDocuments(queryClient);
    } catch (error) {
      setProblem(toProblem(error));
    } finally {
      onUploading(false);
    }
  };

  return (
    <>
      <Controller control={form.control} name="documentId" render={({ field }) => (
        <Field label={t('tax.assessment_form.field.document')} error={form.formState.errors.documentId?.message}>
          {(c) => <ComboboxField {...c} value={field.value} onValueChange={field.onChange} options={options}
            searchPlaceholder={t('form.search')} empty={t('form.no_result')} />}
        </Field>
      )} />
      {problem ? <p role="alert" className="text-sm text-destructive">{t(`documents_page.upload.${refusal(problem)}`, { name: file })}</p> : null}
      <Field label={t('tax.assessment_form.field.upload')}>
        {(c) => <UploadZone {...c} multiple={false} pick={t('documents_page.upload.pick_one')} hint={t('documents_page.upload.types')}
          onFiles={([picked]) => void upload(picked)} onRefused={(picked) => { setFile(picked.name); setProblem(UNSUPPORTED); }} />}
      </Field>
      {NOTICE_AMOUNTS.carried.map((k) => <Amount key={k} form={form} name={k} label={t(`tax.assessment.field.${k}`)} />)}
      {gained.length > 0 ? <p className="text-sm text-muted-foreground">{t('tax.assessment_form.also_reads', { names: new Intl.ListFormat('fr').format(gained.map((r) => r.name)) })}</p> : null}
    </>
  );
}

/** A notice in five steps and one request; with a `view` it corrects that notice (its version rides along). */
function AssessmentForm({ view: live, adults }: { view?: TaxAssessment; adults: { value: string; label: string }[] }) {
  const { t } = useTranslation();
  // The notice the form opened on: a refetch must not move the version the PUT carries away from the values shown.
  const [view] = useState(live);
  const [uploading, setUploading] = useState(false);
  const { session } = useSession();
  const navigate = useNavigate();
  const add = useAddTaxAssessment();
  const correct = useCorrectTaxAssessment();
  const form = useForm<AssessmentValues>({
    resolver: zodResolver(assessmentSchema(t, adults.length)) as unknown as Resolver<AssessmentValues>,
    defaultValues: view ? fromView(view, adults.length) : assessmentDefaults(session.person.id, Number(todayIso().slice(0, 4)) - 1),
  });
  const errors = useSubmitErrors(form.setError, assessmentPath);
  const [reveal, setReveal] = useState<{ step: string } | null>(null);
  const declarants = form.watch('declarants').filter((d) => d.personId).length;
  const pending = add.isPending || correct.isPending;

  const onError = (error: unknown) => {
    errors.onError(error);
    // The refusal lands on the last step: go back to the first step that holds a field it names.
    const steps = fieldErrorsOf(error).map((f) => assessmentPath(f.path)).filter((n) => n !== undefined).map(stepOf);
    const step = STEP_IDS.find((id) => steps.includes(id));
    setReveal(step ? { step } : null);
  };
  const onSuccess = (values: AssessmentValues) => {
    const { year } = values;
    void navigate({ to: '/tax', search: { year, ...(Number(year) >= FIRST_ESTIMATE_YEAR ? { tab: 'assessment' } : {}), ...(declarants === 1 ? { scope: 'me' as const } : {}) } });
  };

  const content: Record<StepId, ReactNode> = {
    household: <HouseholdStep form={form} adults={adults} />,
    declarants: <DeclarantStep form={form} adults={adults} />,
    income: <AmountsStep form={form} names={NOTICE_AMOUNTS.income} folded={NOTICE_AMOUNTS.incomeFolded} />,
    tax: <TaxStep form={form} />,
    source: <SourceStep form={form} stored={view?.documentId} onUploading={setUploading} />,
  };
  return (
    <FormPage crumbs={[{ label: t('tax.title'), to: '/tax', search: { year: form.watch('year') } }]}
      title={view ? t('tax.assessment_form.title_edit', { year: view.year }) : t('tax.assessment_form.title_new')}>
      {errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      <StepForm
        trigger={(fields) => form.trigger(fields as Path<AssessmentValues>[])}
        onSubmit={() => void form.handleSubmit((values) => {
          errors.clear();
          const body = toBody(values);
          const done = { onSuccess: () => onSuccess(values), onError };
          if (view) correct.mutate({ params: { path: { id: view.id } }, body: { ...body, version: view.version } }, done);
          else add.mutate({ body }, done);
        })()}
        submitLabel={t('tax.assessment_form.submit')} pending={pending || uploading} reveal={reveal}
        steps={STEP_IDS.map((id) => ({ id, title: t(`tax.assessment_form.steps.${id}`), fields: stepFields(id, declarants), content: content[id] }))}
      />
    </FormPage>
  );
}

function useAdults() {
  const { $api } = useApi();
  const people = $api.useQuery('get', PERSONS);
  return { loaded: people.data !== undefined, adults: (people.data ?? []).filter((p) => p.kind === 'adult').map((p) => ({ value: p.id, label: p.displayName })) };
}

export function NewAssessment() {
  const { adults } = useAdults();
  return <AssessmentForm adults={adults} />;
}

export function EditAssessment() {
  const { t } = useTranslation();
  const { id } = useParams({ from: '/tax/assessments/$id/edit' });
  const answer = useTaxAssessment(id);
  const { loaded, adults } = useAdults();
  const crumbs = [{ label: t('tax.title'), to: '/tax' as const, search: answer.data ? { year: String(answer.data.year) } : undefined }];
  if (answer.error) return <FormPage crumbs={crumbs} title={t('tax.title')}><ProblemMessage problem={present(toProblem(answer.error))} /></FormPage>;
  if (!answer.data || !loaded) return <><PageTrail crumbs={crumbs} /><PageSkeleton label={t('tax.assessment_form.loading')} actions={0} stats={0} /></>;
  return <AssessmentForm view={answer.data} adults={adults} />;
}
