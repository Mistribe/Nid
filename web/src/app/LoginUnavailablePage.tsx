import { useTranslation } from 'react-i18next';
import { Wordmark } from '@/components/Logo';
import { buttonVariants } from '@/components/ui/button';

/**
 * The identity provider did not answer /auth/login (issue 363). « Réessayer » goes through login again, back to where
 * the person was going: /auth/login checks the path itself (ReturnUrl.IsSafe), so the address bar cannot send it away.
 */
export function LoginUnavailablePage({ returnUrl }: { returnUrl: string | null }) {
  const { t } = useTranslation();
  return (
    <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center p-4">
      {/* Text never sits on the bare halo: the page is one content window, as in the shell. */}
      <div className="content-pane rounded-pane flex flex-col gap-4 p-6 shadow-card">
        <Wordmark className="h-8" />
        <h1 className="text-2xl font-semibold">{t('login_unavailable.title')}</h1>
        <p>{t('login_unavailable.body')}</p>
        <p>
          <a href={`/auth/login?returnUrl=${encodeURIComponent(returnUrl ?? '/')}`} className={buttonVariants()}>{t('login_unavailable.retry')}</a>
        </p>
      </div>
    </main>
  );
}
