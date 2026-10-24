import type fr from '@/locales/fr.json';
import type admin from '@/locales/fr.admin.json';
import type forms from '@/locales/fr.forms.json';
import type goals from '@/locales/fr.goals.json';
import type guides from '@/locales/fr.guides.json';
import type palette from '@/locales/fr.palette.json';
import type income from '@/locales/fr.income.json';
import type investments from '@/locales/fr.investments.json';
import type loans from '@/locales/fr.loans.json';
import type properties from '@/locales/fr.properties.json';
import type proposals from '@/locales/fr.proposals.json';
import type routine from '@/locales/fr.routine.json';
import type scenarios from '@/locales/fr.scenarios.json';
import type security from '@/locales/fr.security.json';
import type board from '@/locales/fr.subscriptions-board.json';
import type tax from '@/locales/fr.tax.json';

// The module bundles join `translation` when their pages load (src/locales/<module>.ts), under the same keys.
type Lazy = typeof admin & typeof forms & typeof goals & typeof guides & typeof scenarios & typeof palette & typeof income & typeof investments
  & typeof loans & typeof properties & typeof proposals & typeof routine & typeof security & typeof tax;

declare module 'i18next' {
  interface CustomTypeOptions {
    resources: { translation: typeof fr & Lazy; 'subscriptions-board': typeof board };
  }
}
