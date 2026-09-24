import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Problem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { CancelImport } from './CancelImport';
import type { ReadyFile } from './draft';
import type { PreparedBatch } from './prepare';
import { IssueList } from './ReportView';
import { sendBatches, type BatchView, type PutResult } from './sender';

type Phase =
  | { kind: 'sending' }
  | { kind: 'retrying' }
  | { kind: 'paused' }
  | { kind: 'failed'; problem: Problem }
  | { kind: 'refused'; refused: BatchView[] }
  | { kind: 'different_file' }
  | { kind: 'validating' }
  | { kind: 'stale' };

/**
 * Steps 4 and 5 of web design §6.2: every batch, in order, with progress, pause, retries and cancel; then the full
 * validation. A resumed import sends every batch again: the server replays the ones it already holds (§11.6).
 */
export function SendStep({ id, file, onValidated, onCancelled }: {
  id: string; file: ReadyFile; onValidated: () => void; onCancelled: () => void;
}) {
  const { t } = useTranslation();
  const { fetchClient } = useApi();
  const [sent, setSent] = useState(0);
  const [phase, setPhase] = useState<Phase>({ kind: 'sending' });
  const next = useRef(0);
  const refused = useRef<BatchView[]>([]);
  const pauseRequested = useRef(false);
  const started = useRef(false);
  const total = file.batches.length;

  const put = useCallback(async (batch: PreparedBatch): Promise<PutResult> => {
    try {
      const { data, error } = await fetchClient.PUT('/api/v1/platform/imports/{id}/batches/{section}/{n}', {
        params: { path: { id, section: batch.section, n: batch.index } },
        body: batch.body,
        bodySerializer: (body) => body as string, // the prepared text, byte for byte: its numbers are as written
      });
      return data ? { ok: true, view: data } : { ok: false, problem: toProblem(error) };
    } catch {
      return { ok: false, problem: { status: 0 } };
    }
  }, [fetchClient, id]);

  const validate = useCallback(async () => {
    setPhase({ kind: 'validating' });
    try {
      const { data, error } = await fetchClient.POST('/api/v1/platform/imports/{id}/validate', { params: { path: { id } } });
      if (data) {
        onValidated();
        return;
      }
      const problem = toProblem(error);
      // A batch arrived while the server validated (import-format.md §12bis): its report would be stale.
      setPhase(problem.code === 'import.state' ? { kind: 'stale' } : { kind: 'failed', problem });
    } catch {
      setPhase({ kind: 'failed', problem: { status: 0 } });
    }
  }, [fetchClient, id, onValidated]);

  const run = useCallback(async () => {
    pauseRequested.current = false;
    setPhase({ kind: 'sending' });
    const outcome = await sendBatches({
      batches: file.batches,
      start: next.current,
      put,
      onSent: (count) => {
        next.current = count;
        setSent(count);
        setPhase((current) => (current.kind === 'retrying' ? { kind: 'sending' } : current));
      },
      onRetry: () => setPhase({ kind: 'retrying' }),
      shouldPause: () => pauseRequested.current,
    });
    refused.current = [...refused.current, ...outcome.refused];
    if (outcome.kind === 'paused') setPhase({ kind: 'paused' });
    else if (outcome.kind === 'failed')
      setPhase(outcome.problem.code === 'import.slot_taken' ? { kind: 'different_file' } : { kind: 'failed', problem: outcome.problem });
    else if (refused.current.length > 0) setPhase({ kind: 'refused', refused: refused.current });
    else await validate();
  }, [file.batches, put, validate]);

  useEffect(() => {
    if (started.current) return; // StrictMode runs effects twice; refs survive, so the file is sent once
    started.current = true;
    void run();
  }, [run]);

  const sending = phase.kind === 'sending' || phase.kind === 'retrying';
  const cancel = (label: string) => (
    <CancelImport id={id} label={label} onCancelled={onCancelled} beforeCancel={() => { pauseRequested.current = true; }} />
  );

  return (
    <section aria-labelledby="import-send-title" className="space-y-4">
      <h2 id="import-send-title" className="text-lg font-semibold">{t('import.send_title')}</h2>
      <progress value={sent} max={total} aria-label={t('import.progress_label')} className="w-full" />
      <p role="status" aria-live="polite">{t('import.progress', { sent, total })}</p>

      {phase.kind === 'retrying' ? <p>{t('import.retrying')}</p> : null}
      {phase.kind === 'paused' ? <p>{t('import.paused')}</p> : null}
      {phase.kind === 'validating' ? (
        <div aria-busy="true" className="h-16 animate-pulse rounded-md bg-muted">
          <span className="sr-only">{t('import.validating')}</span>
        </div>
      ) : null}
      {phase.kind === 'stale' ? <p role="alert">{t('import.validate_stale')}</p> : null}
      {phase.kind === 'different_file' ? <p role="alert">{t('import.different_file')}</p> : null}
      {phase.kind === 'failed' && (phase.problem.status === 0 || phase.problem.status >= 500) ? <p role="alert">{t('import.send_failed')}</p> : null}
      {phase.kind === 'failed' && phase.problem.status > 0 && phase.problem.status < 500 ? <ProblemMessage problem={present(phase.problem)} /> : null}
      {phase.kind === 'refused' ? (
        <div className="space-y-3">
          <p role="alert">{t('import.refused')}</p>
          <IssueList issues={phase.refused.flatMap((batch) => batch.issues)} />
        </div>
      ) : null}

      <div className="flex flex-wrap gap-2">
        {sending ? <Button variant="outline" onClick={() => { pauseRequested.current = true; }}>{t('import.pause')}</Button> : null}
        {phase.kind === 'paused' ? <Button onClick={() => void run()}>{t('import.continue')}</Button> : null}
        {phase.kind === 'failed' && (phase.problem.status === 0 || phase.problem.status >= 500) ? (
          <Button onClick={() => void (next.current === total ? validate() : run())}>{t('import.retry')}</Button>
        ) : null}
        {phase.kind === 'stale' ? <Button onClick={() => void validate()}>{t('import.validate_again')}</Button> : null}
        {phase.kind === 'refused' || phase.kind === 'different_file' || (phase.kind === 'failed' && phase.problem.status > 0 && phase.problem.status < 500)
          ? cancel(t('import.fix'))
          : cancel(t('import.cancel'))}
      </div>
    </section>
  );
}
