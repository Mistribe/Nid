import { Fragment } from 'react';
import { useTranslation } from 'react-i18next';
import { useApi } from '@/api/ApiProvider';
import { PERSONS } from '@/api/persons';
import type { TaxEstimate } from '@/api/tax';
import { AssessmentView } from '../assessments/AssessmentView';
import { GapTable } from '../assessments/GapTable';
import type { TaxAssessment } from '../assessments/api';
import '@/locales/tax';

/** « Ce que l'administration a calculé »: the household's notices, each under the gaps the estimate found against it. */
export function AssessmentTab({ notices, gaps }: { notices: TaxAssessment[]; gaps?: TaxEstimate['gaps'] }) {
  const { t } = useTranslation();
  const { $api } = useApi();
  const persons = $api.useQuery('get', PERSONS).data ?? [];
  return (
    <div className="space-y-6">
      <h2 className="text-title">{t('tax.assessment_tab.question')}</h2>
      {notices.map((a) => (
        <Fragment key={a.id}>
          {gaps?.assessment === a.id ? <GapTable gaps={gaps} persons={persons} /> : null}
          <AssessmentView assessment={a} persons={persons} />
        </Fragment>
      ))}
    </div>
  );
}
