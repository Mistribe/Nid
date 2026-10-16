import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { useSession } from '@/app/Providers';
import type { Preferences } from '@/app/session';
import { applyTheme, rememberTheme, resolveMode, resolveTheme } from '@/app/theme';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { modes, themes, type Mode, type ThemeId } from '@/styles/themes';

type Change = Partial<Pick<Preferences, 'theme' | 'mode' | 'chartPatterns' | 'guidesSeen'>>;

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
  // The not-yet-confirmed choice, so a preview restore or a further pick builds on it
  // instead of a stale `session.preferences` while a save is in flight. The radios do not
  // show it: they follow the SAVED preferences (pessimistic writes, frontend/architecture.md §6),
  // only the page's look follows the choice at once.
  const [desired, setDesired] = useState<Choice | null>(null);
  const [replayed, setReplayed] = useState(false);
  const pending = useRef(0);

  const savedTheme = resolveTheme(session.preferences.theme);
  const savedMode = resolveMode(session.preferences.mode);
  const current = desired ?? { theme: savedTheme, mode: savedMode };
  const restoreCurrent = () => applyTheme(current.theme, current.mode);

  const save = $api.useMutation('patch', '/api/v1/core/me/preferences', {
    // Same-scope mutations run one after another, in the order they were started, so two
    // overlapping saves can never resolve out of order (design review, Ruling T10a).
    scope: { id: 'preferences' },
    onSuccess: (preferences, { body }) => {
      pending.current -= 1;
      // The guides are local first (a reset in `change`, a guide closed in useMarkSeen): a guide marked seen while this
      // save was in flight (this page may be gone) must not be undone by the older map this response carries.
      setPreferences((local) => ({ ...preferences, guidesSeen: local.guidesSeen }));
      // Only the last of a run of saves applies what the server confirmed and clears the
      // not-yet-confirmed choice: an earlier one settling first must not flash the page (or the
      // local copy theme-init.js reads) back to a value a queued save is about to change.
      // The same point also clears a problem an earlier save in the run may have raised — this
      // save's own success is the last word on whether the choice landed (Ruling T10c).
      if (pending.current === 0) {
        const theme = resolveTheme(preferences.theme);
        const mode = resolveMode(preferences.mode);
        applyTheme(theme, mode);
        rememberTheme(theme, mode);
        setDesired(null);
        setProblem(null);
      }
      setReplayed(body.guidesSeen !== undefined);
    },
    onError: (error) => {
      pending.current -= 1;
      setProblem(present(toProblem(error)));
      // A newer save is still queued behind this failed one (same scope): its choice is
      // still the one to show, and it will settle its own outcome — reverting now would
      // flash back to a value that newer save is about to change anyway (Ruling T10b).
      if (pending.current === 0) {
        setDesired(null);
        applyTheme(savedTheme, savedMode);
      }
    },
  });

  const change = (body: Change) => {
    setReplayed(false);
    setProblem(null);
    pending.current += 1;
    // ponytail: a reset that then fails stays local, so the guides replay this session anyway — what the person asked.
    const { guidesSeen } = body;
    if (guidesSeen) setPreferences((local) => ({ ...local, guidesSeen }));
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

      <section className="space-y-2">
        <h2 className="font-medium">{t('guides.settings.title')}</h2>
        <Button variant="outline" onClick={() => change({ guidesSeen: {} })}>{t('guides.settings.replay_all')}</Button>
        <p role="status">{replayed ? t('guides.settings.replayed') : null}</p>
      </section>
    </div>
  );
}
