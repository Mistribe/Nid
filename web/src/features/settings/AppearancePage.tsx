import { CheckIcon } from 'lucide-react';
import { RadioGroup } from 'radix-ui';
import { useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { useApi } from '@/api/ApiProvider';
import { present, toProblem, type Presentation } from '@/api/errors';
import { useSession } from '@/app/Providers';
import type { Preferences } from '@/app/session';
import { useReduceTransparency } from '@/app/transparency';
import { applyTheme, rememberTheme, resolveMode, resolveTheme } from '@/app/theme';
import { ProblemMessage } from '@/components/ProblemMessage';
import { Segmented } from '@/components/form/Segmented';
import { SettingsFrame } from './SettingsFrame';
import { SwitchField } from '@/components/page/SwitchField';
import { Button } from '@/components/ui/button';
import { modes, themes, type Mode, type ThemeId } from '@/styles/themes';
import '@/locales/guides';

type Change = Partial<Pick<Preferences, 'theme' | 'mode' | 'chartPatterns' | 'guidesSeen'>>;

/** The latest choice the person made, applied locally, that the server hasn't confirmed yet. */
type Choice = { theme: ThemeId; mode: Mode };

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
  const pending = useRef(0);
  const transparency = useReduceTransparency();

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
      if (body.guidesSeen !== undefined) toast.success(t('guides.settings.replayed'));
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

  const modeLabel = useId();
  const themeLabel = useId();

  return (
    <SettingsFrame title={t('settings.appearance.title')}>
      {problem ? <ProblemMessage problem={problem} /> : null}

      <section className="space-y-2">
        <h2 id={modeLabel} className="font-medium">{t('settings.appearance.mode')}</h2>
        <Segmented aria-labelledby={modeLabel} value={savedMode} onValueChange={(mode) => chooseMode(mode as Mode)}
          options={modes.map((mode) => ({ value: mode, label: t(`mode.${mode}`) }))} />
      </section>

      <section className="space-y-2">
        <h2 id={themeLabel} className="font-medium">{t('settings.appearance.theme')}</h2>
        <RadioGroup.Root aria-labelledby={themeLabel} value={savedTheme} onValueChange={(theme) => chooseTheme(theme as ThemeId)} className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          {themes.map((theme) => (
            <RadioGroup.Item
              key={theme.id}
              value={theme.id}
              onMouseEnter={() => previewTheme(theme.id)}
              onMouseLeave={restoreCurrent}
              onFocus={() => previewTheme(theme.id)}
              onBlur={restoreCurrent}
              className="flex flex-col items-start gap-3 rounded-lg border bg-card p-4 text-left text-sm text-foreground outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50 data-[state=checked]:border-ring data-[state=checked]:ring-2 data-[state=checked]:ring-ring"
            >
              <span className="flex gap-1" aria-hidden="true">
                {theme.swatches.map((color) => <span key={color} className="size-6 rounded-full border" style={{ background: color }} />)}
              </span>
              <span className="flex items-center gap-1 font-medium">
                {t(`theme.${theme.id}`)}
                {savedTheme === theme.id ? <CheckIcon aria-hidden="true" className="size-4" /> : null}
              </span>
            </RadioGroup.Item>
          ))}
        </RadioGroup.Root>
      </section>

      <SwitchField label={t('settings.appearance.chart_patterns')} checked={session.preferences.chartPatterns}
        onCheckedChange={(chartPatterns) => change({ chartPatterns })} />

      <SwitchField label={t('settings.appearance.reduce_transparency')} hint={t('settings.appearance.reduce_transparency_hint')}
        checked={transparency.reduced} onCheckedChange={transparency.setReduced} />

      <section className="space-y-2">
        <h2 className="font-medium">{t('guides.settings.title')}</h2>
        <Button variant="outline" onClick={() => change({ guidesSeen: {} })}>{t('guides.settings.replay_all')}</Button>
      </section>
    </SettingsFrame>
  );
}
