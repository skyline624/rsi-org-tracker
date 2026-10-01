import type { NavContextMenuPatchCallback } from "@api/ContextMenu";
import definePlugin from "@utils/types";
import type { Guild } from "@vencord/discord-types";
import { Menu, React } from "@webpack/common";

import { settings } from "./configuration";
import { isTracked, runner, start, stop, trackGuild } from "./controller";
import { noncePatches } from "./lib/noncePatches";
import style from "./style.css?managed";

const guildMenu: NavContextMenuPatchCallback = (children, { guild }: { guild?: Guild }) => {
  if (!guild) return;
  const id = guild.id;
  children.push(<Menu.MenuGroup key="sc-tracker">
    <Menu.MenuItem id="sc-tracker-send" label="SC Tracker : envoyer ce serveur" disabled={runner.busy} action={() => void runner.run([id], { requireTracked: false })} />
    {isTracked(id) ?
      <Menu.MenuItem id="sc-tracker-untrack" label="SC Tracker : ne plus suivre" disabled={runner.busy} action={() => trackGuild(id, false)} />
      : <Menu.MenuItem id="sc-tracker-track" label="SC Tracker : suivre ce serveur" disabled={runner.busy} action={() => trackGuild(id, true)} />}
  </Menu.MenuGroup>);
};

export default definePlugin({
  name: "ScTracker",
  description: "Envoie les membres et rôles d'un serveur au SC Tracker, à la demande ou par minuteur pour les serveurs cochés.",
  authors: [{ name: "SC Tracker", id: 0n }],
  tags: ["Servers", "Utility"],
  patches: noncePatches,
  settings,
  managedStyle: style,
  start,
  stop,
  contextMenus: { "guild-context": guildMenu },
});
