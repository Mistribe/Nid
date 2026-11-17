import { Fragment } from 'react';
import type { TaxEstimate } from '@/api/tax';
import { AssessmentView } from '../assessments/AssessmentView';
import { GapTable } from '../assessments/GapTable';
import type { TaxAssessment } from '../assessments/api';

/** The household's notices, each under the gaps the estimate found against it. */
export function AssessmentTab({ notices, persons, gaps, adults }: {
  notices: TaxAssessment[]; persons: React.ComponentProps<typeof AssessmentView>['persons']; gaps: TaxEstimate['gaps']; adults: TaxEstimate['adults'];
}) {
  return <>{notices.map((a) => (
    <Fragment key={a.id}>
      {gaps && gaps.assessment === a.id ? <GapTable gaps={gaps} adults={adults} /> : null}
      <AssessmentView assessment={a} persons={persons} />
    </Fragment>
  ))}</>;
}
