import { spawnSync } from "node:child_process";
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

const INSTALL = fileURLToPath(new URL("../install.mjs", import.meta.url));
const PLUGIN = fileURLToPath(new URL("../scTracker.desktop", import.meta.url));

/** Every file under dir, as sorted "a/b.ts" paths. */
function listFiles(dir: string, prefix = ""): string[] {
  return readdirSync(dir, { withFileTypes: true })
    .flatMap(e => (e.isDirectory() ? listFiles(join(dir, e.name), `${prefix}${e.name}/`) : [`${prefix}${e.name}`]))
    .sort();
}

function install(...args: string[]) {
  return spawnSync(process.execPath, [INSTALL, ...args], { encoding: "utf8" });
}

describe("install.mjs", () => {
  let root: string;

  beforeEach(() => {
    root = mkdtempSync(join(tmpdir(), "sc-tracker-install-"));
  });

  afterEach(() => {
    rmSync(root, { recursive: true, force: true });
  });

  function fakeVencord(name = "vencord"): string {
    const dir = join(root, "Vencord");
    mkdirSync(join(dir, "src"), { recursive: true });
    writeFileSync(join(dir, "package.json"), JSON.stringify({ name, private: "true" }));
    return dir;
  }

  it("copies the plugin folder into src/userplugins, creating it in a fresh clone", () => {
    const vencord = fakeVencord();

    const run = install(vencord);

    expect(run.status, run.stderr).toBe(0);
    const target = join(vencord, "src", "userplugins", "scTracker.desktop");
    expect(lstatSync(target).isSymbolicLink()).toBe(false);
    expect(lstatSync(target).isDirectory()).toBe(true);
    expect(listFiles(target)).toEqual(listFiles(PLUGIN));
    expect(listFiles(target)).toContain("lib/fingerprint.ts");
    for (const file of listFiles(PLUGIN)) {
      expect(readFileSync(join(target, file), "utf8")).toBe(readFileSync(join(PLUGIN, file), "utf8"));
    }
    expect(run.stdout).toContain("pnpm build");
    expect(run.stdout).toContain("pnpm inject");
    expect(run.stdout).not.toContain("Le plugin est incomplet");
  });

  it("replaces the previous copy instead of merging into it", () => {
    const vencord = fakeVencord();
    const target = join(vencord, "src", "userplugins", "scTracker.desktop");
    mkdirSync(join(target, "lib"), { recursive: true });
    writeFileSync(join(target, "lib", "removedLongAgo.ts"), "export {};\n");

    const run = install(vencord);

    expect(run.status, run.stderr).toBe(0);
    expect(existsSync(join(target, "lib", "removedLongAgo.ts"))).toBe(false);
    expect(listFiles(target)).toEqual(listFiles(PLUGIN));
  });
  it("removes abandoned copies from the former staging location", () => {
    const vencord = fakeVencord();
    const plugins = join(vencord, "src", "userplugins");
    const stale = join(plugins, ".scTracker.desktop-00000000-0000-0000-0000-000000000000.tmp");
    mkdirSync(stale, { recursive: true });
    writeFileSync(join(stale, "native.ts"), "export {};\n");
    const run = install(vencord);
    expect(run.status, run.stderr).toBe(0);
    expect(readdirSync(plugins)).toEqual(["scTracker.desktop"]);
    expect(readdirSync(vencord).some(name => name.startsWith(".scTracker-install-"))).toBe(false);
  });
  it("cleans staging when the target becomes unsafe after copying", () => {
    const vencord = fakeVencord();
    const preload = join(root, "fail-after-copy.cjs");
    writeFileSync(preload, `
      const fs = require('node:fs'); const path = require('node:path');
      const copy = fs.cpSync;
      fs.cpSync = (...args) => {
        copy(...args);
        fs.writeFileSync(path.join(path.dirname(args[1]), 'src', 'userplugins', 'scTracker.desktop'), 'keep me');
      };
      require('node:module').syncBuiltinESMExports();
    `);
    const run = spawnSync(process.execPath, ["--require", preload, INSTALL, vencord], { encoding: "utf8" });
    expect(run.status).toBe(1);
    expect(run.stderr).toContain("Chemin de copie dangereux");
    expect(readFileSync(join(vencord, "src", "userplugins", "scTracker.desktop"), "utf8")).toBe("keep me");
    expect(readdirSync(vencord).some(name => name.startsWith(".scTracker-install-"))).toBe(false);
  });

  it("refuses a directory without package.json and writes nothing", () => {
    const notVencord = join(root, "empty");
    mkdirSync(notVencord);

    const run = install(notVencord);

    expect(run.status).toBe(1);
    expect(run.stderr).toContain("n'est pas un dépôt Vencord");
    expect(existsSync(join(notVencord, "src"))).toBe(false);
  });

  it("refuses another project's checkout and writes nothing", () => {
    const other = fakeVencord("collector-web");

    const run = install(other);

    expect(run.status).toBe(1);
    expect(run.stderr).toContain("n'est pas un dépôt Vencord");
    expect(existsSync(join(other, "src", "userplugins"))).toBe(false);
  });

  it("prints its usage when the Vencord path is missing", () => {
    const run = install();

    expect(run.status).toBe(1);
    expect(run.stderr).toContain("node vencord/install.mjs <chemin de Vencord>");
  });

  it.each(["src", "userplugins", "target"])("refuses a %s symlink or junction before replacing files", component => {
    const vencord = fakeVencord();
    const outside = join(root, "outside");
    mkdirSync(outside);
    const marker = join(outside, "keep.txt");
    writeFileSync(marker, "must survive");
    const src = join(vencord, "src");
    const userplugins = join(src, "userplugins");
    let link: string;
    if (component === "src") {
      rmSync(src, { recursive: true });
      link = src;
    } else if (component === "userplugins") {
      link = userplugins;
    } else {
      mkdirSync(userplugins);
      link = join(userplugins, "scTracker.desktop");
    }
    symlinkSync(outside, link, process.platform === "win32" ? "junction" : "dir");

    const run = install(vencord);

    expect(run.status).toBe(1);
    expect(run.stderr).toContain("Chemin de copie dangereux");
    expect(readFileSync(marker, "utf8")).toBe("must survive");
    expect(readdirSync(outside)).toEqual(["keep.txt"]);
    expect(lstatSync(link).isSymbolicLink()).toBe(true);
  });

  it("refuses a file at the replacement path without removing it", () => {
    const vencord = fakeVencord();
    const userplugins = join(vencord, "src", "userplugins");
    mkdirSync(userplugins);
    const target = join(userplugins, "scTracker.desktop");
    writeFileSync(target, "keep this file");

    const run = install(vencord);

    expect(run.status).toBe(1);
    expect(run.stderr).toContain("Chemin de copie dangereux");
    expect(readFileSync(target, "utf8")).toBe("keep this file");
  });
});
