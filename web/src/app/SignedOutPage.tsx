import { useTranslation } from 'react-i18next';
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
    <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center gap-4 p-4">
      <h1 className="text-2xl font-semibold">{t('signed_out.title')}</h1>
      <p>{t('signed_out.sso_open')}</p>
      {isWebAddress(identityProvider) ? (
        <p>
          <a href={identityProvider} className="underline">{t('signed_out.portal')}</a>
        </p>
      ) : null}
      <p>
        <a href={loginUrl({ pathname: '/', search: '' })} className="underline">{t('signed_out.sign_in_again')}</a>
      </p>
    </main>
  );
}
