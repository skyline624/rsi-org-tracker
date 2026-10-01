import { HudPanel } from "@/components/hud/HudPanel";
import { HudBadge } from "@/components/hud/HudBadge";
import { getSession, sessionCtx } from "@/lib/auth/session";
import { apiGet } from "@/lib/api/client";
import { getDiscordIngestConfig, listApiKeys } from "@/lib/api/endpoints";
import { formatDate } from "@/lib/utils/format";
import { ChangePasswordForm } from "./ChangePasswordForm";
import { DiscordIngestKeysPanel } from "./DiscordIngestKeysPanel";
import { DiscordTokenForm } from "./DiscordTokenForm";

export default async function SettingsPage() {
  const session = await getSession();
  if (!session) return null; // layout already redirects

  const discordConfigured = session.isAdmin
    ? await apiGet<{ configured: boolean }>("/api/admin/discord-token", undefined, sessionCtx(session))
        .then((r) => r.configured)
        .catch(() => false)
    : false;

  // Neither is essential to the page: the panel says what is missing instead.
  const [ingestConfig, apiKeys] = await Promise.all([
    getDiscordIngestConfig(sessionCtx(session)).catch(() => null),
    listApiKeys(sessionCtx(session)).catch(() => null),
  ]);

  return (
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
      <HudPanel label="PROFILE">
        <dl className="flex flex-col gap-3 font-mono text-xs">
          <div className="flex items-center justify-between">
            <dt className="text-hud-text-dim">USERNAME</dt>
            <dd className="text-hud-cyan">{session.username}</dd>
          </div>
          <div className="flex items-center justify-between">
            <dt className="text-hud-text-dim">USER ID</dt>
            <dd className="text-hud-cyan">#{session.userId}</dd>
          </div>
          {session.email && (
            <div className="flex items-center justify-between">
              <dt className="text-hud-text-dim">EMAIL</dt>
              <dd className="text-hud-cyan">{session.email}</dd>
            </div>
          )}
          <div className="flex items-center justify-between">
            <dt className="text-hud-text-dim">ROLE</dt>
            <dd>
              {session.isAdmin ? (
                <HudBadge tone="orange">ADMIN</HudBadge>
              ) : (
                <HudBadge tone="dim">CITIZEN</HudBadge>
              )}
            </dd>
          </div>
          <div className="flex items-center justify-between">
            <dt className="text-hud-text-dim">SESSION EXPIRES</dt>
            <dd className="text-hud-cyan">
              {formatDate(session.expiresAt)}
            </dd>
          </div>
        </dl>
      </HudPanel>

      <DiscordIngestKeysPanel
        config={ingestConfig}
        keys={apiKeys}
        defaultName={`Vencord ${new Date().toISOString().slice(0, 10)}`}
      />

      <HudPanel label="SECURITY">
        <ChangePasswordForm />
      </HudPanel>

      {session.isAdmin && (
        <HudPanel label="BOT DISCORD" accent="orange">
          <DiscordTokenForm initialConfigured={discordConfigured} />
        </HudPanel>
      )}

      <HudPanel label="SESSION TOKENS" accent="red">
        <form action="/api/auth/logout" method="post">
          <p className="mb-3 font-mono text-xs text-hud-text-dim">
            Terminates the current session and revokes the refresh token.
          </p>
          <button
            type="submit"
            className="hud-clip border border-hud-red px-3 py-1.5 font-mono text-xs uppercase tracking-[0.15em] text-hud-red hover:bg-hud-red/10 hover:shadow-hud-glow-red"
          >
            TERMINATE SESSION
          </button>
        </form>
      </HudPanel>
    </div>
  );
}
