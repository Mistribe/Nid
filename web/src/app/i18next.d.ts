import type fr from '@/locales/fr.json';
import type admin from '@/locales/fr.admin.json';
import type banks from '@/locales/fr.banks.json';
import type documents from '@/locales/fr.documents.json';
import type exportStrings from '@/locales/fr.export.json';
import type importStrings from '@/locales/fr.import.json';
import type forms from '@/locales/fr.forms.json';
import type childcare from '@/locales/fr.childcare.json';
import type goals from '@/locales/fr.goals.json';
import type guides from '@/locales/fr.guides.json';
import type notifications from '@/locales/fr.notifications.json';
import type palette from '@/locales/fr.palette.json';
import type income from '@/locales/fr.income.json';
import type investments from '@/locales/fr.investments.json';
import type loans from '@/locales/fr.loans.json';
import type properties from '@/locales/fr.properties.json';
import type proposals from '@/locales/fr.proposals.json';
import type routine from '@/locales/fr.routine.json';
import type scenarios from '@/locales/fr.scenarios.json';
import type series from '@/locales/fr.series.json';
import type security from '@/locales/fr.security.json';
import type subscriptions from '@/locales/fr.subscriptions.json';
import type board from '@/locales/fr.subscriptions-board.json';
import type tax from '@/locales/fr.tax.json';
import type taxAssessments from '@/locales/fr.tax-assessments.json';

// The module bundles join `translation` when their pages load (src/locales/<module>.ts), under the same keys.
type Lazy = typeof admin & typeof banks & typeof childcare & typeof documents & typeof exportStrings & typeof importStrings & typeof forms & typeof goals & typeof guides & typeof scenarios & typeof notifications & typeof palette & typeof income & typeof investments
  & typeof loans & typeof properties & typeof proposals & typeof routine & typeof security & typeof series & typeof subscriptions & typeof tax & typeof taxAssessments;

declare module 'i18next' {
  interface CustomTypeOptions {
    resources: { translation: typeof fr & Lazy; 'subscriptions-board': typeof board };
  }
}
