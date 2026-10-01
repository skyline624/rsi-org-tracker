"use client";
import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { DiscordText } from "@/components/discord/DiscordText";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudButton } from "@/components/hud/HudButton";
import type { DiscordSuggestionDto } from "@/lib/api/types";
import { cleanDiscordText, confidenceBadge } from "@/lib/discord/format";
import { acceptSuggestionAction, rejectSuggestionAction, undoRejectionAction } from "../actions";

const keyOf = (s: DiscordSuggestionDto) => `${s.discordUserId}:${s.citizenId ?? `h:${s.handle.toLowerCase()}`}`;

/**
 * Link suggestions with « Valider » (creates the link) and « Ignorer » (stops proposing
 * it; the toast offers to undo). The list is read again with router.refresh().
 */
export function SuggestionsList({ suggestions }: { suggestions: DiscordSuggestionDto[] }) {
  const router = useRouter();
  const [busyKey, setBusyKey] = useState<string | null>(null);

  async function accept(s: DiscordSuggestionDto) {
    setBusyKey(keyOf(s));
    const res = await acceptSuggestionAction(s.discordUserId, s.citizenId, s.handle);
    setBusyKey(null);
    if (res.ok) {
      toast.success(`Lien validé : ${cleanDiscordText(s.discordName) ?? s.discordUserId} → ${res.data?.handle ?? s.handle}.`);
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function undo(id: number) {
    const res = await undoRejectionAction(id);
    if (res.ok) {
      toast.success("Suggestion rétablie.");
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function reject(s: DiscordSuggestionDto) {
    setBusyKey(keyOf(s));
    const res = await rejectSuggestionAction(s.discordUserId, s.citizenId, s.handle);
    setBusyKey(null);
    if (!res.ok) {
      toast.error(res.error ?? "Échec.");
      return;
    }
    router.refresh();
    const rejectionId = res.data?.id;
    toast.success(
      `Suggestion ignorée : ${s.handle}.`,
      rejectionId === undefined
        ? undefined
        : { action: { label: "Annuler", onClick: () => void undo(rejectionId) } },
    );
  }

  if (suggestions.length === 0) {
    return (
      <p className="py-6 text-center font-mono text-xs text-hud-text-dim">
        Aucune suggestion : chaque membre est lié, ou aucun nom ne correspond à un handle RSI.
      </p>
    );
  }

  return (
    <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
      {suggestions.map((s) => {
        const key = keyOf(s);
        const badge = confidenceBadge(s.confidence);
        return (
          <li key={key} className="flex flex-wrap items-center gap-3 py-2">
            <span className="flex min-w-0 flex-1 flex-wrap items-center gap-2">
              <span className="inline-flex min-w-0 max-w-[16rem] text-hud-text">
                <DiscordText value={s.discordName} fallback={s.discordUserId} />
              </span>
              <span aria-hidden className="text-hud-text-dim">
                →
              </span>
              <Link href={`/users/${encodeURIComponent(s.handle)}`} className="text-hud-cyan hover:text-hud-orange">
                {s.handle}
              </Link>
              {s.displayName && <span className="max-w-[12rem] truncate text-hud-text-dim">{s.displayName}</span>}
              {s.citizenId !== null && <HudBadge tone="dim">#{s.citizenId}</HudBadge>}
              <HudBadge tone={badge.tone}>{badge.label}</HudBadge>
              <span className="inline-flex min-w-0 max-w-[14rem] items-center gap-1 text-[10px] text-hud-text-dim">
                jeton <DiscordText value={s.matchedToken} />
              </span>
            </span>
            <span className="flex gap-2">
              <HudButton type="button" className="px-2 py-1" disabled={busyKey !== null} onClick={() => accept(s)}>
                {busyKey === key ? "…" : "Valider"}
              </HudButton>
              <HudButton
                type="button"
                variant="ghost"
                className="px-2 py-1"
                disabled={busyKey !== null}
                onClick={() => reject(s)}
              >
                Ignorer
              </HudButton>
            </span>
          </li>
        );
      })}
    </ul>
  );
}
