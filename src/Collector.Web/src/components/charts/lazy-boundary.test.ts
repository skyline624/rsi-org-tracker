import { readdirSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const srcDir = fileURLToPath(new URL("../..", import.meta.url));
const chartsDir = join(srcDir, "components", "charts");

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return sourceFiles(path);
    return /\.(ts|tsx)$/.test(entry.name) && !/\.test\.ts$/.test(entry.name) ? [path] : [];
  });
}

describe("charts", () => {
  // recharts is ~100 kB of JavaScript: pages load it lazily, through
  // components/charts/lazy, only when a chart is on screen.
  it("are imported through the lazy module only", () => {
    const offenders = sourceFiles(srcDir)
      .filter((file) => !file.startsWith(chartsDir))
      .filter((file) => {
        const code = readFileSync(file, "utf8");
        return /from\s+["']recharts["']/.test(code)
          || /from\s+["']@\/components\/charts\/(?!lazy["'])[^"']+["']/.test(code);
      });

    expect(offenders.map((f) => f.slice(srcDir.length))).toEqual([]);
  });
});
