"use client";
import { useState, type FormEvent } from "react";
import { toast } from "sonner";
import { HudPanel } from "@/components/hud/HudPanel";
import { HudButton } from "@/components/hud/HudButton";
import { formatRelative } from "@/lib/utils/format";
import {
  createOrgNoteAction,
  updateOrgNoteAction,
  deleteOrgNoteAction,
  type OrgNoteDto,
} from "./org-note-actions";

interface Props {
  sid: string;
  initialNotes: OrgNoteDto[];
  currentUserId?: number;
  isAdmin?: boolean;
}

const textareaCls =
  "hud-clip border border-hud-cyan-dim bg-hud-bg/60 px-3 py-2 font-mono text-sm text-hud-text placeholder:text-hud-text-dim/60 focus:border-hud-cyan focus:outline-none";

export function OrgNotesSection({ sid, initialNotes, currentUserId, isAdmin }: Props) {
  const [notes, setNotes] = useState<OrgNoteDto[]>(initialNotes);
  const [draft, setDraft] = useState("");
  const [busy, setBusy] = useState(false);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editDraft, setEditDraft] = useState("");

  const canModify = (n: OrgNoteDto) => Boolean(isAdmin) || n.authorApiUserId === currentUserId;

  async function add() {
    if (!draft.trim()) return;
    setBusy(true);
    const res = await createOrgNoteAction(sid, draft);
    setBusy(false);
    if (res.ok && res.note) {
      setNotes([res.note, ...notes]);
      setDraft("");
      toast.success("Note ajoutée.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function saveEdit(id: number) {
    setBusy(true);
    const res = await updateOrgNoteAction(id, editDraft);
    setBusy(false);
    if (res.ok && res.note) {
      const updated = res.note;
      setNotes((prev) => prev.map((n) => (n.id === id ? updated : n)));
      setEditingId(null);
      toast.success("Note modifiée.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  async function remove(id: number) {
    setBusy(true);
    const res = await deleteOrgNoteAction(id);
    setBusy(false);
    if (res.ok) {
      setNotes((prev) => prev.filter((n) => n.id !== id));
      toast.success("Note supprimée.");
    } else {
      toast.error(res.error ?? "Échec.");
    }
  }

  return (
    <HudPanel label={`NOTES (${notes.length})`}>
      <div className="flex flex-col gap-4">
        <div className="flex flex-col gap-2">
          <textarea
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            placeholder="Ajouter une note sur cette organisation…"
            rows={3}
            maxLength={10000}
            className={textareaCls}
          />
          <div className="flex justify-end">
            <HudButton type="button" onClick={add} disabled={busy || !draft.trim()}>
              {busy ? "…" : "AJOUTER LA NOTE"}
            </HudButton>
          </div>
        </div>

        {notes.length === 0 ? (
          <p className="font-mono text-xs text-hud-text-dim">Aucune note.</p>
        ) : (
          <ul className="flex flex-col gap-3">
            {notes.map((n) => (
              <li key={n.id} className="border border-hud-cyan/15 bg-hud-bg/40 p-3">
                {editingId === n.id ? (
                  <div className="flex flex-col gap-2">
                    <textarea
                      value={editDraft}
                      onChange={(e) => setEditDraft(e.target.value)}
                      rows={3}
                      maxLength={10000}
                      className={textareaCls}
                    />
                    <div className="flex gap-2">
                      <HudButton type="button" onClick={() => saveEdit(n.id)} disabled={busy}>
                        OK
                      </HudButton>
                      <HudButton type="button" variant="ghost" onClick={() => setEditingId(null)}>
                        ANNULER
                      </HudButton>
                    </div>
                  </div>
                ) : (
                  <>
                    <p className="whitespace-pre-wrap font-mono text-sm text-hud-text">{n.body}</p>
                    <div className="mt-2 flex items-center justify-between font-mono text-[10px] uppercase tracking-wide text-hud-text-dim">
                      <span>
                        {n.authorUsername} · {formatRelative(n.updatedAt)}
                      </span>
                      {canModify(n) && (
                        <span className="flex gap-3">
                          <button
                            type="button"
                            className="hover:text-hud-cyan"
                            onClick={() => {
                              setEditingId(n.id);
                              setEditDraft(n.body);
                            }}
                          >
                            ÉDITER
                          </button>
                          <button type="button" className="hover:text-hud-red" onClick={() => remove(n.id)}>
                            SUPPR
                          </button>
                        </span>
                      )}
                    </div>
                  </>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>
    </HudPanel>
  );
}
