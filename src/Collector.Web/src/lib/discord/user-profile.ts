/**
 * Loads a citizen's Discord cross profile for the « DISCORD · SERVEURS » section. The
 * section must never break the citizen page: a handle the API does not know (404) or a
 * citizen without a linked Discord account gives the empty state, any other failure a
 * short notice. Server-side only (it calls the API client).
 */

import { getUserDiscord } from "@/lib/api/endpoints";
import { ApiError } from "@/lib/api/errors";
import type { DiscordUserProfileDto } from "@/lib/api/types";

export type UserDiscordState =
  | { kind: "profile"; profile: DiscordUserProfileDto }
  | { kind: "empty" }
  | { kind: "unavailable" };

export async function loadUserDiscord(
  ctx: Parameters<typeof getUserDiscord>[0],
  handle: string,
): Promise<UserDiscordState> {
  try {
    const profile = await getUserDiscord(ctx, handle);
    const accounts = Array.isArray(profile?.accounts) ? profile.accounts : [];
    // Without a linked account the timeline would only repeat the RSI changes of the page.
    if (accounts.length === 0) return { kind: "empty" };
    return {
      kind: "profile",
      profile: { accounts, timeline: Array.isArray(profile.timeline) ? profile.timeline : [] },
    };
  } catch (err) {
    return err instanceof ApiError && err.status === 404 ? { kind: "empty" } : { kind: "unavailable" };
  }
}
