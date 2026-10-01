import { cleanDiscordText } from "@/lib/discord/format";
import { cn } from "@/lib/utils/cn";

interface DiscordTextProps {
  /** A name typed by a Discord user (server, role, account or nick). */
  value: string | null | undefined;
  /** Shown when the value is empty or has nothing visible. */
  fallback?: string;
  className?: string;
}

/**
 * A Discord-supplied name as inert text. React escapes it, so HTML and markdown stay
 * literal; bidi controls and zero-width spaces are removed; <bdi> keeps its direction
 * from reordering the row around it; CSS truncates it with an ellipsis, so a very long
 * name never widens a table. The whole value stays in the tooltip.
 */
export function DiscordText({ value, fallback = "—", className }: DiscordTextProps) {
  const text = cleanDiscordText(value) ?? fallback;
  return (
    <bdi title={text} className={cn("inline-block max-w-full truncate align-bottom", className)}>
      {text}
    </bdi>
  );
}
