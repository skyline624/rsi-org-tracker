"use client";
import { useEffect, useId, useState, type FormEvent, type KeyboardEvent } from "react";
import { useRouter } from "next/navigation";
import { toast } from "sonner";
import { DiscordText } from "@/components/discord/DiscordText";
import { HudButton } from "@/components/hud/HudButton";
import { HudInput } from "@/components/hud/HudInput";
import { reportDiscordActionError } from "@/lib/discord/action-error";
import { cleanDiscordText } from "@/lib/discord/format";
import { sidSchema } from "@/lib/validation";
import { mapGuildOrgAction, searchGuildOrgsAction, type GuildOrgOption } from "./actions";

interface GuildOrgFormProps {
  guildId: string;
  guildName: string;
  currentSid: string | null;
  currentOrgName?: string | null;
  canEdit: boolean;
}

const MIN_SEARCH_LENGTH = 1;
const MAX_SEARCH_LENGTH = 100;
const orgLabel = (org: GuildOrgOption) => `${cleanDiscordText(org.name) ?? org.sid} [${org.sid}]`;

/** Suggest a query from the server name, removing decorations such as ⭐ at its edges. */
function guildQuery(name: string) {
  return (cleanDiscordText(name) ?? "")
    .normalize("NFKC")
    .replace(/^[^\p{L}\p{N}]+|[^\p{L}\p{N}]+$/gu, "")
    .trim().slice(0, MAX_SEARCH_LENGTH);
}

/** Search known RSI organizations by name or SID; only an explicitly selected result is submitted. */
export function GuildOrgForm(props: GuildOrgFormProps) {
  return <GuildOrgFields key={props.currentSid ?? "unmapped"} {...props} />;
}

function GuildOrgFields({ guildId, guildName, currentSid, currentOrgName, canEdit }: GuildOrgFormProps) {
  const router = useRouter();
  const listId = useId();
  const hintId = useId();
  const current = currentSid ? { sid: currentSid, name: currentOrgName ?? currentSid } : null;
  const suggestion = guildQuery(guildName);
  const [selected, setSelected] = useState<GuildOrgOption | null>(current);
  const [query, setQuery] = useState(current ? orgLabel(current) : suggestion);
  const [options, setOptions] = useState<GuildOrgOption[]>([]);
  const [phase, setPhase] = useState<"idle" | "loading" | "ready" | "error">("idle");
  const [open, setOpen] = useState(!current && canEdit);
  const [active, setActive] = useState(-1);
  const [busy, setBusy] = useState(false);
  const disabled = !canEdit || busy;
  // Looking up a corpo is a read operation; responsibility only restricts saving.
  const showResults = open && !selected && !busy;

  // Ignore earlier searches after typing, choosing an organization, or disabling the form.
  useEffect(() => {
    const text = query.trim();
    setOptions([]);
    setActive(-1);
    if (selected || busy || text.length < MIN_SEARCH_LENGTH) {
      setPhase("idle");
      return;
    }
    let stale = false;
    setPhase("loading");
    const timer = setTimeout(async () => {
      try {
        const found = await searchGuildOrgsAction(text);
        if (!stale) {
          setOptions(found);
          setPhase("ready");
        }
      } catch (error) {
        if (!stale) {
          setPhase("error");
          reportDiscordActionError(error, "Recherche de corpos indisponible. Réessaie.");
        }
      }
    }, 300);
    return () => { stale = true; clearTimeout(timer); };
  }, [query, selected, busy]);

  function choose(org: GuildOrgOption) {
    setSelected(org);
    setQuery(orgLabel(org));
    setOpen(false);
    setActive(-1);
  }

  function onKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (selected || busy) return;
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      setOpen(true);
      if (options.length) {
        setActive(previous => e.key === "ArrowDown"
          ? (previous + 1) % options.length
          : (previous <= 0 ? options.length - 1 : previous - 1));
      }
    } else if (e.key === "Enter") {
      e.preventDefault();
      if (showResults && active >= 0 && options[active]) choose(options[active]);
    } else if (e.key === "Escape") {
      e.preventDefault();
      setOpen(false);
      setActive(-1);
    }
  }

  async function save(orgSid: string | null) {
    if (disabled) return;
    setBusy(true);
    try {
      const res = await mapGuildOrgAction(guildId, orgSid);
      if (res.ok) {
        toast.success(res.data?.orgSid ? `Serveur relié à ${res.data.orgSid}.` : "Serveur délié de sa corpo.");
        router.refresh();
      } else {
        toast.error(res.error ?? "Échec du rattachement.");
      }
    } catch (error) {
      reportDiscordActionError(error, "Impossible d'enregistrer le rattachement. Réessaie.");
    } finally {
      setBusy(false);
    }
  }

  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (!selected || !sidSchema.safeParse(selected.sid).success) {
      toast.error("Sélectionne une corpo dans les propositions.");
      return;
    }
    if (selected.sid !== currentSid) await save(selected.sid);
  }

  function unmap() {
    if (!disabled && currentSid && confirm(`Délier ce serveur de ${currentSid} ?`)) void save(null);
  }

  return (
    <form onSubmit={submit} className="flex w-full min-w-0 flex-col gap-3 sm:max-w-xl">
      <div onBlur={e => {
        if (!e.currentTarget.contains(e.relatedTarget)) { setOpen(false); setActive(-1); }
      }}>
        <HudInput
          label="CORPO RSI — NOM OU SID"
          type="text"
          autoComplete="off"
          maxLength={MAX_SEARCH_LENGTH}
          placeholder="Rechercher une corpo par son nom ou son SID…"
          value={query}
          onChange={e => {
            setQuery(e.target.value);
            setSelected(null);
            setOptions([]);
            setActive(-1);
            setOpen(true);
          }}
          onFocus={() => { if (!selected) setOpen(true); }}
          onKeyDown={onKeyDown}
          disabled={busy}
          role="combobox"
          aria-autocomplete="list"
          aria-expanded={showResults}
          aria-controls={listId}
          aria-describedby={hintId}
          aria-activedescendant={showResults && active >= 0 ? `${listId}-${active}` : undefined}
          className="w-full bg-hud-bg"
        />
        {showResults && (
          <div className="mt-2 border border-hud-cyan-dim bg-hud-bg font-mono text-xs">
            <div role="status" className="px-3 py-2 text-hud-text-dim">
              {phase === "loading" ? "Recherche…"
                : phase === "error" ? "Recherche indisponible. Modifie la recherche pour réessayer."
                : phase === "ready" && options.length === 0 ? "Aucune corpo trouvée. Essaie un autre nom ou SID."
                : phase === "idle" ? "Saisis un nom ou un SID."
                : query === suggestion ? "Propositions à partir du nom du serveur :" : "Sélectionne une corpo :"}
            </div>
            <ul id={listId} role="listbox" aria-label="Propositions de corpos RSI" className="max-h-56 overflow-y-auto">
              {options.map((org, index) => (
                <li key={org.sid} role="presentation">
                  <button
                    id={`${listId}-${index}`}
                    type="button"
                    role="option"
                    aria-selected={index === active}
                    onMouseDown={e => e.preventDefault()}
                    onClick={() => choose(org)}
                    className={`flex w-full min-w-0 items-center justify-between gap-3 px-3 py-2 text-left hover:bg-hud-cyan/10 ${index === active ? "bg-hud-cyan/10" : ""}`}
                  >
                    <DiscordText value={org.name} fallback={org.sid} className="min-w-0 text-hud-text !whitespace-normal [overflow-wrap:anywhere]" />
                    <span className="shrink-0 text-hud-cyan">[{org.sid}]</span>
                  </button>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
      <p id={hintId} className="font-mono text-xs text-hud-text-dim [overflow-wrap:anywhere]">
        {selected ? `Corpo sélectionnée : ${orgLabel(selected)}.${selected.sid !== currentSid && canEdit ? ` Clique sur ${currentSid ? "Changer" : "Relier"} pour enregistrer.` : ""}`
          : "Recherche parmi les corpos connues du tracker. Sélectionne une proposition, puis clique sur Relier."}
      </p>
      <div className="flex flex-wrap gap-2">
        <HudButton type="submit" disabled={disabled || !selected || selected.sid === currentSid}>
          {busy ? "…" : currentSid ? "CHANGER" : "RELIER"}
        </HudButton>
        {currentSid && (
          <HudButton type="button" variant="ghost" disabled={disabled} onClick={unmap}>DÉLIER</HudButton>
        )}
      </div>
    </form>
  );
}
