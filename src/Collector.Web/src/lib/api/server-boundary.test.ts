import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const srcDir = fileURLToPath(new URL("../..", import.meta.url));

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return sourceFiles(path);
    return /\.(ts|tsx)$/.test(entry.name) && !/\.test\.ts$/.test(entry.name) ? [path] : [];
  });
}

describe("API client boundary", () => {
  // The browser cannot reach the API (it listens on the server's loopback) and must
  // never hold a token: data is fetched in server components / actions only.
  it("is never imported by a client component", () => {
    const offenders = sourceFiles(srcDir).filter((file) => {
      const code = readFileSync(file, "utf8");
      return (
        /^\s*["']use client["']/m.test(code) &&
        /from\s+["']@\/lib\/api\/(client|endpoints)["']/.test(code)
      );
    });

    expect(offenders.map((f) => f.slice(srcDir.length))).toEqual([]);
  });
});
