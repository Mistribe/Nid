import { useQueryClient } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { ConfirmFromMenu } from '@/components/ConfirmAction';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Folded } from '@/components/form/Folded';
import { RowMenu, shownOr } from '@/components/page/RowMenu';
import { Section } from '@/components/page/Section';
import { SortableTable } from '@/components/page/SortableTable';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { formatInstant } from '@/lib/format';
import { NewTokenDialog } from './NewTokenDialog';
import { TOKEN, TOKENS, type TokenView } from './tokens';

/** Spec 4A §8: « Mes jetons » on the Sécurité page. */
export function TokensSection() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const queryClient = useQueryClient();
  const [creating, setCreating] = useState(false);
  const [revoking, setRevoking] = useState<string | null>(null);
  // Each row's ⋯ (in the table and in the phone list), where the focus goes back when the confirmation closes.
  const menus = useRef<Record<string, HTMLButtonElement | null>>({});
  const tokens = $api.useQuery('get', TOKENS);
  const revoke = $api.useMutation('delete', TOKEN, {
    onSuccess: () => {
      toast.success(t('security.tokens.revoked'));
      void queryClient.invalidateQueries({ queryKey: ['get', TOKENS] });
    },
  });

  // Active ones first (#308); revoked and expired ones dimmed, folded under them: they pile up over the years.
  const active = tokens.data?.filter((token) => token.status === 'active') ?? [];
  const inactive = tokens.data?.filter((token) => token.status !== 'active') ?? [];
  const menu = (token: TokenView, where: 'table' | 'list') => (token.status === 'active' ? (
    <RowMenu label={t('security.tokens.menu', { name: token.name })} triggerRef={(node) => { menus.current[`${token.id}:${where}`] = node; }}
      items={[{ label: t('security.tokens.revoke'), destructive: true, onSelect: () => setRevoking(token.id) }]} />
  ) : null);

  const list = (shown: TokenView[], caption: string) => (
    <SortableTable<TokenView> caption={caption} rows={shown} rowKey={(token) => token.id}
      rowClassName={(token) => (token.status === 'active' ? undefined : 'text-muted-foreground')}
      columns={[
        { id: 'name', header: t('security.tokens.name'), wrap: true, cell: (token) => token.name },
        { id: 'preset', header: t('security.tokens.preset_label'), cell: (token) => <PresetLabel preset={token.preset} /> },
        { id: 'expires', header: t('security.tokens.expires'), className: 'max-lg:hidden', cell: (token) => formatInstant(token.expiresAt) },
        { id: 'used', header: t('security.tokens.last_used'), className: 'max-lg:hidden', cell: (token) => (token.lastUsedAt ? formatInstant(token.lastUsedAt) : t('security.tokens.never_used')) },
        { id: 'status', header: t('security.tokens.status_label'), cell: (token) => <Status token={token} /> },
        { id: 'menu', header: t('page.more_actions'), align: 'end', cell: (token) => menu(token, 'table') },
      ]}
      mobile={(token) => (
        <div className="space-y-1">
          <div className="flex items-center justify-between gap-2">
            <span className="min-w-0 break-words">{token.name}</span>
            <span className="flex flex-none items-center gap-1"><Status token={token} />{menu(token, 'list')}</span>
          </div>
          <p className="text-sm text-muted-foreground"><PresetLabel preset={token.preset} /> · {t('security.tokens.expires_on', { at: formatInstant(token.expiresAt) })}</p>
        </div>
      )} />
  );

  return (
    <Section title={t('security.tokens.title')} actions={<Button onClick={() => setCreating(true)}>{t('security.tokens.create')}</Button>}>
      <p className="max-w-prose">{t('security.tokens.intro')}</p>
      {creating ? <NewTokenDialog onClose={() => setCreating(false)} /> : null}
      {tokens.error ? <ProblemMessage problem={present(toProblem(tokens.error))} /> : null}
      {revoke.error ? <ProblemMessage problem={present(toProblem(revoke.error))} /> : null}
      {tokens.data && tokens.data.length === 0 ? <p>{t('security.tokens.empty')}</p> : null}
      {active.length > 0 ? list(active, t('security.tokens.title')) : null}
      {inactive.length > 0 ? (
        <Folded label={t('security.tokens.inactive', { count: inactive.length })} hasError={false}>
          {list(inactive, t('security.tokens.inactive_caption'))}
        </Folded>
      ) : null}
      {revoking ? (
        <ConfirmFromMenu label={t('security.tokens.revoke')} confirmation={t('security.tokens.revoke_confirm')} pending={revoke.isPending}
          returnFocus={{ current: shownOr(menus.current[`${revoking}:table`] ?? null, menus.current[`${revoking}:list`] ?? null) }} onClose={() => setRevoking(null)}
          onConfirm={() => revoke.mutate({ params: { path: { id: revoking } } })} />
      ) : null}
    </Section>
  );
}

function Status({ token }: { token: TokenView }) {
  const { t } = useTranslation();
  return <Badge variant={token.status === 'active' ? 'secondary' : 'outline'}>{t(`security.tokens.status.${token.status as 'active'}`)}</Badge>;
}

/** A calendar subscription's token is made and managed in Réglages › Calendrier: it says so, with the way there. */
function PresetLabel({ preset }: { preset: string }) {
  const { t } = useTranslation();
  const label = t(`security.tokens.preset.${preset as 'read'}`);
  return preset === 'calendar' ? <Link to="/settings/calendar" className="underline">{label}</Link> : label;
}
