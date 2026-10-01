import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const srcDir = fileURLToPath(new URL("../..", import.meta.url));

/**
 * Everything that renders names typed by Discord users (server, role, account and nick
 * names), relative to src/. A folder is scanned recursively.
 */
const DISCORD_UI = [
  "app/(public)/discord",
  "components/discord",
  "lib/discord",
  "app/(public)/users/[handle]/DiscordServersSection.tsx",
  "app/(public)/orgs/[sid]/OrgDiscordPanel.tsx",
];

function sourceFiles(path: string): string[] {
  if (statSync(path).isFile()) return [path];
  return readdirSync(path, { withFileTypes: true }).flatMap((entry) => {
    const child = join(path, entry.name);
    if (entry.isDirectory()) return sourceFiles(child);
    return /\.(ts|tsx)$/.test(entry.name) && !/\.test\.ts$/.test(entry.name) ? [child] : [];
  });
}

describe("Discord names render as inert text", () => {
  const roots = DISCORD_UI.map((path) => join(srcDir, path));

  it("scans paths that exist", () => {
    expect(DISCORD_UI.filter((_, i) => !existsSync(roots[i]!))).toEqual([]);
    expect(roots.filter((root) => existsSync(root)).flatMap(sourceFiles).length).toBeGreaterThan(0);
  });

  it("never injects HTML", () => {
    const offenders = roots
      .filter((root) => existsSync(root))
      .flatMap(sourceFiles)
      .filter((file) =>
        /dangerouslySetInnerHTML|\.innerHTML\b|\.outerHTML\b|insertAdjacentHTML/.test(readFileSync(file, "utf8")),
      );

    expect(offenders.map((file) => file.slice(srcDir.length))).toEqual([]);
  });

  it("cuts long names with CSS and isolates their direction", () => {
    const code = readFileSync(join(srcDir, "components", "discord", "DiscordText.tsx"), "utf8");

    expect(code).toMatch(/<bdi\b/);
    expect(code).toMatch(/\btruncate\b/);
    expect(code).toMatch(/cleanDiscordText\(/);
  });

  it("renders the names Discord users type only through DiscordText", () => {
    // `>{member.nick}` put straight into JSX would skip the cleaning, the <bdi> and the CSS cut.
    const rawName =
      />\s*\{\s*[\w.?!]*\.(?:name|nick|username|globalName|guildName|discordName|matchedToken|discordRank)\s*\}/;
    const offenders = roots
      .filter((root) => existsSync(root))
      .flatMap(sourceFiles)
      .filter((file) => file.endsWith(".tsx") && rawName.test(readFileSync(file, "utf8")));

    expect(offenders.map((file) => file.slice(srcDir.length))).toEqual([]);
  });
});
