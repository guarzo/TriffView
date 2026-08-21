import React from "react";

type Plan = { name: string; requirementCount: number };

export default function PlanRail({
  plans,
  readyCounts,
  characterCount,
  selectedPlanName,
  onSelect,
}: {
  plans: Plan[];
  readyCounts: Record<string, number>;
  characterCount: number;
  selectedPlanName: string;
  onSelect: (planName: string) => void;
}) {
  if (!plans.length) {
    return <p className="triffskills-rail-status">No local plans yet. Import one, then reload plans.</p>;
  }

  return (
    <nav className="triffskills-plan-list" aria-label="Skill plans" data-hud-scroll>
      {plans.map((plan) => {
        const selected = plan.name === selectedPlanName;
        return (
          <button
            type="button"
            key={plan.name}
            className={`triffskills-plan-row${selected ? " is-selected" : ""}`}
            aria-current={selected ? "true" : undefined}
            title={`${plan.name} — ${plan.requirementCount} requirement${plan.requirementCount === 1 ? "" : "s"}`}
            onClick={() => onSelect(plan.name)}
          >
            <span className="triffskills-plan-name">{plan.name}</span>
            <span className="triffskills-plan-ratio">
              {readyCounts[plan.name] ?? 0}/{characterCount}
            </span>
          </button>
        );
      })}
    </nav>
  );
}
