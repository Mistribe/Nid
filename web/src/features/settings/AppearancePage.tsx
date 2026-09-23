import { useTranslation } from 'react-i18next';

export function AppearancePage() {
  const { t } = useTranslation();
  return <h1 className="text-2xl font-semibold">{t('settings.appearance.title')}</h1>;
}
