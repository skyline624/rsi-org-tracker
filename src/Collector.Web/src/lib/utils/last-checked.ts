/**
 * Dernière fois que le collector a regardé une organisation. Un snapshot
 * (`timestamp`) n'est écrit que quand quelque chose change : il peut dater de
 * plusieurs mois pour une organisation relue chaque jour.
 */
export function lastChecked(org: {
  timestamp: string;
  contentCheckedAt?: string | null;
  membersCollectedAt?: string | null;
}): string {
  return [org.timestamp, org.contentCheckedAt, org.membersCollectedAt]
    .filter((d): d is string => !!d)
    .reduce((latest, d) => (Date.parse(d) > Date.parse(latest) ? d : latest));
}
