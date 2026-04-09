import { HudPanel } from "@/components/hud/HudPanel";
import { HudBadge } from "@/components/hud/HudBadge";
import { getSession } from "@/lib/auth/session";
import { formatDate } from "@/lib/utils/format";

export default async function SettingsPage() {
  const session = await getSession();
  if (!session) return null; // layout already redirects

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

      <HudPanel label="API KEYS" accent="orange">
        <p className="py-4 text-center font-mono text-xs text-hud-text-dim">
          — API key management UI arriving in v2 —
          <br />
          Use the /api/api-keys endpoints directly for now.
        </p>
      </HudPanel>

      <HudPanel label="SECURITY">
        <p className="py-4 text-center font-mono text-xs text-hud-text-dim">
          — password change form arriving in v2 —
          <br />
          Use /api/auth/forgot-password for now.
        </p>
      </HudPanel>

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
