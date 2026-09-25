import { describe, expect, it } from "vitest";
import {
  handleSchema,
  idSchema,
  linkProviderSchema,
  noteBodySchema,
  sidSchema,
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
});
