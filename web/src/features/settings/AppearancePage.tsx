import { useRef, useState } from 'react';
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

/** The latest choice the person made, applied locally, that the server hasn't confirmed yet. */
type Choice = { theme: ThemeId; mode: Mode };

const isDark = (mode: Mode, media: MediaQueryList = window.matchMedia('(prefers-color-scheme: dark)')): boolean =>
  mode === 'dark' || (mode === 'system' && media.matches);

/** design-system.md §7.5: mode, theme, chart patterns; applied at once, saved per person on the server. */
export function AppearancePage() {
  const { t } = useTranslation();
  const { $api } = useApi();
  const { session, setPreferences } = useSession();
  const [problem, setProblem] = useState<Presentation | null>(null);
  // The not-yet-confirmed choice, so a preview restore or a further pick reflects it
  // instead of falling back to a stale `session.preferences` while a save is in flight.
  const [desired, setDesired] = useState<Choice | null>(null);
  const pending = useRef(0);

  const savedTheme = resolveTheme(session.preferences.theme);
  const savedMode = resolveMode(session.preferences.mode);
  const current = desired ?? { theme: savedTheme, mode: savedMode };
  const restoreCurrent = () => applyTheme(current.theme, current.mode);

  const save = $api.useMutation('patch', '/api/v1/core/me/preferences', {
    // Same-scope mutations run one after another, in the order they were started, so two
    // overlapping saves can never resolve out of order (design review, Ruling T10a).
    scope: { id: 'preferences' },
    onSuccess: (preferences) => {
      pending.current -= 1;
      const theme = resolveTheme(preferences.theme);
      const mode = resolveMode(preferences.mode);
      applyTheme(theme, mode);
      rememberTheme(theme, mode);
      setPreferences(preferences);
      // Only the last of a run of saves clears the not-yet-confirmed choice: an earlier one
      // settling first must not make radios fall back to a saved value another save will still change.
      if (pending.current === 0) setDesired(null);
    },
    onError: (error) => {
      pending.current -= 1;
      setDesired(null);
      applyTheme(savedTheme, savedMode);
      setProblem(present(toProblem(error)));
    },
  });

  const change = (body: Change) => {
    setProblem(null);
    pending.current += 1;
    save.mutate({ body });
  };

  const chooseTheme = (theme: ThemeId) => {
    const next: Choice = { theme, mode: current.mode };
    applyTheme(next.theme, next.mode);
    setDesired(next);
    change({ theme });
  };

  const chooseMode = (mode: Mode) => {
    const next: Choice = { theme: current.theme, mode };
    applyTheme(next.theme, next.mode);
    setDesired(next);
    change({ mode });
  };

  const previewTheme = (theme: ThemeId) => applyTheme(theme, current.mode);

  const darkNow = isDark(current.mode);

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
              onMouseEnter={() => previewTheme(theme.id)}
              onMouseLeave={restoreCurrent}
              className={cn(
                'flex cursor-pointer flex-col gap-3 rounded-lg border bg-background p-4 text-foreground',
                'has-[:checked]:ring-2 has-[:checked]:ring-ring has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-ring',
                darkNow && 'dark',
              )}
            >
              <input
                type="radio"
                name="theme"
                value={theme.id}
                checked={savedTheme === theme.id}
                onChange={() => chooseTheme(theme.id)}
                onFocus={() => previewTheme(theme.id)}
                onBlur={restoreCurrent}
                className="sr-only"
              />
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
