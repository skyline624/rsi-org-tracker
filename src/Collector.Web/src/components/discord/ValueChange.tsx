import { DiscordText } from "./DiscordText";

/** Old → new value of a nick, name or handle change. */
export function ValueChange({ oldValue, newValue }: { oldValue: string | null; newValue: string | null }) {
  return (
    <span className="inline-flex min-w-0 items-center gap-2 text-hud-text-dim">
      <span className="inline-flex min-w-0 max-w-[12rem] text-hud-red/80 line-through">
        <DiscordText value={oldValue} />
      </span>
      <span aria-hidden>→</span>
      <span className="inline-flex min-w-0 max-w-[12rem] text-hud-green">
        <DiscordText value={newValue} />
      </span>
    </span>
  );
}
