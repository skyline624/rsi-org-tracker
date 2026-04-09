"use client";
import { create } from "zustand";
import { persist } from "zustand/middleware";

/**
 * Favoris côté client (v1) stockés en localStorage. Migration vers backend
 * en v2 quand un endpoint /api/me/favorites sera ajouté.
 */

interface FavoritesState {
  orgs: string[]; // array of SIDs
  users: string[]; // array of handles
  toggleOrg: (sid: string) => void;
  toggleUser: (handle: string) => void;
  isOrgFav: (sid: string) => boolean;
  isUserFav: (handle: string) => boolean;
  clear: () => void;
}

export const useFavorites = create<FavoritesState>()(
  persist(
    (set, get) => ({
      orgs: [],
      users: [],
      toggleOrg: (sid) =>
        set((s) => ({
          orgs: s.orgs.includes(sid)
            ? s.orgs.filter((x) => x !== sid)
            : [...s.orgs, sid],
        })),
      toggleUser: (handle) =>
        set((s) => ({
          users: s.users.includes(handle)
            ? s.users.filter((x) => x !== handle)
            : [...s.users, handle],
        })),
      isOrgFav: (sid) => get().orgs.includes(sid),
      isUserFav: (handle) => get().users.includes(handle),
      clear: () => set({ orgs: [], users: [] }),
    }),
    { name: "sct_favorites" },
  ),
);
