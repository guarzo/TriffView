export type Readiness = "Ready" | "Training" | "Locked" | "Missing" | "Unknown" | "Unscored";

export type RosterCharacter = { characterId: number; characterName: string };
export type RosterGroupDefinition = { name: string; characterIds: number[] };

export type RosterInput = {
  characters: RosterCharacter[];
  readinessOf: (characterId: number) => Readiness;
  missingOf: (characterId: number) => number;
  pinnedIds: number[];
  filter: string;
  activeGroup: string | null;
  groups: RosterGroupDefinition[];
};

export type RosterGroup = {
  key: Readiness | "Pinned" | "Ungrouped";
  label: string;
  characterIds: number[];
};

const READINESS_ORDER: Readiness[] = ["Ready", "Training", "Locked", "Missing", "Unknown", "Unscored"];

const LABELS: Record<Readiness | "Pinned" | "Ungrouped", string> = {
  Pinned: "Pinned",
  Ready: "Ready",
  Training: "Training",
  Locked: "Locked",
  Missing: "Missing",
  Unknown: "Unknown",
  Unscored: "Unscored",
  Ungrouped: "Ungrouped",
};

/**
 * Groups every character exactly once. Driven by the character list rather than
 * by enumerating readiness values, so a character whose readiness is Unscored —
 * every newly added character, until its first refresh — still gets a row and
 * stays reachable for forget and re-authenticate.
 *
 * `readinessOf` comes from an unvalidated native message, so a readiness value
 * outside READINESS_ORDER (one native adds later, before this list is updated
 * to match) must still surface the character rather than silently drop its
 * only row — that is what the trailing "Ungrouped" bucket below guarantees.
 */
export function buildRoster(input: RosterInput): RosterGroup[] {
  const needle = input.filter.trim().toLowerCase();
  const pinned = new Set(input.pinnedIds);
  const groupMembers = input.activeGroup
    ? new Set(input.groups.find((group) => group.name === input.activeGroup)?.characterIds ?? [])
    : null;

  const visible = input.characters.filter((character) => {
    if (needle && !character.characterName.toLowerCase().includes(needle)) return false;
    if (groupMembers && !groupMembers.has(character.characterId)) return false;
    return true;
  });

  const byName = (left: RosterCharacter, right: RosterCharacter) =>
    left.characterName.localeCompare(right.characterName);

  const groups: RosterGroup[] = [];

  const pinnedMembers = visible.filter((character) => pinned.has(character.characterId)).sort(byName);
  if (pinnedMembers.length) {
    groups.push({ key: "Pinned", label: LABELS.Pinned, characterIds: pinnedMembers.map((c) => c.characterId) });
  }

  const grouped = new Set<number>(pinnedMembers.map((c) => c.characterId));

  for (const readiness of READINESS_ORDER) {
    const members = visible.filter(
      (character) => !pinned.has(character.characterId) && input.readinessOf(character.characterId) === readiness,
    );
    if (!members.length) continue;
    for (const member of members) grouped.add(member.characterId);

    // Closest-to-ready first, so the long Missing group leads with whoever is
    // nearly there. Distance is a count of unmet requirements, not training
    // time — the evaluator has no skill ranks to compute time from.
    const ordered =
      readiness === "Missing"
        ? [...members].sort(
            (left, right) =>
              input.missingOf(left.characterId) - input.missingOf(right.characterId) || byName(left, right),
          )
        : [...members].sort(byName);

    groups.push({ key: readiness, label: LABELS[readiness], characterIds: ordered.map((c) => c.characterId) });
  }

  // A readiness value outside READINESS_ORDER must still produce a row, or the
  // character loses its only surface for forget and re-authenticate.
  const ungrouped = visible.filter((character) => !grouped.has(character.characterId)).sort(byName);
  if (ungrouped.length) {
    groups.push({ key: "Ungrouped", label: LABELS.Ungrouped, characterIds: ungrouped.map((c) => c.characterId) });
  }

  return groups;
}
