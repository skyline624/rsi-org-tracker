// Copies the ScTracker plugin into a Vencord checkout:
//   node vencord/install.mjs <path to Vencord>
// then, in Vencord: pnpm build, pnpm inject. Rerun it after every plugin update.
//
// The folder is copied, never linked: esbuild resolves Vencord's path aliases (@api, @utils,
// @webpack) from the real location of each file, so a symlinked plugin would not build.
import { randomUUID } from "node:crypto";
import { cpSync, existsSync, lstatSync, mkdirSync, readdirSync, readFileSync, realpathSync, renameSync, rmSync } from "node:fs";
import { isAbsolute, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const PLUGIN_DIR = "scTracker.desktop";

/** Throw so every staged copy is cleaned before the CLI exits. */
function fail(message) {
  throw new Error(message);
}

function install() {
const source = fileURLToPath(new URL(`./${PLUGIN_DIR}`, import.meta.url));
if (!existsSync(source)) fail(`Dossier du plugin introuvable : ${source}`);

const vencordArg = process.argv[2];
if (!vencordArg) fail("Usage : node vencord/install.mjs <chemin de Vencord>");
let vencord = resolve(vencordArg);

let packageName;
try {
  packageName = JSON.parse(readFileSync(join(vencord, "package.json"), "utf8"))?.name;
} catch {
  packageName = undefined;
}
if (packageName !== "vencord") {
  fail(`${vencord} n'est pas un dépôt Vencord (package.json absent ou dont le nom n'est pas « vencord »).`);
}

vencord = realpathSync(vencord);
/** Validate every computed replacement path against the explicitly supplied checkout. */
function assertInsideCheckout(candidate) {
  const rel = relative(vencord, candidate);
  if (rel === "" || isAbsolute(rel) || rel === ".." || rel.startsWith(`..${sep}`)) {
    fail(`Chemin de copie dangereux : ${candidate} sort du dépôt Vencord.`);
  }
}

/** Refuse junctions/symlinks before creating a directory or replacing an existing copy. */
function assertSafeExistingPath(candidate) {
  assertInsideCheckout(candidate);
  let stat;
  try {
    stat = lstatSync(candidate);
  } catch (error) {
    if (error.code === "ENOENT") return;
    throw error;
  }
  if (stat.isSymbolicLink() || !stat.isDirectory()) {
    fail(`Chemin de copie dangereux : ${candidate} doit être un dossier ordinaire, sans lien symbolique.`);
  }
  assertInsideCheckout(realpathSync(candidate));
}

const src = join(vencord, "src");
const userplugins = join(vencord, "src", "userplugins");
const target = join(userplugins, PLUGIN_DIR);
assertSafeExistingPath(src);
assertSafeExistingPath(userplugins);
assertSafeExistingPath(target);
const sourceReal = realpathSync(source);
const sourceToTarget = relative(sourceReal, target);
const targetToSource = relative(target, sourceReal);
const isContained = rel => rel === "" || (!isAbsolute(rel) && rel !== ".." && !rel.startsWith(`..${sep}`));
if (isContained(sourceToTarget) || isContained(targetToSource)) {
  fail("Chemin de copie dangereux : la source du plugin et sa destination se recouvrent.");
}

// Keep all staging out of src: Vencord recursively builds native.ts under userplugins.
const staging = join(vencord, `.scTracker-install-${randomUUID()}.tmp`);
assertInsideCheckout(staging);
let copyError;
try {
  mkdirSync(userplugins, { recursive: true });
  // Old installer versions could leave an abandoned plugin-shaped directory in src.
  for (const name of readdirSync(userplugins)) {
    if (!/^\.scTracker\.desktop-[0-9a-f-]{36}\.tmp$/.test(name)) continue;
    const abandoned = join(userplugins, name);
    assertSafeExistingPath(abandoned);
    rmSync(abandoned, { recursive: true, force: true });
  }
  // Copy first: an unreadable source must not delete the previous installation.
  cpSync(source, staging, { recursive: true, dereference: false });
  assertSafeExistingPath(userplugins);
  assertSafeExistingPath(target);
  rmSync(target, { recursive: true, force: true });
  renameSync(staging, target);
} catch (error) {
  copyError = error;
} finally {
  if (existsSync(staging)) {
    try {
      assertSafeExistingPath(staging);
      rmSync(staging, { recursive: true, force: true });
    } catch (error) { console.error(`Nettoyage du dossier temporaire impossible : ${error.message}`); }
  }
}
if (copyError) fail(`Impossible de copier le plugin : ${copyError.message}`);

console.log(`Dossier ScTracker copié dans ${target}.`);
if (existsSync(join(source, "index.tsx")) || existsSync(join(source, "index.ts"))) {
  console.log(`Ensuite, dans ${vencord} : pnpm build, puis pnpm inject, puis redémarre complètement Discord.`);
} else {
  console.log("Le plugin est incomplet : ses bibliothèques sont prêtes, mais son point d'entrée reste à implémenter.");
  console.log("pnpm build et pnpm inject ne permettront de l'activer qu'après cette implémentation.");
}
}

try { install(); }
catch (error) { console.error(error.message); process.exitCode = 1; }
