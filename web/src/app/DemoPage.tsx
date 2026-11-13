import { useTranslation } from 'react-i18next';
import { Wordmark } from '@/components/Logo';
import { buttonVariants } from '@/components/ui/button';
import { demoSignInUrl } from './session';

/** The demo household's adults (DemoHousehold on the server): Alice administers Nid, Bob does not. */
const ADULTS = ['alice', 'bob'] as const;

/**
 * Demo mode (issue 203): /auth/login sends the browser here instead of an identity provider. One click signs in as an
 * adult of the invented demo household; the server checks the return path (ReturnUrl.IsSafe), as /auth/login does.
 */
export function DemoPage({ returnUrl }: { returnUrl: string | null }) {
  const { t } = useTranslation();
  return (
    <main className="mx-auto flex min-h-dvh max-w-md flex-col justify-center p-4">
      <div className="content-pane rounded-pane flex flex-col gap-4 p-6 shadow-card">
        <Wordmark className="h-8" />
        <h1 className="text-2xl font-semibold">{t('demo.title')}</h1>
        <p>{t('demo.body')}</p>
        <ul className="flex flex-col gap-3">
          {ADULTS.map((key) => (
            <li key={key} className="flex flex-col gap-1">
              <a href={demoSignInUrl(key, returnUrl)} className={buttonVariants()}>{t(`demo.enter_${key}`)}</a>
              <span className="text-sm text-muted-foreground">{t(`demo.who_${key}`)}</span>
            </li>
          ))}
        </ul>
        <p className="text-sm text-muted-foreground">{t('demo.reset')}</p>
      </div>
    </main>
  );
}

/** Every screen of a demo says so (Shell): invented data, reset each night, and where the code is. */
export function DemoBanner() {
  const { t } = useTranslation();
  return (
    <div role="note" className="material rounded-pane mx-2 mb-2 flex flex-wrap items-center gap-2 px-4 py-2 md:ml-0">
      <span>{t('demo.banner')}</span>
      <a href="https://github.com/Mistribe/Nid" className="underline" target="_blank" rel="noreferrer">{t('demo.banner_link')}</a>
    </div>
  );
}
