import React from "react";

function Field({ label, children }) {
  return (
    <label className="triffview-field">
      <span>{label}</span>
      {children}
    </label>
  );
}

export default Field;
