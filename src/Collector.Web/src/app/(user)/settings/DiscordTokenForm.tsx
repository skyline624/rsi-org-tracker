"use client";
import { useState, type FormEvent } from "react";
import { toast } from "sonner";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { HudBadge } from "@/components/hud/HudBadge";
import { setDiscordTokenAction } from "./discord-token-actions";

export function DiscordTokenForm({ initialConfigured }: { initialConfigured: boolean }) {
  const [configured, setConfigured] = useState(initialConfigured);
  const [loading, setLoading] = useState(false);

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const el = e.currentTarget;
    const token = String(new FormData(el).get("token") ?? "").trim();
    if (token.length < 20) {
      toast.error("Token invalide (trop court).");
      return;
    }
    setLoading(true);
    const res = await setDiscordTokenAction(token);
    setLoading(false);
    if (res.ok) {
      setConfigured(true);
      el.reset();
      toast.success("Token Discord mis à jour.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <form onSubmit={submit} className="flex flex-col gap-3">
      <div className="flex items-center gap-2 font-mono text-xs text-hud-text-dim">
        Statut :
        {configured ? (
          <HudBadge tone="green">CONFIGURÉ</HudBadge>
        ) : (
          <HudBadge tone="orange">NON CONFIGURÉ</HudBadge>
        )}
      </div>
      <HudInput
        label="NOUVEAU TOKEN DU BOT"
        name="token"
        type="password"
        required
        autoComplete="off"
        placeholder="Colle le token du bot Discord"
      />
      <div className="flex justify-end">
        <HudButton type="submit" disabled={loading}>
          {loading ? "…" : "ENREGISTRER LE TOKEN"}
        </HudButton>
      </div>
      <p className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
        Le token n'est jamais réaffiché. Stocké côté serveur uniquement (data/discord.token).
        Prend effet immédiatement, sans redémarrage.
      </p>
    </form>
  );
}
