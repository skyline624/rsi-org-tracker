import { definePluginSettings } from "@api/Settings";
import { OptionType } from "@utils/types";

import { Controls } from "./settings";
import { DEFAULT_AUTO_SYNC_MINUTES } from "./lib/autoSync";

// The API key deliberately has no settings definition: settings are cloud-synchronised.
export const settings = definePluginSettings({
  trackerUrl: { type: OptionType.CUSTOM, default: "" },
  fingerprint: { type: OptionType.CUSTOM, default: "" },
  trackedGuildIds: { type: OptionType.CUSTOM, default: [] as string[] },
  autoSyncEnabled: { type: OptionType.CUSTOM, default: false },
  autoSyncIntervalMinutes: { type: OptionType.CUSTOM, default: DEFAULT_AUTO_SYNC_MINUTES },
  controls: { type: OptionType.COMPONENT, component: Controls },
});
