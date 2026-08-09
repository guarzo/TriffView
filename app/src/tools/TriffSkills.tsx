import React, { useEffect, useState } from "react";
import { onNativeMessage, postNative } from "../nativeBridge.js";

type TriffSkillsCharacter = {
  characterId: number;
  characterName: string;
  scopes: string[];
  authenticatedUtc: string;
  fetchedUtc: string | null;
  error: string;
  needsReauth: boolean;
};

type TriffSkillsState = {
  authInProgress: boolean;
  refreshInFlight: boolean;
  selectedCharacterId: number;
  characters: TriffSkillsCharacter[];
};

export default function TriffSkills() {
  const [state, setState] = useState<TriffSkillsState | null>(null);

  useEffect(() => {
    const unsubscribe = onNativeMessage((message: any) => {
      if (message?.type === "triffskills:state") {
        setState(message as TriffSkillsState);
      }
    });
    postNative({ type: "triffskills:get-state" });
    return unsubscribe;
  }, []);

  return (
    <div className="triffskills">
      <h2>Skill Planner</h2>
      <p>{state ? `${state.characters.length} character(s) authenticated.` : "Loading..."}</p>
    </div>
  );
}
