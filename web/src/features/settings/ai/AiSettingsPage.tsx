import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useSession } from '@/app/Providers';
import { Section } from '@/components/page/Section';
import { SettingsFrame } from '../SettingsFrame';
import '@/locales/ai';
import { AssistantMeSection } from './AssistantMeSection';

const link = 'text-sm font-medium underline underline-offset-4';

/**
 * Réglages › IA (#585): what each adult decides for themselves, turning the assistant on and their preference for their data
 * off the premises, and their own usage. The workers, the models and the household's data policy are the administration's.
 */
export function AiSettingsPage() {
  const { t } = useTranslation();
  const { capabilities } = useSession();
  return (
    <SettingsFrame title={t('ai.title')}>
      <AssistantMeSection />
      <Section title={t('ai.system.title')}>
        <p className="max-w-prose text-sm text-muted-foreground">{t('ai.system.body')}</p>
        <div className="flex flex-wrap gap-4">
          <Link to="/settings/ai/usage" className={link}>{t('ai.usage.link')}</Link>
          {capabilities.admin ? <Link to="/admin/ai" className={link}>{t('ai.system.admin_link')}</Link> : null}
        </div>
      </Section>
    </SettingsFrame>
  );
}
