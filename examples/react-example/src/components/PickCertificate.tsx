import * as React from "react";

const PickCertificate = ({
  children,
  onCancel,
}: {
  children: React.ReactNode;
  onCancel: () => void;
}): React.ReactElement => {
  return (
    <div className="certificate-picker-container">
      <h3>Select a Signing Certificate</h3>
      <p>
        Please select a certificate from a hardware token or your operating
        system store.
      </p>
      {children}
      <div style={{ marginTop: 20 }}>
        <button type="button" onClick={onCancel}>
          Cancel
        </button>
      </div>
    </div>
  );
};

export default PickCertificate;
