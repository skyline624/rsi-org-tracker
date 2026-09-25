/**
 * Runtime checks for Server Action arguments. TypeScript types vanish at run time:
 * any client can call an action with arbitrary values, and ids / handles / SIDs end
 * up in API paths.
 */

import { z } from "zod";

/** RSI handle (max 50 characters in the tracked data). */
export const handleSchema = z.string().regex(/^[A-Za-z0-9_-]{1,60}$/);

/** Organization SID (RSI: at most 10 characters). */
export const sidSchema = z.string().trim().regex(/^[A-Za-z0-9_-]{1,10}$/);

/** Database id. */
export const idSchema = z.number().int().positive().max(Number.MAX_SAFE_INTEGER);

/** Note text (the API stores up to 10 000 characters). */
export const noteBodySchema = z.string().trim().min(1).max(10_000);

/** Link providers accepted by the API (LinkProviders). */
export const linkProviderSchema = z.enum(["uex", "discord", "twitch"]);

export const INVALID_ARGUMENTS = "Paramètres invalides.";

export function valid(schema: z.ZodType, value: unknown): boolean {
  return schema.safeParse(value).success;
}
