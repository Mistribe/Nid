import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { expiresSoon, TOKENS } from './tokens';

/** Spec 4A D5: no email channel, so a token about to expire is said here, in the shell. */
export function TokenBanner() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const tokens = $api.useQuery('get', TOKENS);
  if (!tokens.data?.some((token) => expiresSoon(token))) return null;

  return (
    <div role="note" className="flex flex-wrap items-center gap-2 border-b bg-muted px-4 py-2">
      <span>{t('security.tokens.banner')}</span>
      <Link to="/security" className="underline">{t('security.tokens.banner_link')}</Link>
    </div>
  );
}
