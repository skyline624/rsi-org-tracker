"use client";
import { useEffect, useState, type ReactNode } from "react";
import { useRouter } from "next/navigation";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudBadge } from "@/components/hud/HudBadge";
import { autoAddTwitchLinkAction } from "./link-actions";

interface UexData {
  id: number;
  username: string;
  avatarUrl?: string;
  discordUsername?: string;
  twitchUsername?: string;
  twitchVerified: boolean;
  websiteUrl?: string;
  timezone?: string;
  specializations: string[];
  isDatarunner: boolean;
  isStaff: boolean;
  rsiVerifiedAt?: number; // unix seconds; >0 means UEX confirmed this RSI handle owns the account
}

/**
 * UEX profile for a person, fetched by RSI handle.
 *
 * The call runs in the visitor's browser on purpose: UEX sits behind Cloudflare,
 * which blocks datacenter IPs (our server gets a 403 challenge) but lets
 * residential IPs through. CORS on the API is open (Allow-Origin: *). If UEX is
 * unreachable or the person has no UEX account, the panel simply hides itself.
 *
 * Trust: a UEX username is chosen freely, so it alone does NOT prove identity.
 * `date_rsi_verified` (non-zero) is UEX's proof that the account owner controls
 * this RSI handle — verified via the [UEX:NNN] code placed in the RSI bio.
 */
export function UexPanel({ handle }: { handle: string }) {
  const [uex, setUex] = useState<UexData | null>(null);
  const router = useRouter();

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const res = await fetch(
          `https://api.uexcorp.space/2.0/user?username=${encodeURIComponent(handle)}`,
          { signal: AbortSignal.timeout(8000) },
        );
        if (!res.ok) return;
        const json = await res.json();
        const d = json?.data;
        if (json?.status !== "ok" || !d || Array.isArray(d) || typeof d !== "object") return;
        if (cancelled) return;
        const data: UexData = {
          id: d.id,
          username: d.username ?? handle,
          avatarUrl: d.avatar || undefined,
          discordUsername: d.discord_username || undefined,
          twitchUsername: d.twitch_username || undefined,
          twitchVerified: Number(d.date_twitch_verified) > 0,
          websiteUrl: d.website_url || undefined,
          timezone: d.timezone || undefined,
          specializations: String(d.specializations || "")
            .split(",")
            .map((s: string) => s.trim())
            .filter(Boolean),
          isDatarunner: d.is_datarunner === 1,
          isStaff: d.is_staff === 1,
          rsiVerifiedAt: Number(d.date_rsi_verified) > 0 ? Number(d.date_rsi_verified) : undefined,
        };
        setUex(data);

        // Auto-add the verified Twitch channel to the person's links (idempotent).
        if (data.twitchUsername && data.twitchVerified) {
          const { added } = await autoAddTwitchLinkAction(handle, data.twitchUsername);
          if (added && !cancelled) router.refresh();
        }
      } catch {
        // Cloudflare block, timeout or network error → keep the panel hidden.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [handle, router]);

  if (!uex) return null;

  const verifiedDate = uex.rsiVerifiedAt
    ? new Date(uex.rsiVerifiedAt * 1000).toLocaleDateString("fr-FR")
    : null;
  const profileUrl = `https://uexcorp.space/@${encodeURIComponent(uex.username)}`;
  const website = uex.websiteUrl
    ? uex.websiteUrl.startsWith("http")
      ? uex.websiteUrl
      : `https://${uex.websiteUrl}`
    : null;

  return (
    <HudPanel label="UEX">
      <div className="flex flex-col gap-3">
        <div className="flex items-start gap-3">
          {uex.avatarUrl && (
            // eslint-disable-next-line @next/next/no-img-element -- external UEX CDN, loaded client-side
            <img
              src={uex.avatarUrl}
              alt=""
              width={48}
              height={48}
              referrerPolicy="no-referrer"
              className="rounded border border-hud-cyan/30 object-cover"
            />
          )}
          <div className="flex flex-wrap items-center gap-2">
            <HudBadge tone="green">COMPTE UEX #{uex.id}</HudBadge>
            {verifiedDate ? (
              <HudBadge tone="green">RSI VÉRIFIÉ · {verifiedDate}</HudBadge>
            ) : (
              <HudBadge tone="orange">⚠ NON VÉRIFIÉ RSI</HudBadge>
            )}
            {uex.isDatarunner && <HudBadge tone="orange">DATA RUNNER</HudBadge>}
            {uex.isStaff && <HudBadge tone="orange">STAFF UEX</HudBadge>}
          </div>
        </div>

        {!verifiedDate && (
          <p className="font-mono text-[10px] text-hud-orange/80">
            Ce compte UEX porte le même pseudo mais n'est pas vérifié RSI : le lien avec ce
            joueur n'est pas confirmé.
          </p>
        )}

        {uex.discordUsername && <Row label="Discord">{uex.discordUsername}</Row>}
        {uex.twitchUsername && (
          <Row label="Twitch">
            {uex.twitchVerified ? (
              <ExtLink href={`https://www.twitch.tv/${encodeURIComponent(uex.twitchUsername)}`}>
                {uex.twitchUsername}
              </ExtLink>
            ) : (
              <span className="text-hud-text-dim">{uex.twitchUsername} (non vérifié)</span>
            )}
          </Row>
        )}
        {website && (
          <Row label="Site">
            <ExtLink href={website}>{uex.websiteUrl}</ExtLink>
          </Row>
        )}
        {uex.timezone && <Row label="Fuseau">{uex.timezone}</Row>}

        {uex.specializations.length > 0 && (
          <div className="flex flex-col gap-1">
            <span className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
              Spécialités
            </span>
            <div className="flex flex-wrap gap-1">
              {uex.specializations.map((s) => (
                <span
                  key={s}
                  className="border border-hud-cyan/20 px-1.5 py-0.5 font-mono text-[10px] text-hud-text-dim"
                >
                  {s}
                </span>
              ))}
            </div>
          </div>
        )}

        <ExtLink
          href={profileUrl}
          className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim hover:text-hud-cyan"
        >
          voir le profil UEX
        </ExtLink>
      </div>
    </HudPanel>
  );
}

function Row({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-4 font-mono text-sm">
      <span className="text-[10px] uppercase tracking-wide text-hud-text-dim">{label}</span>
      <span className="text-right text-hud-text">{children}</span>
    </div>
  );
}

function ExtLink({
  href,
  children,
  className,
}: {
  href: string;
  children: ReactNode;
  className?: string;
}) {
  return (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      className={className ?? "text-hud-cyan hover:underline"}
    >
      {children} ↗
    </a>
  );
}
