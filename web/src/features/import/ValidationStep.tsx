import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import type { components } from '@/api/schema';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { Field } from '@/components/form/Field';
import { CancelImport } from './CancelImport';
import { IssueList, SectionCounts } from './ReportView';
import { StepCard } from './StepCard';

type Session = components['schemas']['ImportSessionView'];

/**
 * Steps 5 and 6 of web design §6.2: the server's report, then Importer. The browser never decides (import-format.md
 * §11.9): it offers Importer only when the server says `validated`, and a conflict blocks as the server's commit does.
 */
export function ValidationStep({ session, onChanged, onCancelled }: { session: Session; onChanged: () => void; onCancelled: () => void }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const [problem, setProblem] = useState<Presentation | null>(null);
  const [notCommitted, setNotCommitted] = useState(false);

  const report = session.report ?? { state: session.state, sections: {}, issues: [] };
  const conflicts = Object.values(report.sections).some((counts) => counts.conflict > 0);
  const errors = report.issues.some((issue) => issue.severity === 'error');
  const canCommit = session.state === 'validated' && !conflicts && !errors;

  const conflictCount = Object.values(report.sections).reduce((sum, counts) => sum + counts.conflict, 0);
  const replacedCount = Object.values(report.sections).reduce((sum, counts) => sum + counts.replaced, 0);
  const onlyConflicts = report.issues.filter((i) => i.severity === 'error').every((i) => i.code === 'import.conflict');
  const [replace, setReplace] = useState(replacedCount > 0); // a report already validated with replace keeps the box on
  // Once checked, the choice stays offered even if a replace validation answers another error (§5): the user must
  // still be able to see and uncheck it. Otherwise, an initial conflict-only report offers it.
  const offerReplace = replace || (conflictCount > 0 && onlyConflicts);

  const validate = $api.useMutation('post', '/api/v1/platform/imports/{id}/validate', {
    onSuccess: () => {
      setProblem(null); // a later success clears an earlier failed attempt's message
      onChanged();
    },
    onError: (error) => { setProblem(present(toProblem(error))); onChanged(); },
  });
  const toggleReplace = (next: boolean) => {
    setReplace(next);
    validate.mutate(
      { params: { path: { id: session.id } }, body: { replace: next } },
      { onError: () => setReplace(!next) }, // a failed revalidation never leaves the box in the state it never reached
    );
  };

  const commit = $api.useMutation('post', '/api/v1/platform/imports/{id}/commit', {
    onSuccess: onChanged,
    onError: (error) => {
      const failure = toProblem(error);
      if (failure.code === 'import.not_committed') {
        setProblem(null);
        setNotCommitted(true);
      } else setProblem(present(failure));
      onChanged(); // the report now holds the reason
    },
  });

  return (
    <StepCard>
    <section aria-labelledby="import-report-title" className="space-y-4">
      <h2 id="import-report-title" className="text-lg font-semibold">{t('import.report_title')}</h2>
      {notCommitted ? <p role="alert">{t('import.not_committed')}</p> : null}
      {!notCommitted && conflicts && !offerReplace ? <p role="alert">{t('import.conflicts_block')}</p> : null}
      {!notCommitted && !conflicts && session.state === 'invalid' ? <p role="alert">{t('import.invalid')}</p> : null}
      {offerReplace ? (
        <div className="space-y-1 rounded-md border p-3">
          {conflictCount > 0 ? <p role="alert">{t('import.replace_found', { count: conflictCount })}</p> : null}
          <Field inline label={t('import.replace_label')}>
            {(c) => (
              <Checkbox
                {...c}
                checked={replace}
                aria-disabled={validate.isPending}
                onCheckedChange={(next) => { if (!validate.isPending) toggleReplace(next === true); }}
              />
            )}
          </Field>
          <p className="text-sm text-muted-foreground">{t('import.replace_note')}</p>
        </div>
      ) : null}
      <SectionCounts report={report} />
      <IssueList issues={report.issues} />
      {problem ? <ProblemMessage problem={problem} /> : null}
      <div className="flex flex-wrap gap-2">
        {canCommit ? (
          <Button onClick={() => commit.mutate({ params: { path: { id: session.id } }, body: { replace } })} disabled={commit.isPending}>
            {t('import.commit')}
          </Button>
        ) : null}
        <CancelImport id={session.id} label={t('import.fix')} onCancelled={onCancelled} />
      </div>
    </section>
    </StepCard>
  );
}
