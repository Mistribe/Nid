import { useTranslation } from 'react-i18next';
import { Wordmark } from '@/components/Logo';
import { buttonVariants } from '@/components/ui/button';
import { loginUrl } from './session';

/**
 * The identity provider authenticated the person, Nid refused them: not in the access group, the household full, or an
 * email another adult holds (authentication.md §5).
 * The email comes from the address bar, so it is shown as text only — React escapes it.
 */
export function LoginRefusedPage({ email }: { email: string | null }) {
  const { t } = useTranslation();
  return (
    <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center p-4">
      {/* Text never sits on the bare halo: the page is one content window, as in the shell. */}
      <div className="content-pane rounded-pane flex flex-col gap-4 p-6 shadow-card">
        <Wordmark className="h-8" />
        <h1 className="text-2xl font-semibold">{t('login_refused.title')}</h1>
        <p>{t('login_refused.ask_admin')}</p>
        <p>{email ? t('login_refused.email', { email }) : t('login_refused.no_email')}</p>
        <p>
          <a href={loginUrl({ pathname: '/', search: '' })} className={buttonVariants()}>{t('login_refused.sign_in_again')}</a>
        </p>
      </div>
    </main>
  );
}
