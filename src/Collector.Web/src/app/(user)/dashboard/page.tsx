import { AutoRefresh } from "@/components/layout/AutoRefresh";
import { getCycleStatus, getStatsOverview } from "@/lib/api/endpoints";
import { redirectIfUnauthorized, requireAuthCtx } from "@/lib/auth/server-api";
import { DashboardView } from "./DashboardView";

export const dynamic = "force-dynamic";

export default async function DashboardPage() {
  const ctx = await requireAuthCtx();
  const results = await Promise.allSettled([getStatsOverview(ctx), getCycleStatus(ctx)]);
  redirectIfUnauthorized(results);
  const [overview, cycle] = results;

  return (
    <>
      <AutoRefresh intervalMs={60_000} />
      <DashboardView
        overview={overview.status === "fulfilled" ? overview.value : null}
        cycle={cycle.status === "fulfilled" ? cycle.value : null}
      />
    </>
  );
}
