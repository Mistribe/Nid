import { useTranslation } from 'react-i18next';
import { buttonVariants } from '@/components/ui/button';
import { loginUrl } from './session';

/** Only an http(s) address becomes a link: the value comes from the server, never from the address bar. */
const isWebAddress = (url: string | null): url is string => url !== null && /^https?:\/\//i.test(url);

/**
 * Nid's cookie is gone, the identity provider's session is not (identity-and-access/authentication.md §6):
 * "Se reconnecter" would succeed without a password. Say so, and point to the portal that can close it.
 */
export function SignedOutPage({ identityProvider }: { identityProvider: string | null }) {
  const { t } = useTranslation();
  return (
    <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center p-4">
      {/* Text never sits on the bare halo: the page is one content window, as in the shell. */}
      <div className="content-pane rounded-pane flex flex-col gap-4 p-6 shadow-card">
        <h1 className="text-2xl font-semibold">{t('signed_out.title')}</h1>
        <p>{t('signed_out.sso_open')}</p>
        {isWebAddress(identityProvider) ? (
          <p>
            <a href={identityProvider} className={buttonVariants({ variant: 'outline' })}>{t('signed_out.portal')}</a>
          </p>
        ) : null}
        <p>
          <a href={loginUrl({ pathname: '/', search: '' })} className={buttonVariants()}>{t('signed_out.sign_in_again')}</a>
        </p>
      </div>
    </main>
  );
}
