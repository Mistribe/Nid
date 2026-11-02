import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';
import { Button } from '@/components/ui/button';
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible';
import { formatInstant } from '@/features/admin/format';
import { downloadReport } from './report';
import { IssueList, SectionCounts } from './ReportView';
import { StepCard } from './StepCard';
import '@/locales/import';

type Session = components['schemas']['ImportSessionView'];

/** Step 7 of web design §6.2: what was written, and the report to keep. */
export function FinalReport({ session, onNew }: { session: Session; onNew: () => void }) {
  const { t } = useTranslation();
  const report = session.report ?? { state: session.state, sections: {}, issues: [] };
  const envelopes = report.sections.envelopes;
  const madeEnvelopes = envelopes != null && envelopes.created + (envelopes.replaced ?? 0) > 0;
  return (
    <StepCard>
    <section aria-labelledby="import-done-title" className="space-y-4">
      <h2 id="import-done-title" className="text-lg font-semibold">{t('import.done_title')}</h2>
      <SectionCounts report={report} />
      <IssueList issues={report.issues} />
      {session.audit ? (
        <section aria-labelledby="import-audit-title" className="space-y-1">
          <h3 id="import-audit-title" className="font-semibold">{t('import.audit_title')}</h3>
          <p>{t('import.audit_committed', { date: formatInstant(session.audit.committedAt) })}</p>
          <div>
            {t('import.audit_hash')} : <code>{session.audit.contentHash.slice(0, 12)}…</code>{' '}
            <Collapsible className="inline">
              <CollapsibleTrigger className="cursor-pointer underline">{t('import.audit_hash_show')}</CollapsibleTrigger>
              <CollapsibleContent><code className="break-all">{session.audit.contentHash}</code></CollapsibleContent>
            </Collapsible>
          </div>
          <p>{t('import.audit_format', { format: session.audit.format, version: session.audit.version })}</p>
          <p>{t(session.audit.replace ? 'import.audit_replace_yes' : 'import.audit_replace_no')}</p>
        </section>
      ) : null}
      <div className="flex flex-wrap gap-2">
        <Button onClick={() => downloadReport(session)}>{t('import.download')}</Button>
        <Button asChild variant="outline">
          <Link to="/subscriptions">{t('import.see_subscriptions')}</Link>
        </Button>
        {madeEnvelopes ? (
          <Button asChild variant="outline">
            <Link to="/investments">{t('import.see_investments')}</Link>
          </Button>
        ) : null}
        <Button variant="ghost" onClick={onNew}>{t('import.new')}</Button>
      </div>
    </section>
    </StepCard>
  );
}
