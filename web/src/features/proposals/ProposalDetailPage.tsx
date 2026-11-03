import { zodResolver } from '@hookform/resolvers/zod';
import { useQueryClient } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from '@tanstack/react-router';
import { AlertTriangle } from 'lucide-react';
import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { useForm, type FieldValues } from 'react-hook-form';
import { useTranslation } from 'react-i18next';
import type { TFunction } from 'i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { knownConflictMessage } from '../subscriptions/fieldErrors';
import { formatInstantDay } from '@/features/admin/format';
import { ProblemMessage } from '@/components/ProblemMessage';
import { PageHeader } from '@/components/page/PageHeader';
import { Button, buttonVariants } from '@/components/ui/button';
import { Textarea } from '@/components/ui/textarea';
import { presentConflict } from '../investments/entries/entries';
import { Field } from '@/components/form/Field';
import { Reload } from '../subscriptions/edits/Reload';
import { useSubmitErrors } from '../subscriptions/edits/useSubmitErrors';
import { PERSONS } from '../subscriptions/api';
import { APPROVE, PROPOSAL, REJECT, invalidateProposals, usePending, type ProposalView } from './api';
import type { KindUi, LinkTarget } from './kindParts';
import { groupBySource, kindOf, originText } from './kinds';
import { RoutePayload, isRouteKind, routeElement, routeLabel, routeLink, useNames } from './routeKind';
import { announce, focusHeading, focusNextHeading, useAnnouncement } from './announce';
import '@/locales/proposals';
import '@/locales/forms';

/** A decision that lost a race: reload (any other 409, such as a reference already recorded, is the payload's own refusal). */
const RACES = ['core.proposal_conflict', 'core.proposal_decided', 'core.fact_conflict', 'income.phase_conflict'];
const isRace = (error: unknown) => RACES.includes(toProblem(error).code ?? '');

/** The payload's own refusal in French, naming the movement when the 409 points at one; never empty. */
function refusalText(t: TFunction, error: unknown): string {
  const problem = toProblem(error);
  const index = problem.errors?.map((e) => /^payload\.movements\[(\d+)\]\.ref$/.exec(e.path)?.[1]).find((i) => i !== undefined);
  if (problem.code === 'core.external_ref_taken' && index !== undefined) return t('field_error.core_external_ref_taken', { context: 'movement', n: Number(index) + 1 });
  return knownConflictMessage(t, problem.code) ?? t('problem.unexpected');
}

/** An `apply_failed` warning reads `<code>: <server sentence>`: its code in French when known, never the server's English. */
function applyFailedText(t: TFunction, message: string): string {
  const reason = knownConflictMessage(t, message.split(':')[0]?.trim());
  return reason ? t('proposal.apply_failed', { message: reason }) : t('proposal.apply_failed', { context: 'unknown' });
}

/** Spec 4C2 §4: one proposal — read, edit, decide, move on. */
export function ProposalDetailPage() {
  const { id } = useParams({ from: '/proposals/$id' });
  const message = useAnnouncement(id);
  return (
    <div className="space-y-6">
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
  const names = useNames(answer.data ? [answer.data.proposal.kind] : []);
  const heading = useRef<HTMLHeadingElement>(null);
  const shown = answer.data?.proposal.id;
  // Keyed on the shown proposal, not the h1's mount: a cached next proposal keeps the same h1.
  useEffect(() => focusHeading(heading.current), [shown]);

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return null;
  const { proposal: p, preview, canDecide } = answer.data;
  const kind = kindOf(p.kind);
  const order = groupBySource(pending ?? []).flatMap((g) => g.items);
  const at = order.findIndex((i) => i.id === p.id);
  const next = order[at + 1] ?? order.find((i) => i.id !== p.id) ?? null;
  // already_recorded is told by the table's state column and the checkbox hint; schedule_mismatch by the comparison — both in French.
  const hidden = ['payload_version_unsupported', 'already_recorded', 'schedule_mismatch'];
  const unsupported = p.warnings.some((w) => w.code === 'payload_version_unsupported');
  const warnings = p.warnings.filter((w) => !hidden.includes(w.code));
  // Spec 4D3: a kind without its own screen replays a web write, approved as proposed.
  const route = !kind && !unsupported && isRouteKind(p.kind);
  const link = kind ? kind.link(p) : route ? routeLink(p) : undefined;
  const targetName = route ? routeElement(t, p, names) : p.targetName ?? t(p.targetItemId ? 'proposals.unknown_target' : 'proposal.route.new');

  const title = kind ? t(kind.label) : route ? routeLabel(t, p.kind) : t('proposal.unknown_kind', { kind: p.kind });
  const head = (actions: ReactNode) => (
    <PageHeader crumbs={[{ label: t('proposal.back'), to: '/proposals' }]} title={title} titleRef={heading} actions={actions}
      meta={p.sourceLabel ? <span className="text-sm text-muted-foreground">{p.sourceLabel}</span> : undefined} />
  );
  const body = (
    <>
      <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1">
        {targetName ? (<>
          <dt className="text-muted-foreground">{t('proposal.target')}</dt>
          <dd>{link && p.targetItemId ? <Link to={link.to} params={link.params} className="underline">{targetName}</Link> : targetName}</dd>
        </>) : null}
        {p.sourceLabel ? (<><dt className="text-muted-foreground">{t('proposal.statement')}</dt><dd>{p.sourceLabel}</dd></>) : null}
      </dl>
      <p className="text-sm text-muted-foreground">{t('proposals.proposed_on', { origin: originText(t, p.createdVia), date: formatInstantDay(p.createdAt) })}</p>
      {warnings.length > 0 ? (
        <section aria-label={t('proposal.warnings')} className="flex gap-2 rounded-md border border-status-warning/60 bg-status-warning/10 p-3 text-sm">
          <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0 text-status-warning" />
          <ul>{warnings.map((w, i) => <li key={i}>{w.code === 'apply_failed' ? applyFailedText(t, w.message) : w.message}</li>)}</ul>
        </section>
      ) : null}
      {kind && preview ? <kind.Preview preview={preview} original={p.editedPayload ?? p.payload} /> : null}
      {route ? <RoutePayload p={p} /> : !kind ? <pre className="whitespace-pre-wrap rounded-md border p-2 text-sm">{JSON.stringify(p.editedPayload ?? p.payload)}</pre> : null}
    </>
  );
  const tail = (
    <>
      {next ? <Link to="/proposals/$id" params={{ id: next.id }} className={buttonVariants({ variant: 'outline', size: 'sm' }) + ' self-start'}>{t('proposal.next')}</Link> : null}
      {unsupported ? <p>{t('proposal.unsupported')}</p> : null}
    </>
  );

  return (
    <div className="flex flex-col gap-6">
      {p.status !== 'pending' ? <>{head(null)}{body}<Decided p={p} target={p.status === 'approved' ? link ?? null : null} /></>
        : !canDecide ? <>{head(null)}{body}<p>{t('proposal.read_only')}</p></>
        : <Decide key={`${p.id}:${p.version}`} p={p} preview={preview} next={next} kind={kind && !unsupported ? kind : null} asProposed={route} head={head} body={body} />}
      {tail}
    </div>
  );
}

function Decided({ p, target }: { p: ProposalView; target: LinkTarget | null }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const people = $api.useQuery('get', PERSONS).data;
  const by = people?.find((x) => x.id === p.decidedByPersonId)?.displayName;
  return (
    <div className="space-y-1">
      <p>{t(`proposals.decided.${p.status === 'rejected' ? 'rejected' : 'approved'}`, { date: formatInstantDay(p.decidedAt ?? p.createdAt) })}</p>
      {by ? <p>{t('proposal.decided_by', { name: by })}</p> : null}
      {p.decisionNote ? <p>{t('proposal.decision_note', { note: p.decisionNote })}</p> : null}
      {target ? <Link to={target.to} params={target.params} className="underline">{t('proposal.open_target')}</Link> : null}
    </div>
  );
}

/** Owns the decision: its buttons sit in the page header (`head`), under the document (`body`), the approval's form below. */
/** `asProposed`: a proposal with no form of its own (spec 4D3), approved as proposed. */
function Decide({ p, preview, next, kind, asProposed, head, body }: {
  p: ProposalView; preview: unknown; next: ProposalView | null; kind: KindUi | null; asProposed: boolean;
  head: (actions: ReactNode) => ReactNode; body: ReactNode;
}) {
  const formId = useId();
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const original = p.editedPayload ?? p.payload;
  // The kind's own form (spec 4D1 §5); a proposal that can only be rejected mounts an empty one (hooks run unconditionally).
  const form = useForm<FieldValues>({
    resolver: kind ? zodResolver(kind.schema(t, original)) : undefined,
    defaultValues: kind?.toForm(original, preview),
  });
  const errors = useSubmitErrors(form.setError, (path) => kind?.field(path), (field) => kind?.message?.(t, field));
  // A 409 that is no race: the payload's own refusal (a reference already recorded), told in French.
  const [refusal, setRefusal] = useState<string | null>(null);
  const nothing = !!kind?.nothing?.(form.watch(), original);
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
  const conflict = [approve.error, reject.error].find((e) => e && isRace(e));

  const send = (edited: unknown) => {
    errors.clear();
    setRefusal(null);
    approve.mutate({ params: { path: { id: p.id } }, body: { version: p.version, ...(edited ? { editedPayload: edited } : {}) } }, {
      onSuccess: () => after(t('proposal.approved'), true),
      onError: (e) => {
        if (isRace(e)) return;
        const problem = toProblem(e);
        const own = kind?.refusal?.(t, problem);
        if (own) setRefusal(own);
        else if (problem.status === 409) setRefusal(refusalText(t, e));
        else errors.onError(e);
      },
    });
  };
  const submit = form.handleSubmit((values) => {
    if (kind) send(kind.toPayload(values, original, preview));
  });

  const actions = (
    <>
      {kind ? <Button type="submit" form={formId} disabled={approve.isPending || nothing} aria-describedby={nothing ? 'nothing-to-record' : undefined}>{t('proposal.approve')}</Button>
        : asProposed ? <Button type="button" disabled={approve.isPending} onClick={() => send(null)}>{t('proposal.approve')}</Button> : null}
      {rejecting ? null : <Button type="button" variant="outline" onClick={() => setRejecting(true)}>{t('proposal.reject')}</Button>}
    </>
  );

  return (
    <>
      {head(actions)}
      {body}
      {conflict ? (
        <>
          <ProblemMessage problem={{ ...presentConflict(t, conflict), detail: t('proposal.conflict') }} />
          <Reload onReload={() => invalidateProposals(queryClient)} />
        </>
      ) : refusal !== null ? <div role="alert" className="rounded-md border border-destructive p-3">{refusal}</div>
        : errors.problem ? <ProblemMessage problem={errors.problem} /> : null}
      {kind ? (
        <form id={formId} noValidate className="max-w-xl space-y-3" aria-label={t('proposal.edit.title')} onSubmit={(ev) => void submit(ev)}>
          <kind.Fields form={form} original={original} preview={preview} />
          {nothing ? <p id="nothing-to-record">{t('proposal.nothing_to_record')}</p> : null}
        </form>
      ) : null}
      {rejecting ? (
        <form className="max-w-xl space-y-2" onSubmit={(ev) => { ev.preventDefault(); errors.clear(); setRefusal(null); reject.mutate(
          { params: { path: { id: p.id } }, body: { version: p.version, ...(note.trim() ? { note: note.trim() } : {}) } },
          { onSuccess: () => after(t('proposal.rejected')), onError: (er) => {
            if (isRace(er)) return;
            if (toProblem(er).status === 409) setRefusal(refusalText(t, er)); else errors.onError(er);
          } }); }}>
          <Field label={t('proposal.reject_note')}>{(c) => <Textarea {...c} autoFocus value={note} onChange={(ev) => setNote(ev.target.value)} />}</Field>
          <div className="flex gap-2">
            <Button type="submit" disabled={reject.isPending}>{t('proposal.reject_confirm')}</Button>
            <Button type="button" variant="ghost" onClick={() => setRejecting(false)}>{t('proposal.cancel')}</Button>
          </div>
        </form>
      ) : null}
    </>
  );
}
