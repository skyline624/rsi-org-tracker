import { describe, expect, it } from "vitest";
import { parsePage, withPage } from "./page-param";

describe("parsePage", () => {
  it.each([
    [undefined, 1],
    ["", 1],
    ["abc", 1],
    ["0", 1],
    ["-4", 1],
    ["2.7", 2],
    ["12", 12],
  ])("%s → %d", (raw, expected) => {
    expect(parsePage(raw)).toBe(expected);
  });
});

describe("withPage", () => {
  it("sets its own parameter and keeps the others", () => {
    expect(withPage("members=3&tab=x", "former", 2)).toBe("members=3&tab=x&former=2");
    expect(withPage("page=4", "page", 5)).toBe("page=5");
  });

  it("drops the parameter for the first page", () => {
    expect(withPage("page=4&search=a", "page", 1)).toBe("search=a");
  });
});
