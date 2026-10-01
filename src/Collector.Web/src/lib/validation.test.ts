import { describe, expect, it } from "vitest";
import {
  apiKeyNameSchema,
  handleSchema,
  idSchema,
  ingestKeyExpiryDaysSchema,
  linkProviderSchema,
  noteBodySchema,
  rankOrderSchema,
  rsiRankLabelSchema,
  sidSchema,
  snowflakeSchema,
  valid,
} from "./validation";

describe("server action argument schemas", () => {
  it.each(["L66", "Shooting_Star", "n-380", "a".repeat(60)])("accepts handle %s", (h) => {
    expect(valid(handleSchema, h)).toBe(true);
  });

  it.each(["", "../admin", "a/b", "evil.example", "a".repeat(61), 42])("rejects handle %j", (h) => {
    expect(valid(handleSchema, h)).toBe(false);
  });

  it.each(["OPPF", "indepren", "BLCK-HRT_1"])("accepts SID %s", (s) => {
    expect(valid(sidSchema, s)).toBe(true);
  });

  it.each(["WAYTOOLONGSID", "a b", "../x", ""])("rejects SID %j", (s) => {
    expect(valid(sidSchema, s)).toBe(false);
  });

  it.each([1, 42, 9_007_199_254_740_991])("accepts id %s", (id) => {
    expect(valid(idSchema, id)).toBe(true);
  });

  it.each([0, -1, 1.5, "1", "../admin/users/3", Number.NaN, null])("rejects id %j", (id) => {
    expect(valid(idSchema, id)).toBe(false);
  });

  it("limits note bodies to what the API stores", () => {
    expect(valid(noteBodySchema, "note")).toBe(true);
    expect(valid(noteBodySchema, "   ")).toBe(false);
    expect(valid(noteBodySchema, "x".repeat(10_001))).toBe(false);
  });

  it("only knows the link providers the API accepts", () => {
    expect(valid(linkProviderSchema, "twitch")).toBe(true);
    expect(valid(linkProviderSchema, "javascript")).toBe(false);
  });

  it("bounds API key names to what the API stores", () => {
    expect(valid(apiKeyNameSchema, "Vencord 2026-10-01")).toBe(true);
    expect(valid(apiKeyNameSchema, "x".repeat(100))).toBe(true);
    expect(valid(apiKeyNameSchema, "   ")).toBe(false);
    expect(valid(apiKeyNameSchema, "x".repeat(101))).toBe(false);
    expect(valid(apiKeyNameSchema, 42)).toBe(false);
  });

  it.each([1, 180, 365])("accepts an ingest key lifetime of %s days", (days) => {
    expect(valid(ingestKeyExpiryDaysSchema, days)).toBe(true);
  });

  it.each([0, 366, 1.5, -1, "180", Number.NaN, null])("rejects an ingest key lifetime of %j", (days) => {
    expect(valid(ingestKeyExpiryDaysSchema, days)).toBe(false);
  });

  it.each(["12345678901234567", "123456789012345678", "12345678901234567890"])("accepts snowflake %s", (id) => {
    expect(valid(snowflakeSchema, id)).toBe(true);
  });

  it.each([
    "1234567890123456",
    "123456789012345678901",
    "12345678901234567a",
    " 123456789012345678",
    "../123456789012345678",
    123456789012,
    null,
  ])("rejects snowflake %j", (id) => {
    expect(valid(snowflakeSchema, id)).toBe(false);
  });

  it.each([0, 12, 1000, null])("accepts rank order %j", (order) => {
    expect(valid(rankOrderSchema, order)).toBe(true);
  });

  it.each([-1, 1001, 1.5, "3", undefined, Number.NaN])("rejects rank order %j", (order) => {
    expect(valid(rankOrderSchema, order)).toBe(false);
  });

  it.each(["Officer", "  Director  ", "x".repeat(100), null])("accepts RSI rank label %j", (label) => {
    expect(valid(rsiRankLabelSchema, label)).toBe(true);
  });

  it.each(["x".repeat(101), 42, undefined])("rejects RSI rank label %j", (label) => {
    expect(valid(rsiRankLabelSchema, label)).toBe(false);
  });
});
