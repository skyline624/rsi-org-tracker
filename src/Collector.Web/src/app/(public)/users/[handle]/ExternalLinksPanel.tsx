"use client";
import { useState } from "react";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { createLinkAction, deleteLinkAction, type LinkDto } from "./link-actions";

// Manually-added links that resolve to a clickable profile URL.
// (UEX is handled automatically by <UexPanel> via the API — the bio [UEX:NNN]
// code is only a verification token and has no public profile page.)
const PROVIDERS: Record<string, { label: string; url: (v: string) => string | null; hint: string }> = {
  discord: {
    label: "Discord",
    url: (v) => `https://discord.com/users/${encodeURIComponent(v)}`,
    hint: "ID utilisateur Discord (nombre)",
  },
  twitch: {
    label: "Twitch",
    url: (v) => `https://www.twitch.tv/${encodeURIComponent(v)}`,
    hint: "Nom de chaîne Twitch",
  },
};
const PROVIDER_KEYS = Object.keys(PROVIDERS);

const fieldCls =
  "hud-clip border border-hud-cyan-dim bg-hud-bg/60 px-3 py-2 font-mono text-sm text-hud-text placeholder:text-hud-text-dim/60 focus:border-hud-cyan focus:outline-none";

interface Props {
  handle: string;
  initialLinks: LinkDto[];
  currentUsername?: string;
  isAdmin?: boolean;
}

export function ExternalLinksPanel({ handle, initialLinks, currentUsername, isAdmin }: Props) {
  const [links, setLinks] = useState<LinkDto[]>(initialLinks);
  const [provider, setProvider] = useState<string>(PROVIDER_KEYS[0] ?? "discord");
  const [value, setValue] = useState("");
  const [busy, setBusy] = useState(false);

  const canModify = (l: LinkDto) => Boolean(isAdmin) || l.authorUsername === currentUsername;

  async function add(p: string, v: string) {
    const val = v.trim();
    if (!val) {
      toast.error("Entre une valeur.");
      return;
    }
    setBusy(true);
    const res = await createLinkAction(handle, p, val);
    setBusy(false);
    if (res.ok && res.link) {
      const created = res.link;
      // Keep other links (incl. same provider); replace only an identical id.
      setLinks((prev) => [...prev.filter((l) => l.id !== created.id), created]);
      setValue("");
      toast.success("Lien enregistré.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function remove(l: LinkDto) {
    setBusy(true);
    const res = await deleteLinkAction(l.id);
    setBusy(false);
    if (res.ok) {
      setLinks((prev) => prev.filter((x) => x.id !== l.id));
      toast.success("Lien supprimé.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <HudPanel label="LIENS EXTERNES">
      <div className="flex flex-col gap-4">
        {links.length === 0 ? (
          <p className="font-mono text-xs text-hud-text-dim">Aucun lien.</p>
        ) : (
          <ul className="flex flex-col gap-2">
            {links.map((l) => {
              const cfg = PROVIDERS[l.provider];
              const href = cfg?.url(l.value) ?? null;
              return (
                <li key={l.id} className="flex items-center justify-between gap-3 font-mono text-sm">
                  <span className="flex items-center gap-2">
                    <span className="text-hud-text-dim">{cfg?.label ?? l.provider}</span>
                    {href ? (
                      <a
                        href={href}
                        target="_blank"
                        rel="noopener noreferrer"
                        className="text-hud-cyan hover:underline"
                      >
                        {l.value} ↗
                      </a>
                    ) : (
                      <span className="text-hud-cyan">{l.value}</span>
                    )}
                  </span>
                  {canModify(l) && (
                    <button
                      type="button"
                      className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim hover:text-hud-red disabled:opacity-50"
                      disabled={busy}
                      onClick={() => remove(l)}
                    >
                      suppr
                    </button>
                  )}
                </li>
              );
            })}
          </ul>
        )}

        <div className="flex flex-wrap items-end gap-2 border-t border-hud-cyan/10 pt-3">
          <label className="flex flex-col gap-1 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
            Fournisseur
            <select value={provider} onChange={(e) => setProvider(e.target.value)} className={fieldCls}>
              {PROVIDER_KEYS.map((k) => (
                <option key={k} value={k}>
                  {PROVIDERS[k]?.label}
                </option>
              ))}
            </select>
          </label>
          <label className="flex flex-1 flex-col gap-1 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
            Valeur
            <input
              type="text"
              value={value}
              onChange={(e) => setValue(e.target.value)}
              placeholder={PROVIDERS[provider]?.hint}
              className={fieldCls}
            />
          </label>
          <HudButton type="button" disabled={busy || !value.trim()} onClick={() => add(provider, value)}>
            {busy ? "…" : "AJOUTER"}
          </HudButton>
        </div>
      </div>
    </HudPanel>
  );
}
