import { useTranslation } from 'react-i18next';
import type { Presentation } from '@/api/errors';

export function ProblemMessage({ problem }: { problem: Presentation }) {
  const { t } = useTranslation();
  return (
    <div role="alert" className="space-y-1 rounded-md border border-destructive p-3">
      <p>{t(`problem.${problem.kind}`)}</p>
      {problem.detail ? <p className="text-sm text-muted-foreground">{problem.detail}</p> : null}
      {problem.requestId ? <p className="text-sm text-muted-foreground">{t('problem.request_id', { id: problem.requestId })}</p> : null}
    </div>
  );
}
