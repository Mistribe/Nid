import { useTranslation } from 'react-i18next';
import type { components } from '@/api/schema';

type Issue = components['schemas']['ImportIssue'];

/** Errors then warnings, each with the server's path (global index in the section) and code, as plain text. */
export function IssueList({ issues }: { issues: readonly Issue[] }) {
  const { t } = useTranslation();
  if (issues.length === 0) return <p>{t('import.no_issues')}</p>;
  const errors = issues.filter((issue) => issue.severity === 'error');
  const warnings = issues.filter((issue) => issue.severity === 'warning');
  return (
    <div className="space-y-3">
      {errors.length > 0 ? <IssueGroup title={t('import.errors', { n: errors.length })} issues={errors} /> : null}
      {warnings.length > 0 ? <IssueGroup title={t('import.warnings', { n: warnings.length })} issues={warnings} /> : null}
    </div>
  );
}

function IssueGroup({ title, issues }: { title: string; issues: readonly Issue[] }) {
  const { t } = useTranslation();
  return (
    <section className="space-y-1">
      <h3 className="font-semibold">{title}</h3>
      <ul className="space-y-1">
        {issues.map((issue, i) => (
          <li key={`${issue.path}-${issue.code}-${i}`} className="flex flex-wrap gap-x-2">
            <code className="text-sm">{issue.path === '' ? t('import.no_path') : issue.path}</code>
            <span>{issue.message}</span>
            <span className="text-sm text-muted-foreground">{issue.code}</span>
          </li>
        ))}
      </ul>
    </section>
  );
}
