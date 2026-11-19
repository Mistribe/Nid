import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem } from '@/api/errors';
import { useSession } from '@/app/Providers';
import { ConfirmAction } from '@/components/ConfirmAction';
import { Folded } from '@/components/form/Folded';
import { DetailTabs, useDetailTab } from '@/components/page/DetailTabs';
import { PageHeader } from '@/components/page/PageHeader';
import { StatLine } from '@/components/page/StatLine';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Checkbox } from '@/components/ui/checkbox';
import { Label } from '@/components/ui/label';
import { ActivitySection } from './ActivitySection';
import { McpHelp } from './McpHelp';
import { PasswordSection } from './PasswordSection';
import { SettingRow } from './SettingRow';
import { TwoStepSection } from './TwoStepSection';
import { TokensSection } from './TokensSection';
import { TOKENS } from './tokens';
import '@/locales/security';

const TABS = ['sign-in', 'tokens', 'activity'] as const;

/**
 * screens.md "Sécurité" (administration-and-audit.md §5, #308): where the account stands, then three tabs: signing in
 * (sessions, password, two-step validation), my tokens with the assistant help, and my security events.
 */
export function SecurityPage() {
  const { t } = useTranslation();
  const [tab, setTab] = useDetailTab(TABS);
  return (
    <div className="space-y-6">
      <PageHeader title={t('security.title')}>
        <Status />
      </PageHeader>
      <DetailTabs value={tab} onChange={setTab} tabs={[
        { id: 'sign-in', label: t('security_page.tab.sign_in'), content: <SignInTab /> },
        {
          id: 'tokens', label: t('security_page.tab.tokens'), content: (
            <>
              <TokensSection />
              <Folded label={t('mcp_help.title')} hasError={false}><McpHelp /></Folded>
            </>
          ),
        },
        { id: 'activity', label: t('security_page.tab.activity'), content: <ActivitySection /> },
      ]} />
    </div>
  );
}

/** Is my account safe, without scrolling: how I sign in, my second factor, my active tokens. */
function Status() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session } = useSession();
  const local = session.methods.includes('password');
  const credential = $api.useQuery('get', '/api/v1/core/me/credential', {}, { retry: false, enabled: local });
  const tokens = $api.useQuery('get', TOKENS);
  const ways = [session.methods.includes('oidc') ? t('security_page.status.oidc') : null, local ? t('security_page.status.password') : null].filter(Boolean).join(' + ');
  return (
    <StatLine items={[
      { label: t('security_page.status.sign_in'), value: ways, hint: credential.data ? t('security_page.status.login', { login: credential.data.login }) : undefined },
      ...(credential.data ? [{
        label: t('security.totp.title'),
        value: credential.data.totpEnabled ? t('security.totp.badge_on') : <span className="text-status-warning-text">{t('security.totp.badge_off')}</span>,
        hint: credential.data.totpEnabled ? undefined : <Link to="/security" hash="two-step" className="underline">{t('security_page.status.turn_on')}</Link>,
      }] : []),
      { label: t('security_page.status.tokens'), value: tokens.data ? String(tokens.data.filter((token) => token.status === 'active').length) : '…' },
    ]} />
  );
}

function SignInTab() {
  const { session } = useSession();
  return (
    <div className="max-w-3xl divide-y divide-separator">
      <SessionsRow />
      {/* Issue 215: with local accounts; they render nothing for an adult without one. */}
      {session.methods.includes('password') ? <><PasswordSection /><TwoStepSection /></> : null}
    </div>
  );
}

function SessionsRow() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { leave, session } = useSession();
  // The session this page runs in ends too. Straight to the signed-out page: a reload would go through login, and the
  // identity provider's own session would sign the adult back in at once.
  const [alsoTokens, setAlsoTokens] = useState(false);
  const revoke = $api.useMutation('post', '/api/v1/core/me/revoke-sessions', { onSuccess: () => leave() });
  return (
    <SettingRow title={t('security.sessions_title')} actions={
      <ConfirmAction label={t('security.revoke')} confirmation={t(alsoTokens ? 'security.revoke_confirm_tokens' : 'security.revoke_confirm')}
        pending={revoke.isPending} onConfirm={() => revoke.mutate({ body: { revokeTokens: alsoTokens } })}>
        <Label className="items-start gap-2 font-normal">
          <Checkbox checked={alsoTokens} onCheckedChange={(on) => setAlsoTokens(on === true)} className="mt-0.5" />
          {t('security.revoke_tokens')}
        </Label>
        {session.methods.includes('oidc') ? <p className="text-sm text-muted-foreground">{t('security.sso_reminder')}</p> : null}
      </ConfirmAction>
    }>
      <p className="text-sm text-muted-foreground">{t('security.sessions_hint')}</p>
      {revoke.error ? <ProblemMessage problem={present(toProblem(revoke.error))} /> : null}
    </SettingRow>
  );
}
