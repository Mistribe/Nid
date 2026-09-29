import { useParams } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { ENVELOPE } from './api';

// Task 4 fills the rest of this screen (figures, valuations, flows); for now it shows the envelope's name so
// that creating an envelope (Task 3) can land on it.
export function EnvelopeDetailPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { id } = useParams({ from: '/investments/$id' });
  const answer = $api.useQuery('get', ENVELOPE, { params: { path: { id } } });

  if (answer.error) return <ProblemMessage problem={present(toProblem(answer.error))} />;
  if (!answer.data) return <article aria-busy="true" aria-label={t('investments.loading')} className="h-40 animate-pulse rounded-md border bg-muted" />;

  return (
    <article className="space-y-4">
      <h1 className="text-2xl font-semibold">{answer.data.displayName}</h1>
    </article>
  );
}
