import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { NAV } from '@/app/nav';
import { ModalDialog } from '@/components/ModalDialog';
import { Button } from '@/components/ui/button';
import { findTarget, type Guide } from './guideData';
import '@/locales/guides';

/** Spec §4.3: A, the module's introduction in a few screens. Every way out (« Passer », « Commencer », Escape) is `onClose`. */
export function GuideIntro({ guide, onClose, onShow }: { guide: Guide; onClose: () => void; onShow?: () => void }) {
  const { t } = useTranslation();
  const [step, setStep] = useState(1);
  const main = useRef<HTMLButtonElement>(null);
  const module = t(NAV.find((item) => item.to === guide.section)!.label);
  // i18next types its keys; every guide's screens share loans' shape (guides.test.ts checks they exist).
  const screen = `guides.${guide.id}.screens.${step}` as 'guides.loans.screens.1';
  const last = step === guide.screens;
  // Read at each screen: the page may have rendered its targets behind A meanwhile.
  const shown = last && guide.tour.some((s) => findTarget(guide, s));

  return (
    <ModalDialog title={t('guides.common.title', { module })} onClose={onClose}>
      {() => (
        <>
          {/* One live region for the screen: a change reads out its count, title and text, not the count alone. */}
          <div aria-live="polite" className="space-y-3">
            <p className="text-sm text-muted-foreground">{t('guides.common.step', { n: step, total: guide.screens })}</p>
            <h3 className="font-semibold">{t(`${screen}.title`)}</h3>
            <p>{t(`${screen}.body`)}</p>
          </div>
          {last && !shown && guide.hint ? <p>{t(`guides.${guide.id}.hint` as 'guides.loans.hint')}</p> : null}
          <div className="flex flex-wrap justify-between gap-2">
            <Button variant="ghost" onClick={onClose}>{t('guides.common.skip')}</Button>
            <div className="flex flex-wrap gap-2">
              {/* Back on screen 1 « Précédent » goes: the focus moves to the main button, which never unmounts. */}
              {step > 1 ? <Button variant="outline" onClick={() => { if (step === 2) main.current?.focus(); setStep(step - 1); }}>{t('guides.common.previous')}</Button> : null}
              {shown && onShow ? <Button variant="outline" onClick={onShow}>{t('guides.common.show')}</Button> : null}
              <Button ref={main} onClick={last ? onClose : () => setStep(step + 1)}>{t(last ? 'guides.common.start' : 'guides.common.next')}</Button>
            </div>
          </div>
        </>
      )}
    </ModalDialog>
  );
}
