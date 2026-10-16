import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from '@tanstack/react-router';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { formatLocalDate } from '@/components/LocalDate';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { presentConflict, valueSchema, type ValueFormValues } from '../investments/entries/entries';
import { Field } from '../subscriptions/edits/Field';
import { Reload } from '../subscriptions/edits/Reload';
import { useSubmitErrors } from '../subscriptions/edits/useSubmitErrors';
import { PERSONS } from '../subscriptions/api';
import { APPROVE, PROPOSAL, REJECT, invalidateProposals, usePending, type ProposalView } from './api';
import { groupBySource, kindOf, originText } from './kinds';
import { announce, focusHeading, focusNextHeading, useAnnouncement } from './announce';

const input = 'rounded-md border px-3 py-2';
const PATHS: Record<string, keyof ValueFormValues> = {
  'payload.asOf': 'asOf', 'payload.amount.amount': 'amount', 'payload.amount.currency': 'amount', 'payload.note': 'note',
  'editedPayload': 'amount', // ProposalUseCases emits it when the edited payload is not an object
};

/** Spec 4C2 §4: one proposal — read, edit, decide, move on. */
export function ProposalDetailPage() {
  const { id } = useParams({ from: '/proposals/$id' });
  const message = useAnnouncement(id);
  return (
    <div className="space-y-4">
      <p role="status">{message}</p>
      <Proposal id={id} />
    </div>
  );
}

function Proposal({ id }: { id: string }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', PROPOSAL, { params: { path: { id } } });
  const pending = usePending();

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return null;
  const { proposal: p, preview, canDecide } = answer.data;
  const kind = kindOf(p.kind);
  const order = groupBySource(pending ?? []).flatMap((g) => g.items);
  const at = order.findIndex((i) => i.id === p.id);
  const next = order[at + 1] ?? order.find((i) => i.id !== p.id) ?? null;
  const unsupported = p.warnings.some((w) => w.code === 'payload_version_unsupported');
  const link = kind?.link(p);

  return (
    <div className="space-y-4">
      <Link to="/proposals" className="underline">{t('proposal.back')}</Link>
      <h1 tabIndex={-1} ref={focusHeading} className="text-2xl font-semibold">{kind ? t(kind.label) : t('proposal.unknown_kind', { kind: p.kind })}</h1>
      <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1">
        <dt>{t('proposal.target')}</dt>
        <dd>{link ? <Link to={link.to} params={link.params} className="underline">{p.targetName ?? t('proposals.unknown_target')}</Link> : (p.targetName ?? t('proposals.unknown_target'))}</dd>
        {p.sourceLabel ? (<><dt>{t('proposal.statement')}</dt><dd>{p.sourceLabel}</dd></>) : null}
      </dl>
      <p className="text-sm text-muted-foreground">{t('proposals.proposed_on', { origin: originText(t, p.createdVia), date: formatLocalDate(p.createdAt.slice(0, 10)) })}</p>
      {p.warnings.some((w) => w.code !== 'payload_version_unsupported') ? (
        <section aria-label={t('proposal.warnings')} className="rounded-md border p-3">
          <ul>{p.warnings.filter((w) => w.code !== 'payload_version_unsupported').map((w, i) => <li key={i}>{w.code === 'apply_failed' ? t('proposal.apply_failed', { message: w.message }) : w.message}</li>)}</ul>
        </section>
      ) : null}
      {kind && preview ? <kind.Preview preview={preview} /> : null}
      {!kind ? <pre className="whitespace-pre-wrap rounded-md border p-2 text-sm">{JSON.stringify(p.editedPayload ?? p.payload)}</pre> : null}
      {p.status !== 'pending' ? <Decided p={p} target={p.status === 'approved' ? link ?? null : null} /> : !canDecide ? <p>{t('proposal.read_only')}</p>
        : <Decide key={`${p.id}:${p.version}`} p={p} next={next} canApprove={!!kind && !unsupported} />}
      {next ? <Link to="/proposals/$id" params={{ id: next.id }} className="underline">{t('proposal.next')}</Link> : null}
      {unsupported ? <p>{t('proposal.unsupported')}</p> : null}
    </div>
  );
}

function Decided({ p, target }: { p: ProposalView; target: ReturnType<NonNullable<ReturnType<typeof kindOf>>['link']> | null }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const people = $api.useQuery('get', PERSONS).data;
  const by = people?.find((x) => x.id === p.decidedByPersonId)?.displayName;
  return (
    <div className="space-y-1">
      <p>{t(`proposals.decided.${p.status === 'rejected' ? 'rejected' : 'approved'}`, { date: formatLocalDate((p.decidedAt ?? p.createdAt).slice(0, 10)) })}</p>
      {by ? <p>{t('proposal.decided_by', { name: by })}</p> : null}
      {p.decisionNote ? <p>{t('proposal.decision_note', { note: p.decisionNote })}</p> : null}
      {target ? <Link to={target.to} params={target.params} className="underline">{t('proposal.open_target')}</Link> : null}
    </div>
  );
}

function Decide({ p, next, canApprove }: { p: ProposalView; next: ProposalView | null; canApprove: boolean }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const kind = kindOf(p.kind);
  const original = p.editedPayload ?? p.payload;
  const form = useForm<ValueFormValues>({ resolver: zodResolver(valueSchema(t)), defaultValues: kind?.toForm(original) });
  const errors = useSubmitErrors(form.setError, (path) => PATHS[path]);
  const [rejecting, setRejecting] = useState(false);
  const [note, setNote] = useState('');
  const after = (done: string, approved = false) => {
    announce(done);
    // An approval writes through the kind's handler, which may touch any screen's data.
    void (approved ? queryClient.invalidateQueries() : invalidateProposals(queryClient));
    if (next) focusNextHeading();
    void (next ? navigate({ to: '/proposals/$id', params: { id: next.id } }) : navigate({ to: '/proposals' }));
  };
  const approve = $api.useMutation('post', APPROVE);
  const reject = $api.useMutation('post', REJECT);
  const conflict = [approve.error, reject.error].find((e) => e && toProblem(e).status === 409);

  const submit = form.handleSubmit((values) => {
    errors.clear();
    const edited = kind?.toPayload(values, original) ?? null;
    approve.mutate({ params: { path: { id: p.id } }, body: { version: p.version, ...(edited ? { editedPayload: edited } : {}) } }, {
      onSuccess: () => after(t('proposal.approved'), true),
      onError: (e) => { if (toProblem(e).status !== 409) errors.onError(e); },
    });
  });

  const currency = (original as { amount?: { currency?: string } }).amount?.currency ?? '';
  const e = form.formState.errors;
  return (
    <div className="space-y-3">
      {conflict ? (
        <>
          <ProblemMessage problem={{ ...presentConflict(t, conflict), detail: t('proposal.conflict') }} />
          <Reload onReload={() => invalidateProposals(queryClient)} />
        </>
      ) : errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      {canApprove ? (
        <form noValidate className="space-y-3" aria-label={t('proposal.edit.title')} onSubmit={(ev) => void submit(ev)}>
          <Field label={t('proposal.edit.as_of')} error={e.asOf?.message}>{(c) => <input type="date" {...c} {...form.register('asOf')} className={input} />}</Field>
          <Field label={t('proposal.edit.amount', { currency })} error={e.amount?.message}>{(c) => <input inputMode="decimal" {...c} {...form.register('amount')} className={input} />}</Field>
          <Field label={t('proposal.edit.note')} error={e.note?.message}>{(c) => <input {...c} {...form.register('note')} className={input} />}</Field>
          <Button type="submit" disabled={approve.isPending}>{t('proposal.approve')}</Button>
        </form>
      ) : null}
      {rejecting ? (
        <form className="space-y-2" onSubmit={(ev) => { ev.preventDefault(); errors.clear(); reject.mutate(
          { params: { path: { id: p.id } }, body: { version: p.version, ...(note.trim() ? { note: note.trim() } : {}) } },
          { onSuccess: () => after(t('proposal.rejected')), onError: (er) => { if (toProblem(er).status !== 409) errors.onError(er); } }); }}>
          <Field label={t('proposal.reject_note')}>{(c) => <textarea {...c} autoFocus value={note} onChange={(ev) => setNote(ev.target.value)} className={input} />}</Field>
          <div className="flex gap-2">
            <Button type="submit" disabled={reject.isPending}>{t('proposal.reject_confirm')}</Button>
            <Button type="button" variant="ghost" onClick={() => setRejecting(false)}>{t('proposal.cancel')}</Button>
          </div>
        </form>
      ) : <Button type="button" variant="outline" onClick={() => setRejecting(true)}>{t('proposal.reject')}</Button>}
    </div>
  );
}
