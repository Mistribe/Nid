import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import { ConfirmAction } from '@/components/ConfirmAction';
import { PageHeader } from '@/components/page/PageHeader';
import { Section } from '@/components/page/Section';
import { SwitchField } from '@/components/page/SwitchField';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { ProblemMessage } from '@/components/ProblemMessage';
import { formatInstant } from '@/lib/format';
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
  const [alsoTokens, setAlsoTokens] = useState(false);
  const revoke = $api.useMutation('post', '/api/v1/core/me/revoke-sessions', { onSuccess: () => leave() });

  return (
    <div className="space-y-8">
      <PageHeader title={t('security.title')} />

      <Section title={t('security.sessions_title')} actions={
        <ConfirmAction label={t('security.revoke')} confirmation={t('security.revoke_confirm')} pending={revoke.isPending} onConfirm={() => revoke.mutate({ body: { revokeTokens: alsoTokens } })} />
      }>
        <SwitchField label={t('security.revoke_tokens')} checked={alsoTokens} onCheckedChange={setAlsoTokens} />
        {revoke.error ? <ProblemMessage problem={present(toProblem(revoke.error))} /> : null}
        <p className="text-muted-foreground">{t('security.sso_reminder')}</p>
      </Section>

      <TokensSection />

      <McpHelp />

      <Section title={t('security.events_title')}>
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
      </Section>
    </div>
  );
}
