import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { useSession } from '@/app/Providers';
import type { Preferences } from '@/app/session';
import { applyTheme, rememberTheme, resolveMode, resolveTheme } from '@/app/theme';
import { ProblemMessage } from '@/components/ProblemMessage';
import { cn } from '@/lib/utils';
import { modes, themes, type Mode, type ThemeId } from '@/styles/themes';

type Change = Partial<Pick<Preferences, 'theme' | 'mode' | 'chartPatterns'>>;

/** design-system.md §7.5: mode, theme, chart patterns; applied at once, saved per person on the server. */
export function AppearancePage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session, setPreferences } = useSession();
  const [problem, setProblem] = useState<Presentation | null>(null);

  const savedTheme = resolveTheme(session.preferences.theme);
  const savedMode = resolveMode(session.preferences.mode);
  const restore = () => applyTheme(savedTheme, savedMode);

  const save = $api.useMutation('patch', '/api/v1/core/me/preferences', {
    onSuccess: (preferences) => {
      const theme = resolveTheme(preferences.theme);
      const mode = resolveMode(preferences.mode);
      applyTheme(theme, mode);
      rememberTheme(theme, mode);
      setPreferences(preferences);
    },
    onError: (error) => {
      restore();
      setProblem(present(toProblem(error)));
    },
  });

  const change = (body: Change) => {
    setProblem(null);
    save.mutate({ body });
  };

  const chooseTheme = (theme: ThemeId) => {
    applyTheme(theme, savedMode);
    change({ theme });
  };

  const chooseMode = (mode: Mode) => {
    applyTheme(savedTheme, mode);
    change({ mode });
  };

  const darkNow = document.documentElement.classList.contains('dark');

  return (
    <div className="max-w-2xl space-y-8">
      <h1 className="text-2xl font-semibold">{t('settings.appearance.title')}</h1>
      {problem ? <ProblemMessage problem={problem} /> : null}

      <fieldset className="space-y-2">
        <legend className="mb-2 font-medium">{t('settings.appearance.mode')}</legend>
        {modes.map((mode) => (
          <label key={mode} className="flex min-h-11 items-center gap-2">
            <input type="radio" name="mode" value={mode} checked={savedMode === mode} onChange={() => chooseMode(mode)} />
            {t(`mode.${mode}`)}
          </label>
        ))}
      </fieldset>

      <fieldset>
        <legend className="mb-2 font-medium">{t('settings.appearance.theme')}</legend>
        <div className="grid grid-cols-2 gap-3">
          {themes.map((theme) => (
            <label
              key={theme.id}
              data-theme={theme.id}
              onMouseEnter={() => applyTheme(theme.id, savedMode)}
              onMouseLeave={restore}
              className={cn(
                'flex cursor-pointer flex-col gap-3 rounded-lg border bg-background p-4 text-foreground',
                'has-[:checked]:ring-2 has-[:checked]:ring-ring has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-ring',
                darkNow && 'dark',
              )}
            >
              <input type="radio" name="theme" value={theme.id} checked={savedTheme === theme.id} onChange={() => chooseTheme(theme.id)} className="sr-only" />
              <span className="flex gap-1" aria-hidden="true">
                <span className="size-6 rounded-full bg-primary" />
                <span className="size-6 rounded-full bg-muted" />
                <span className="size-6 rounded-full border bg-accent" />
              </span>
              <span>{t(`theme.${theme.id}`)}</span>
            </label>
          ))}
        </div>
      </fieldset>

      <label className="flex min-h-11 items-center gap-2">
        <input
          type="checkbox"
          checked={session.preferences.chartPatterns}
          onChange={(event) => change({ chartPatterns: event.target.checked })}
        />
        {t('settings.appearance.chart_patterns')}
      </label>
    </div>
  );
}
