import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { NAV } from '@/app/nav';
import { ModalDialog } from '@/components/ModalDialog';
import { Button } from '@/components/ui/button';
import type { Guide } from './guides';

/** Spec §4.3: A, the module's introduction in a few screens. Every way out (« Passer », « Commencer », Escape) is `onClose`. */
export function GuideIntro({ guide, onClose, onShow }: { guide: Guide; onClose: () => void; onShow?: () => void }) {
  const { t } = useTranslation();
  const [step, setStep] = useState(1);
  const module = t(NAV.find((item) => item.to === guide.section)!.label);
  // i18next types its keys; every guide's screens share loans' shape (guides.test.ts checks they exist).
  const screen = `guides.${guide.id}.screens.${step}` as 'guides.loans.screens.1';
  const last = step === guide.screens;

  return (
    <ModalDialog title={t('guides.common.title', { module })} onClose={onClose}>
      {() => (
        <>
          <p aria-live="polite" className="text-sm text-muted-foreground">{t('guides.common.step', { n: step, total: guide.screens })}</p>
          <h3 className="font-semibold">{t(`${screen}.title`)}</h3>
          <p>{t(`${screen}.body`)}</p>
          <div className="flex flex-wrap justify-between gap-2">
            <Button variant="ghost" onClick={onClose}>{t('guides.common.skip')}</Button>
            <div className="flex flex-wrap gap-2">
              {step > 1 ? <Button variant="outline" onClick={() => setStep(step - 1)}>{t('guides.common.previous')}</Button> : null}
              {!last ? <Button onClick={() => setStep(step + 1)}>{t('guides.common.next')}</Button> : null}
              {last && onShow ? <Button variant="outline" onClick={onShow}>{t('guides.common.show')}</Button> : null}
              {last ? <Button onClick={onClose}>{t('guides.common.start')}</Button> : null}
            </div>
          </div>
        </>
      )}
    </ModalDialog>
  );
}
