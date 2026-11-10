import { Link } from '@tanstack/react-router';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { NAV, type NavItem } from '@/components/nav';
import { PageHeader } from '@/components/page/PageHeader';
import { Section } from '@/components/page/Section';
import { LastRun } from './JobsPage';
import '@/locales/admin';

/** A section's key: its path under /admin, so a typo fails typecheck. */
type Key = NavItem['to'] extends infer P ? P extends `/admin/${infer K}` ? K : never : never;

const GROUPS: readonly (readonly ['reference' | 'operations' | 'access' | 'system', readonly Key[]])[] = [
  ['reference', ['fx', 'prices', 'irl', 'inflation']],
  ['operations', ['jobs', 'banking', 'catalog']],
  ['access', ['users', 'household', 'security']],
  ['system', ['system', 'notifications']],
];

/** The job whose last run a section shows. */
const JOB_OF: Partial<Record<Key, string>> = {
  fx: 'fx-daily', prices: 'prices-daily', irl: 'irl-weekly', inflation: 'cpi-weekly', banking: 'bank-sync-nightly', catalog: 'catalog-sync',
};

const ITEMS = NAV.filter((item) => item.group === 'admin' && item.to !== '/admin');

/** The admin root: every section by group, with its last run or its health when there is one to say (screens.md §12). */
export function AdminOverviewPage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const jobs = $api.useQuery('get', '/api/v1/admin/jobs').data;
  const health = $api.useQuery('get', '/api/v1/admin/system').data?.health;
  const failed = jobs?.filter((j) => j.lastRun?.outcome === 'failed').length ?? 0;

  const state = (key: Key) => {
    if (key === 'jobs') return failed > 0 ? t('admin.overview.jobs_failed', { count: failed }) : null;
    if (key === 'system') return health ? t(health.database === 'healthy' && health.identityProvider === 'healthy' ? 'admin.system.healthy' : 'admin.system.unhealthy') : null;
    const job = jobs?.find((j) => j.key === JOB_OF[key]);
    return job ? <LastRun run={job.lastRun} /> : null;
  };

  return (
    <div className="space-y-6">
      <PageHeader title={t('nav.admin')} />
      {GROUPS.map(([group, keys]) => (
        <Section key={group} title={t(`admin.overview.group_${group}`)}>
          <ul className="divide-y">
            {keys.map((key) => {
              const item = ITEMS.find((i) => i.to === `/admin/${key}`)!;
              return (
                <li key={key} className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 py-2">
                  <div>
                    <Link to={item.to} className="font-medium underline-offset-4 hover:underline">{t(item.label)}</Link>
                    <p className="text-sm text-muted-foreground">{t(`admin.overview.${key}_hint`)}</p>
                  </div>
                  <div className="text-sm">{state(key)}</div>
                </li>
              );
            })}
          </ul>
        </Section>
      ))}
    </div>
  );
}
