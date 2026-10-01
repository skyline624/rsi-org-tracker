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

/** A password as typed: bounded here, its rules are the API's. */
export const passwordSchema = z.string().min(1).max(200);

/** Discord bot token (admin setting). */
export const discordTokenSchema = z.string().trim().min(20).max(200);

/** API key name (the API stores up to 100 characters). */
export const apiKeyNameSchema = z.string().trim().min(1).max(100);

/** Lifetime of a Discord ingest key, in whole days: the API refuses more than 365. */
export const ingestKeyExpiryDaysSchema = z.number().int().min(1).max(365);

/** Discord id (account, server or role): a snowflake, carried as text from end to end. */
export const snowflakeSchema = z.string().regex(/^[0-9]{17,20}$/);

/** Order of a Discord rank role (the API accepts 0 to 1000); null keeps or clears it. */
export const rankOrderSchema = z.number().int().min(0).max(1000).nullable();

/** RSI rank a Discord rank stands for (the API stores up to 100 characters); null clears it. */
export const rsiRankLabelSchema = z.string().trim().max(100).nullable();

/** Organization search box. */
export const searchQuerySchema = z.string().max(100);

/** Citizen number (UEE Citizen Record). */
export const citizenIdSchema = z.number().int().positive().max(2_147_483_647);

/** An image URL shown on the site: http(s) only, never javascript: or data:. */
export const imageUrlSchema = z.string().trim().max(2000).url().refine((u) => /^https?:\/\//i.test(u));

/** Manually added person: fields left empty by the form arrive undefined. */
export const entityInputSchema = z.object({
  handle: handleSchema.optional(),
  displayName: z.string().trim().min(1).max(100).optional(),
  citizenId: citizenIdSchema.optional(),
});

/** Manually added organization. */
export const organizationInputSchema = z.object({
  sid: sidSchema,
  name: z.string().trim().min(1).max(100),
  urlImage: imageUrlSchema.optional(),
  archetype: z.string().trim().max(50).optional(),
  description: z.string().max(10_000).optional(),
});

/** Account created by an admin. */
export const accountInputSchema = z.object({
  username: z.string().trim().min(1).max(100),
  email: z.string().trim().max(200).email(),
  password: passwordSchema,
  isAdmin: z.boolean(),
});

/** Admin / banned flags of an account. */
export const userFlagsSchema = z.object({
  isAdmin: z.boolean().optional(),
  isBanned: z.boolean().optional(),
});

export const INVALID_ARGUMENTS = "Paramètres invalides.";

export function valid(schema: z.ZodType, value: unknown): boolean {
  return schema.safeParse(value).success;
}
