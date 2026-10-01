import type { SyncMethod, SyncPayload } from "./payload";

/** Why collection ended. Only normal exhaustion can assert a full member-search roster. */
export type CollectionStopReason = "exhausted" | "cursor_stalled" | "invalid_cursor" | "call_cap" | "deadline" | "error" | "aborted";

/**
 * The coverage block of a sync. Only a member search can see the whole guild, so only it can
 * be complete, and only when it collected exactly the count Discord announced
 * (`total_result_count`). An empty result is never complete: the sender is a member, and the
 * tracker refuses a complete sync without members (empty_complete_sync). The tracker recomputes
 * this anyway; the plugin reports the same value so its own status matches.
 */
export function computeCoverage(
  method: SyncMethod, collected: number, expected: number | null, stopReason: CollectionStopReason,
): SyncPayload["coverage"] {
  const complete = stopReason === "exhausted" && method === "member-search"
    && Number.isSafeInteger(collected) && collected > 0 && collected === expected;
  return { method, complete, expectedCount: expected, collectedCount: collected };
}
