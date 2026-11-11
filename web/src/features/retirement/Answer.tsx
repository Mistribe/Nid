import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { RETIREMENT } from '@/api/retirement';
import { OverviewSkeleton } from '@/components/page/skeletons';
import { ProblemMessage } from '@/components/ProblemMessage';
import type { RetirementView } from './api';
import '@/locales/retirement';

export type Scope = 'household' | 'me';

/** The server's answer every tab reads (one request, shared through the query cache), or what to show instead. */
export function Answer({ scope, hero = false, children }: { scope: Scope; hero?: boolean; children: (data: RetirementView) => ReactNode }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const answer = $api.useQuery('get', RETIREMENT, { params: { query: { scope } } });
  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return <OverviewSkeleton label={t('retirement.loading')} hero={hero} />;
  return <>{children(answer.data)}</>;
}
