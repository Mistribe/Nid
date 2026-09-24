import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import type { components } from '@/api/schema';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { CancelImport } from './CancelImport';
import { IssueList, SectionCounts } from './ReportView';

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
    <section aria-labelledby="import-report-title" className="space-y-4">
      <h2 id="import-report-title" className="text-lg font-semibold">{t('import.report_title')}</h2>
      {notCommitted ? <p role="alert">{t('import.not_committed')}</p> : null}
      {!notCommitted && conflicts ? <p role="alert">{t('import.conflicts_block')}</p> : null}
      {!notCommitted && !conflicts && session.state === 'invalid' ? <p role="alert">{t('import.invalid')}</p> : null}
      <SectionCounts report={report} />
      <IssueList issues={report.issues} />
      {problem ? <ProblemMessage problem={problem} /> : null}
      <div className="flex flex-wrap gap-2">
        {canCommit ? (
          <Button onClick={() => commit.mutate({ params: { path: { id: session.id } } })} disabled={commit.isPending}>
            {t('import.commit')}
          </Button>
        ) : null}
        <CancelImport id={session.id} label={t('import.fix')} onCancelled={onCancelled} />
      </div>
    </section>
  );
}
