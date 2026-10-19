import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import { ConfirmAction } from '@/components/ConfirmAction';
import { PageHeader } from '@/components/page/PageHeader';
import { Checkbox } from '@/components/ui/checkbox';
import { Label } from '@/components/ui/label';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { ProblemMessage } from '@/components/ProblemMessage';
import { formatInstant } from '@/features/admin/format';
import { eventLabel, viaLabel } from './eventLabel';
import { McpHelp } from './McpHelp';
import { TokensSection } from './TokensSection';

/** screens.md "Sécurité" (administration-and-audit.md §5): my events, signing out everywhere, and my tokens (4A). */
export function SecurityPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { leave } = useSession();
  const events = $api.useQuery('get', '/api/v1/platform/audit/me/security', { params: { query: { limit: 20 } } });
  // The session this page runs in ends too. Straight to the signed-out page: a reload would go through login, and the
  // identity provider's own session would sign the adult back in at once.
  const alsoId = useId();
  const [alsoTokens, setAlsoTokens] = useState(false);
  const revoke = $api.useMutation('post', '/api/v1/core/me/revoke-sessions', { onSuccess: () => leave() });

  return (
    <div className="space-y-8">
      <PageHeader title={t('security.title')} />

      <section className="space-y-3">
        <h2 className="text-xl font-semibold">{t('security.sessions_title')}</h2>
        <div className="flex items-center gap-2">
          <Checkbox id={alsoId} checked={alsoTokens} onCheckedChange={(checked) => setAlsoTokens(checked === true)} />
          <Label htmlFor={alsoId}>{t('security.revoke_tokens')}</Label>
        </div>
        <ConfirmAction label={t('security.revoke')} confirmation={t('security.revoke_confirm')} pending={revoke.isPending} onConfirm={() => revoke.mutate({ body: { revokeTokens: alsoTokens } })} />
        {revoke.error ? <ProblemMessage problem={present(toProblem(revoke.error))} /> : null}
        <p className="text-muted-foreground">{t('security.sso_reminder')}</p>
      </section>

      <TokensSection />

      <McpHelp />

      <section className="space-y-3">
        <h2 className="text-xl font-semibold">{t('security.events_title')}</h2>
        {events.error ? <ProblemMessage problem={present(toProblem(events.error))} /> : null}
        {events.data && events.data.entries.length === 0 ? <p>{t('security.empty')}</p> : null}
        {events.data && events.data.entries.length > 0 ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>{t('security.when')}</TableHead>
                <TableHead>{t('security.event')}</TableHead>
                <TableHead>{t('security.via')}</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {events.data.entries.map((entry) => (
                <TableRow key={entry.seq}>
                  <TableCell>{formatInstant(entry.at)}</TableCell>
                  <TableCell>{eventLabel(t, entry.event)}</TableCell>
                  <TableCell>{viaLabel(t, entry.actor.via)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : null}
      </section>
    </div>
  );
}
