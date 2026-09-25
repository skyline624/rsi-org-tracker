import { AutoRefresh } from "@/components/layout/AutoRefresh";
import { getChangesSummary, listChanges } from "@/lib/api/endpoints";
import { redirectIfUnauthorized, requireAuthCtx } from "@/lib/auth/server-api";
import { ChangesView } from "./ChangesView";

export const dynamic = "force-dynamic";

export default async function ChangesPage() {
  const ctx = await requireAuthCtx();
  const results = await Promise.allSettled([
    listChanges({ limit: 100 }, ctx),
    getChangesSummary(30, ctx),
  ]);
  redirectIfUnauthorized(results);
  const [feed, summary] = results;

  return (
    <>
      <AutoRefresh intervalMs={30_000} />
      <ChangesView
        feed={feed.status === "fulfilled" ? feed.value : null}
        summary={summary.status === "fulfilled" ? summary.value : null}
      />
    </>
  );
}
