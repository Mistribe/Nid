import { getRouteApi, useNavigate } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { formatInstant } from '@/features/admin/format';
import { useDraft } from './draft';
import { FileStep } from './FileStep';
import { SendStep } from './SendStep';
import { effectiveState, useStateLabel } from './states';

const route = getRouteApi('/import/$id');

/** `/import/$id`: one session, rendered by its state on the server (web design §6.2). */
export function ImportSession() {
  const { t } = useTranslation();
  const { id } = route.useParams();
  const { $api } = useApi();
  const { file, setFile } = useDraft();
  const navigate = useNavigate();
  const label = useStateLabel();
  const session = $api.useQuery('get', '/api/v1/platform/imports/{id}', { params: { path: { id } } });

  const restart = () => {
    setFile(null);
    void navigate({ to: '/import' });
  };

  if (session.error) return <ProblemMessage problem={present(toProblem(session.error))} />;
  if (!session.data) {
    return (
      <div aria-busy="true" className="h-32 animate-pulse rounded-md bg-muted">
        <span className="sr-only">{t('import.loading')}</span>
      </div>
    );
  }

  const data = session.data;
  const state = effectiveState(data);

  const body = (() => {
    switch (state) {
      case 'expired':
        return (
          <>
            <p>{t('import.expired')}</p>
            <Button onClick={restart}>{t('import.restart')}</Button>
          </>
        );
      case 'cancelled':
        return (
          <>
            <p>{t('import.cancelled')}</p>
            <Button onClick={restart}>{t('import.new')}</Button>
          </>
        );
      case 'open':
        return file ? (
          <SendStep id={id} file={file} onValidated={() => void session.refetch()} onCancelled={restart} />
        ) : (
          <FileStep title={t('import.reselect_title')} hint={t('import.reselect_hint')} onReady={setFile} />
        );
      default:
        return null;
    }
  })();

  return (
    <section aria-labelledby="import-session-title" className="space-y-4">
      <h2 id="import-session-title" className="text-lg font-semibold">{t('import.session_title', { date: formatInstant(data.createdAt) })}</h2>
      <p>{t('import.session_state', { state: label(state) })}</p>
      {body}
    </section>
  );
}
