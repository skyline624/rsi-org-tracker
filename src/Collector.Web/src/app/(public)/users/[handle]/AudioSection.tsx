"use client";
import { useRef, useState, type FormEvent } from "react";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { formatRelative } from "@/lib/utils/format";
import { uploadAudioAction, deleteAudioAction, type AudioDto } from "./audio-actions";

function fmtSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} o`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} Ko`;
  return `${(bytes / 1024 / 1024).toFixed(1)} Mo`;
}

interface Props {
  handle: string;
  initialAudio: AudioDto[];
  currentUserId?: number;
  isAdmin?: boolean;
}

export function AudioSection({ handle, initialAudio, currentUserId, isAdmin }: Props) {
  const [items, setItems] = useState<AudioDto[]>(initialAudio);
  const [busy, setBusy] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);

  const canModify = (a: AudioDto) => Boolean(isAdmin) || a.authorApiUserId === currentUserId;

  async function upload(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const input = fileRef.current;
    const file = input?.files?.[0];
    if (!input || !file) {
      toast.error("Choisis un fichier audio.");
      return;
    }
    const fd = new FormData();
    fd.append("file", file);
    setBusy(true);
    const res = await uploadAudioAction(handle, fd);
    setBusy(false);
    if (res.ok && res.audio) {
      const created = res.audio;
      setItems((prev) => [created, ...prev]);
      input.value = "";
      toast.success("Audio ajouté.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function remove(id: number) {
    setBusy(true);
    const res = await deleteAudioAction(id);
    setBusy(false);
    if (res.ok) {
      setItems((prev) => prev.filter((a) => a.id !== id));
      toast.success("Audio supprimé.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <HudPanel label={`ENREGISTREMENTS AUDIO (${items.length})`}>
      <div className="flex flex-col gap-4">
        <form onSubmit={upload} className="flex flex-wrap items-center gap-3">
          <input
            ref={fileRef}
            type="file"
            name="file"
            accept="audio/mpeg,audio/ogg,audio/mp4,audio/webm,.mp3,.ogg,.m4a,.webm"
            className="font-mono text-xs text-hud-text-dim file:mr-3 file:border file:border-hud-cyan-dim file:bg-hud-bg/60 file:px-3 file:py-1.5 file:font-mono file:text-hud-cyan"
          />
          <HudButton type="submit" disabled={busy}>
            {busy ? "…" : "TÉLÉVERSER"}
          </HudButton>
          <span className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
            mp3 / ogg / m4a / webm · max 25 Mo
          </span>
        </form>

        {items.length === 0 ? (
          <p className="font-mono text-xs text-hud-text-dim">Aucun enregistrement.</p>
        ) : (
          <ul className="flex flex-col gap-3">
            {items.map((a) => (
              <li key={a.id} className="border border-hud-cyan/15 bg-hud-bg/40 p-3">
                <div className="flex items-center justify-between gap-3">
                  <span className="truncate font-mono text-xs text-hud-text">{a.originalName}</span>
                  <span className="shrink-0 font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
                    {fmtSize(a.sizeBytes)} · {a.authorUsername} · {formatRelative(a.createdAt)}
                  </span>
                </div>
                <audio controls preload="none" src={`/api/audio/${a.id}`} className="mt-2 w-full" />
                {canModify(a) && (
                  <div className="mt-1 flex justify-end">
                    <button
                      type="button"
                      className="font-mono text-[10px] uppercase tracking-wide text-hud-text-dim hover:text-hud-red"
                      onClick={() => remove(a.id)}
                    >
                      SUPPRIMER
                    </button>
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>
    </HudPanel>
  );
}
