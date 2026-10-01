"use client";
import { useState, type FormEvent } from "react";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { HudBadge } from "@/components/hud/HudBadge";
import { HudButton } from "@/components/hud/HudButton";
import { HudDataGrid, type HudColumn } from "@/components/hud/HudDataGrid";
import { HudInput } from "@/components/hud/HudInput";
import { HudPanel } from "@/components/hud/HudPanel";
import type { ApiKeyDto, DiscordIngestConfigDto } from "@/lib/api/types";
import { formatDate } from "@/lib/utils/format";
import { createDiscordIngestKeyAction, revokeApiKeyAction } from "./discord-key-actions";

const DEFAULT_EXPIRY_DAYS = 180;
const MAX_EXPIRY_DAYS = 365;

interface DiscordIngestKeysPanelProps {
  /** Plugin settings published by the API; null when they could not be read. */
  config: DiscordIngestConfigDto | null;
  /** The user's API keys, newest first; null when the list could not be read. */
  keys: ApiKeyDto[] | null;
  /** "Vencord <date>", computed by the page so the server and the browser render the same value. */
  defaultName: string;
}

async function copyToClipboard(value: string, what: string) {
  try {
    await navigator.clipboard.writeText(value);
    toast.success(`${what} copiée.`);
  } catch {
    toast.error("Copie impossible : sélectionne le texte et copie-le à la main.");
  }
}

/** A value to paste into the plugin, with its copy button. */
function CopyField({ label, value, what }: { label: string; value: string; what: string }) {
  return (
    <div className="flex flex-col gap-1">
      <span className="hud-label">{label}</span>
      <div className="flex items-center gap-2">
        <code className="min-w-0 flex-1 break-all border border-hud-cyan-dim bg-hud-bg/60 px-2 py-1 font-mono text-xs text-hud-cyan">
          {value}
        </code>
        <HudButton type="button" variant="ghost" onClick={() => copyToClipboard(value, what)}>
          COPIER
        </HudButton>
      </div>
    </div>
  );
}

function scopeBadge(scope: string | null) {
  if (scope === null) return <HudBadge tone="orange">COMPLÈTE</HudBadge>;
  if (scope === "discord:ingest") return <HudBadge tone="cyan">ENVOI DISCORD</HudBadge>;
  return <HudBadge tone="dim">{scope}</HudBadge>;
}

function isExpired(key: ApiKeyDto) {
  return key.expiresAt !== null && new Date(key.expiresAt).getTime() <= Date.now();
}

/**
 * Settings panel for the Vencord plugin: what to enter in it (URL and certificate
 * fingerprint), a form that creates a discord:ingest key shown once, and the user's
 * keys with a revoke button. Creating and revoking go through server actions; the list
 * comes from the page and is read again with router.refresh().
 */
export function DiscordIngestKeysPanel({ config, keys, defaultName }: DiscordIngestKeysPanelProps) {
  const router = useRouter();
  const [creating, setCreating] = useState(false);
  const [revokingId, setRevokingId] = useState<number | null>(null);
  const [rawKey, setRawKey] = useState<string | null>(null);

  const publicUrl = config?.publicUrl ?? null;
  const fingerprint = config?.certificateSha256 ?? null;

  async function create(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const form = new FormData(e.currentTarget);
    const name = String(form.get("name") ?? "").trim();
    const days = Number(form.get("expiresInDays"));
    if (!Number.isInteger(days) || days < 1 || days > MAX_EXPIRY_DAYS) {
      toast.error(`Expiration : un nombre entier de jours, de 1 à ${MAX_EXPIRY_DAYS}.`);
      return;
    }
    setCreating(true);
    const res = await createDiscordIngestKeyAction(name, days);
    setCreating(false);
    if (res.ok && res.data) {
      setRawKey(res.data.rawKey);
      toast.success("Clé créée.");
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function revoke(key: ApiKeyDto) {
    if (!confirm(`Révoquer la clé « ${key.name} » ? Le plugin qui l'utilise ne pourra plus rien envoyer.`)) return;
    setRevokingId(key.id);
    const res = await revokeApiKeyAction(key.id);
    setRevokingId(null);
    if (res.ok) {
      toast.success("Clé révoquée.");
      router.refresh();
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  const columns: HudColumn<ApiKeyDto>[] = [
    { key: "name", header: "NOM", render: (k) => <span className="break-all text-hud-text">{k.name}</span> },
    { key: "scope", header: "PORTÉE", width: "w-32", render: (k) => scopeBadge(k.scope) },
    { key: "createdAt", header: "CRÉÉE LE", width: "w-28", render: (k) => formatDate(k.createdAt) },
    {
      key: "expiresAt",
      header: "EXPIRE LE",
      width: "w-28",
      render: (k) => (k.expiresAt ? formatDate(k.expiresAt) : "jamais"),
    },
    {
      key: "lastUsedAt",
      header: "DERNIÈRE UTILISATION",
      width: "w-32",
      render: (k) => (k.lastUsedAt ? formatDate(k.lastUsedAt) : "jamais"),
    },
    {
      key: "revoke",
      header: "",
      width: "w-28",
      align: "right",
      render: (k) =>
        k.isRevoked ? (
          <HudBadge tone="dim">RÉVOQUÉE</HudBadge>
        ) : isExpired(k) ? (
          <HudBadge tone="red">EXPIRÉE</HudBadge>
        ) : (
          <HudButton
            type="button"
            variant="danger"
            className="px-2 py-1"
            disabled={revokingId === k.id}
            onClick={() => revoke(k)}
          >
            {revokingId === k.id ? "…" : "RÉVOQUER"}
          </HudButton>
        ),
    },
  ];

  return (
    <HudPanel label="CLÉ D'ENVOI DISCORD" accent="orange">
      <div className="flex flex-col gap-6">
        <section className="flex flex-col gap-3">
          <div className="hud-label text-hud-text-dim">CONFIGURATION DU PLUGIN</div>
          {publicUrl && fingerprint ? (
            <>
              <CopyField label="URL DU TRACKER" value={publicUrl} what="URL" />
              <CopyField label="EMPREINTE SHA-256 DU CERTIFICAT" value={fingerprint} what="Empreinte" />
            </>
          ) : (
            <p className="font-mono text-xs text-hud-orange">
              {"Demande l'URL et l'empreinte à l'administrateur."}
            </p>
          )}
        </section>

        <section className="flex flex-col gap-3">
          <div className="hud-label text-hud-text-dim">CRÉER UNE CLÉ</div>
          <form onSubmit={create} className="flex flex-col gap-3">
            <div className="grid gap-3 sm:grid-cols-[1fr_11rem]">
              <HudInput
                label="NOM"
                className="bg-hud-bg"
                name="name"
                type="text"
                required
                maxLength={100}
                autoComplete="off"
                defaultValue={defaultName}
              />
              <HudInput
                label={`EXPIRATION (JOURS, ≤ ${MAX_EXPIRY_DAYS})`}
                className="bg-hud-bg"
                name="expiresInDays"
                type="number"
                required
                min={1}
                max={MAX_EXPIRY_DAYS}
                step={1}
                defaultValue={DEFAULT_EXPIRY_DAYS}
              />
            </div>
            <div className="flex justify-end">
              <HudButton type="submit" disabled={creating}>
                {creating ? "…" : "CRÉER LA CLÉ"}
              </HudButton>
            </div>
          </form>
          {rawKey && (
            <div className="flex flex-col gap-2 border border-hud-orange/60 bg-hud-orange/5 p-3">
              <p className="font-mono text-xs text-hud-orange">
                {"Copie cette clé et colle-la dans le plugin maintenant : elle ne sera plus jamais affichée."}
              </p>
              <CopyField label="CLÉ D'API" value={rawKey} what="Clé" />
              <div className="flex justify-end">
                <HudButton type="button" variant="ghost" onClick={() => setRawKey(null)}>
                  {"J'AI COPIÉ LA CLÉ"}
                </HudButton>
              </div>
            </div>
          )}
        </section>

        <section className="flex flex-col gap-3">
          <div className="hud-label text-hud-text-dim">MES CLÉS</div>
          {keys ? (
            <HudDataGrid minWidth={880} columns={columns} rows={keys} rowKey={(k) => String(k.id)} empty="Aucune clé." />
          ) : (
            <p className="font-mono text-xs text-hud-red">Liste des clés indisponible.</p>
          )}
          <p className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
            {"Rotation : crée une nouvelle clé, colle-la dans le plugin, puis révoque l'ancienne."}
          </p>
        </section>
      </div>
    </HudPanel>
  );
}
