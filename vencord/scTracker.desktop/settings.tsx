import { GuildStore, React, SortedGuildStore, useEffect, useState, useStateFromStores } from "@webpack/common";

import { settings } from "./configuration";
import { getRunnerState, isTracked, loadApiKey, runner, saveConfiguration, stopSending, subscribeRunner, trackGuild, trackedGuildIds } from "./controller";
import { MAX_AUTO_SYNC_MINUTES, MIN_AUTO_SYNC_MINUTES, validAutoSyncMinutes } from "./lib/autoSync";
import type { SyncStatus } from "./lib/syncRunner";
import { validatePostArgs } from "./lib/postArgs";

function Status({ status }: { status?: SyncStatus }) {
  if (!status) return <small>Aucun envoi pendant cette session.</small>;
  return <small>
    {new Date(status.at).toLocaleString()} — {status.state === "not_sent" ? "Non envoyé" : status.state === "failed" ? "Erreur" : status.complete ? "Complet" : "Partiel"}
    {status.method ? ` · ${status.method}` : ""}{status.count !== undefined ? ` · ${status.count} membres` : ""}
    {status.state === "sent" ? ` · ${status.orgSid ? `Corpo : ${status.orgSid}` : "Corpo non reliée"}` : ""}
    <br />{status.message}
  </small>;
}

export function Controls() {
  const stored = settings.use(["trackedGuildIds", "trackerUrl", "fingerprint", "autoSyncEnabled", "autoSyncIntervalMinutes"]);
  const [state, setState] = useState(getRunnerState);
  const [url, setUrl] = useState(stored.trackerUrl);
  const [fingerprint, setFingerprint] = useState(stored.fingerprint);
  const [apiKey, setApiKey] = useState("");
  const [loaded, setLoaded] = useState(false);
  const [saving, setSaving] = useState(false);
  const [feedback, setFeedback] = useState("");
  const [interval, setInterval] = useState(String(stored.autoSyncIntervalMinutes));
  const ids = useStateFromStores([SortedGuildStore, GuildStore], () => SortedGuildStore.getFlattenedGuildIds());
  useEffect(() => subscribeRunner(() => setState(getRunnerState())), []);
  useEffect(() => setInterval(String(stored.autoSyncIntervalMinutes)), [stored.autoSyncIntervalMinutes]);
  useEffect(() => {
    let mounted = true;
    loadApiKey().then(key => { if (mounted) { setApiKey(key); setLoaded(true); } }, () => { if (mounted) setFeedback("Impossible de lire la clé locale."); });
    return () => { mounted = false; };
  }, []);
  const disabled = state.busy || saving;
  async function save() {
    const valid = validatePostArgs({ url, fingerprint, apiKey, guildId: "100000000000000001", body: "{}" });
    if (!valid.ok) { setFeedback(valid.error); return; }
    setSaving(true);
    try {
      await saveConfiguration(valid.value);
      setUrl(valid.value.url); setFingerprint(valid.value.fingerprint);
      setFeedback("Configuration enregistrée. La clé reste dans le stockage local de ce client.");
    } catch { setFeedback("Impossible d'enregistrer la clé locale. Réessaie."); }
    finally { setSaving(false); }
  }
  async function clearKey() {
    setSaving(true);
    try { await saveConfiguration(null); setApiKey(""); setFeedback("Clé locale effacée."); }
    catch { setFeedback("Impossible d'effacer la clé locale."); }
    finally { setSaving(false); }
  }
  function saveInterval() {
    const minutes = interval.trim() === "" ? NaN : Number(interval);
    if (!validAutoSyncMinutes(minutes)) {
      setFeedback(`Choisis un intervalle entier entre ${MIN_AUTO_SYNC_MINUTES} et ${MAX_AUTO_SYNC_MINUTES} minutes.`);
      return;
    }
    settings.store.autoSyncIntervalMinutes = minutes;
    setFeedback("Intervalle enregistré.");
  }
  return <div className="sc-tracker-controls">
    <p>Envoie un serveur à la demande, ou coche ceux à inclure dans les lots et le minuteur. Le tracker conserve les données et crée l'historique. Copie la configuration depuis Paramètres → Clé d'envoi Discord sur le site.</p>
    <label>URL du tracker<input type="url" value={url} placeholder="https://192.0.2.1" disabled={disabled} onChange={e => setUrl(e.currentTarget.value)} /></label>
    <label>Empreinte SHA-256 du certificat<input value={fingerprint} spellCheck={false} disabled={disabled} onChange={e => setFingerprint(e.currentTarget.value)} /></label>
    <label>Clé d'envoi Discord<input type="password" value={apiKey} autoComplete="off" spellCheck={false} disabled={disabled || !loaded} onChange={e => setApiKey(e.currentTarget.value)} /></label>
    <button disabled={disabled || !loaded} onClick={() => void save()}>{saving ? "Enregistrement…" : "Enregistrer la configuration"}</button>
    <button disabled={disabled || !loaded} onClick={() => void clearKey()}>Effacer la clé locale</button>
    <fieldset className="sc-tracker-timer">
      <legend>Envoi automatique</legend>
      <label><input type="checkbox" checked={stored.autoSyncEnabled === true} disabled={disabled}
        onChange={e => { settings.store.autoSyncEnabled = e.currentTarget.checked; }} /> Envoyer automatiquement les serveurs cochés</label>
      <label>Intervalle entre les envois (minutes)<input type="number" min={MIN_AUTO_SYNC_MINUTES} max={MAX_AUTO_SYNC_MINUTES} step="1"
        value={interval} disabled={disabled} onChange={e => setInterval(e.currentTarget.value)} /></label>
      <button disabled={disabled || interval === String(stored.autoSyncIntervalMinutes)} onClick={saveInterval}>Appliquer l'intervalle</button>
      <p role="status">{state.automatic.enabled
        ? state.automatic.running ? "Envoi automatique en cours."
          : state.automatic.nextRunAt ? `Prochain envoi : ${new Date(state.automatic.nextRunAt).toLocaleString()}.` : "Minuteur en attente."
        : "Envoi automatique désactivé."}</p>
      <small>Le minuteur fonctionne lorsque Discord est ouvert. Un envoi déjà en cours reporte l'échéance suivante. Aucun envoi manqué n'est rattrapé.</small>
    </fieldset>
    {feedback && <p role="status">{feedback}</p>}
    <div className="sc-tracker-actions">
      <button disabled={disabled || trackedGuildIds().length === 0} onClick={() => void runner.run(trackedGuildIds())}>Envoyer les serveurs cochés</button>
      <button disabled={!state.busy && !state.automatic.enabled} onClick={stopSending}>Arrêter</button>
    </div>
    {state.busy && <p role="status">{state.progress}{state.phase === "uploading" && " L'envoi en cours peut déjà être enregistré."}</p>}
    <ul>{ids.map(id => {
      const guild = GuildStore.getGuild(id);
      if (!guild) return null;
      return <li key={id}>
        <div className="sc-tracker-guild">
          <label><input type="checkbox" checked={isTracked(id)} disabled={disabled} onChange={e => trackGuild(id, e.currentTarget.checked)} /> {guild.name}</label>
          <button disabled={disabled} onClick={() => void runner.run([id], { requireTracked: false })}>Envoyer</button>
        </div>
        <Status status={state.lastResults[id]} />
      </li>;
    })}</ul>
    <p>Arrêter pendant la collecte empêche son envoi. Pendant un envoi, les serveurs suivants sont ignorés. Les résultats partiels permettent des mises à jour sans déclarer de départs.</p>
  </div>;
}
