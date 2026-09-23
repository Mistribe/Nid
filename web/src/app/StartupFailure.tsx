import { useTranslation } from 'react-i18next';
import type { Presentation } from '@/api/errors';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';

/** What the page shows when the session or the capabilities cannot be read: the problem, and a way to try again. */
export function StartupFailure({ problem, onRetry }: { problem: Presentation; onRetry: () => void }) {
  const { t } = useTranslation();
  return (
    <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center gap-4 p-4">
      <h1 className="text-2xl font-semibold">{t('startup.title')}</h1>
      <ProblemMessage problem={problem} />
      <Button className="self-start" onClick={onRetry}>{t('startup.retry')}</Button>
    </main>
  );
}
