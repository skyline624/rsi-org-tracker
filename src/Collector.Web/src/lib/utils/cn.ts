import clsx, { type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";

/**
 * Concatène des classes Tailwind en fusionnant proprement les collisions.
 * Usage : className={cn("px-2", condition && "bg-hud-cyan")}.
 */
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}
