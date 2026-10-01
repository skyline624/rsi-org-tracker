import { tabHref } from "@/lib/discord/params";

/** Shown by the gaps and suggestions tabs while the server is not mapped to an org. */
export function UnmappedNotice({ guildId }: { guildId: string }) {
  return (
    <p className="font-mono text-xs text-hud-orange">
      {"Relie d'abord ce serveur à une corpo "}
      <a href={tabHref(guildId, "config")} className="underline hover:text-hud-cyan">
        (onglet config)
      </a>
    </p>
  );
}
