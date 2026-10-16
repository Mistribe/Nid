import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import { ConfirmAction } from '@/components/ConfirmAction';
import { ProblemMessage } from '@/components/ProblemMessage';
import { formatInstant } from '@/features/admin/format';
import { eventLabel, viaLabel } from './eventLabel';
import { TokensSection } from './TokensSection';

/** screens.md "Sécurité" (administration-and-audit.md §5): my events, signing out everywhere, and my tokens (4A). */
export function SecurityPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { leave } = useSession();
  const events = $api.useQuery('get', '/api/v1/platform/audit/me/security', { params: { query: { limit: 20 } } });
  // The session this page runs in ends too. Straight to the signed-out page: a reload would go through login, and the
  // identity provider's own session would sign the adult back in at once.
  const [alsoTokens, setAlsoTokens] = useState(false);
  const revoke = $api.useMutation('post', '/api/v1/core/me/revoke-sessions', { onSuccess: () => leave() });

  return (
    <div className="space-y-8">
      <h1 className="text-2xl font-semibold">{t('security.title')}</h1>

      <section className="space-y-3">
        <h2 className="text-xl font-semibold">{t('security.sessions_title')}</h2>
        <label className="flex items-center gap-2">
          <input type="checkbox" checked={alsoTokens} onChange={(e) => setAlsoTokens(e.target.checked)} />
          {t('security.revoke_tokens')}
        </label>
        <ConfirmAction label={t('security.revoke')} confirmation={t('security.revoke_confirm')} pending={revoke.isPending} onConfirm={() => revoke.mutate({ body: { revokeTokens: alsoTokens } })} />
        {revoke.error ? <ProblemMessage problem={present(toProblem(revoke.error))} /> : null}
        <p className="text-muted-foreground">{t('security.sso_reminder')}</p>
      </section>

      <TokensSection />

      <section className="space-y-3">
        <h2 className="text-xl font-semibold">{t('security.events_title')}</h2>
        {events.error ? <ProblemMessage problem={present(toProblem(events.error))} /> : null}
        {events.data && events.data.entries.length === 0 ? <p>{t('security.empty')}</p> : null}
        {events.data && events.data.entries.length > 0 ? (
          <table className="w-full text-left">
            <thead>
              <tr className="border-b">
                <th scope="col" className="py-2 pr-4">{t('security.when')}</th>
                <th scope="col" className="py-2 pr-4">{t('security.event')}</th>
                <th scope="col" className="py-2">{t('security.via')}</th>
              </tr>
            </thead>
            <tbody>
              {events.data.entries.map((entry) => (
                <tr key={entry.seq} className="border-b">
                  <td className="py-2 pr-4">{formatInstant(entry.at)}</td>
                  <td className="py-2 pr-4">{eventLabel(t, entry.event)}</td>
                  <td className="py-2">{viaLabel(t, entry.actor.via)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ) : null}
      </section>
    </div>
  );
}
