import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { Button } from '@/components/ui/button';
import { downloadReport } from './report';
import { IssueList, SectionCounts } from './ReportView';

type Session = components['schemas']['ImportSessionView'];

/** Step 7 of web design §6.2: what was written, and the report to keep. */
export function FinalReport({ session, onNew }: { session: Session; onNew: () => void }) {
  const { t } = useTranslation();
  const report = session.report ?? { state: session.state, sections: {}, issues: [] };
  return (
    <section aria-labelledby="import-done-title" className="space-y-4">
      <h2 id="import-done-title" className="text-lg font-semibold">{t('import.done_title')}</h2>
      <SectionCounts report={report} />
      <IssueList issues={report.issues} />
      <div className="flex flex-wrap gap-2">
        <Button onClick={() => downloadReport(session)}>{t('import.download')}</Button>
        <Button asChild variant="outline">
          <Link to="/subscriptions">{t('import.see_subscriptions')}</Link>
        </Button>
        <Button variant="ghost" onClick={onNew}>{t('import.new')}</Button>
      </div>
    </section>
  );
}
