import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ConfirmFromMenu } from '@/components/ConfirmAction';
import { ProblemMessage } from '@/components/ProblemMessage';
import { RowMenu } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table';
import { formatInstant } from '@/features/admin/format';
import { NewTokenDialog } from './NewTokenDialog';
import { TOKEN, TOKENS } from './tokens';

/** Spec 4A §8: « Mes jetons » on the Sécurité page. */
export function TokensSection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [creating, setCreating] = useState(false);
  const [revoking, setRevoking] = useState<string | null>(null);
  // Each row's ⋯, where the focus goes back when the confirmation closes.
  const menus = useRef<Record<string, HTMLButtonElement | null>>({});
  const tokens = $api.useQuery('get', TOKENS);
  const revoke = $api.useMutation('delete', TOKEN, {
    onSuccess: () => {
      toast.success(t('security.tokens.revoked'));
      void queryClient.invalidateQueries({ queryKey: ['get', TOKENS] });
    },
  });

  return (
    <Section title={t('security.tokens.title')} actions={<Button onClick={() => setCreating(true)}>{t('security.tokens.create')}</Button>}>
      <p>{t('security.tokens.intro')}</p>
      {creating ? <NewTokenDialog onClose={() => setCreating(false)} /> : null}
      {tokens.error ? <ProblemMessage problem={present(toProblem(tokens.error))} /> : null}
      {revoke.error ? <ProblemMessage problem={present(toProblem(revoke.error))} /> : null}
      {tokens.data && tokens.data.length === 0 ? <p>{t('security.tokens.empty')}</p> : null}
      {tokens.data && tokens.data.length > 0 ? (
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>{t('security.tokens.name')}</TableHead>
              <TableHead>{t('security.tokens.preset_label')}</TableHead>
              <TableHead>{t('security.tokens.expires')}</TableHead>
              <TableHead>{t('security.tokens.last_used')}</TableHead>
              <TableHead>{t('security.tokens.status_label')}</TableHead>
              <TableHead><span className="sr-only">{t('page.more_actions')}</span></TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {tokens.data.map((token) => (
              <TableRow key={token.id}>
                <TableCell>{token.name}</TableCell>
                <TableCell>{t(`security.tokens.preset.${token.preset as 'read'}`)}</TableCell>
                <TableCell>{formatInstant(token.expiresAt)}</TableCell>
                <TableCell>{token.lastUsedAt ? formatInstant(token.lastUsedAt) : t('security.tokens.never_used')}</TableCell>
                <TableCell><Badge variant={token.status === 'active' ? 'secondary' : 'outline'}>{t(`security.tokens.status.${token.status as 'active'}`)}</Badge></TableCell>
                <TableCell className="text-right">
                  {token.status === 'active' ? (
                    <RowMenu label={t('security.tokens.menu', { name: token.name })} triggerRef={(node) => { menus.current[token.id] = node; }}
                      items={[{ label: t('security.tokens.revoke'), destructive: true, onSelect: () => setRevoking(token.id) }]} />
                  ) : null}
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      ) : null}
      {revoking ? (
        <ConfirmFromMenu label={t('security.tokens.revoke')} confirmation={t('security.tokens.revoke_confirm')} pending={revoke.isPending}
          returnFocus={{ current: menus.current[revoking] ?? null }} onClose={() => setRevoking(null)}
          onConfirm={() => revoke.mutate({ params: { path: { id: revoking } } })} />
      ) : null}
    </Section>
  );
}
