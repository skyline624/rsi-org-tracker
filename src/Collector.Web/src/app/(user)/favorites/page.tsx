"use client";
import Link from "next/link";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { HudBadge } from "@/components/hud/HudBadge";
import { useFavorites } from "@/lib/hooks/useFavorites";

export default function FavoritesPage() {
  const { orgs, users, toggleOrg, toggleUser, clear } = useFavorites();

  return (
    <div className="flex flex-col gap-6">
      <div className="flex items-center justify-between">
        <p className="font-mono text-xs text-hud-text-dim">
          Stored locally in your browser. Clearing your site data will remove
          all favorites.
        </p>
        <HudButton variant="danger" onClick={clear} disabled={orgs.length + users.length === 0}>
          CLEAR ALL
        </HudButton>
      </div>

      <section className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <HudPanel label={`ORGS · ${orgs.length}`}>
          {orgs.length === 0 ? (
            <p className="py-4 text-center font-mono text-xs text-hud-text-dim">
              — empty —
            </p>
          ) : (
            <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
              {orgs.map((sid) => (
                <li
                  key={sid}
                  className="flex items-center justify-between py-2"
                >
                  <Link
                    href={`/orgs/${sid}`}
                    className="text-hud-cyan hover:text-hud-orange"
                  >
                    {sid}
                  </Link>
                  <div className="flex items-center gap-2">
                    <HudBadge tone="dim">ORG</HudBadge>
                    <button
                      onClick={() => toggleOrg(sid)}
                      className="text-hud-red hover:text-hud-orange"
                    >
                      REMOVE
                    </button>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </HudPanel>

        <HudPanel label={`CITIZENS · ${users.length}`}>
          {users.length === 0 ? (
            <p className="py-4 text-center font-mono text-xs text-hud-text-dim">
              — empty —
            </p>
          ) : (
            <ul className="flex flex-col divide-y divide-hud-cyan/10 font-mono text-xs">
              {users.map((h) => (
                <li
                  key={h}
                  className="flex items-center justify-between py-2"
                >
                  <Link
                    href={`/users/${h}`}
                    className="text-hud-cyan hover:text-hud-orange"
                  >
                    @{h}
                  </Link>
                  <div className="flex items-center gap-2">
                    <HudBadge tone="dim">USER</HudBadge>
                    <button
                      onClick={() => toggleUser(h)}
                      className="text-hud-red hover:text-hud-orange"
                    >
                      REMOVE
                    </button>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </HudPanel>
      </section>
    </div>
  );
}
