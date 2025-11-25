import React, { useState } from "react";
import type { ChangeEvent } from "react";
import "./App.css";
import PickCertificate from "./components/PickCertificate";
import useSignature from "./hooks/useSignature";
import { downloadBase64Pdf } from "./utils/downloadBase64";
import { fileToBase64 } from "./utils/fileToBase64";

// Define the state of the application to manage the UI flow
type AppState =
  | "initial"
  | "selectingCert"
  | "signing"
  | "error"
  | "done"
| "signingContinue";

const API_BASE = import.meta.env.VITE_API_BASE ?? "http://localhost:5000";
const DEFAULT_SIGN_RECT = { x: 50, y: 50, width: 200, height: 50 };
const DEFAULT_SIGN_PAGE = 1;

function App() {
  const [apiToken, setApiToken] = useState<string>("");
  const [pdfFile, setPdfFile] = useState<File | null>(null);
  const [reason, setReason] = useState<string>("Contract Agreement");
  const [location, setLocation] = useState<string>("Remote Location");
  const [appState, setAppState] = useState<AppState>("initial");
  const [error, setError] = useState<string>("");

  const [pfxPdfFile, setPfxPdfFile] = useState<File | null>(null);
  const [pfxFile, setPfxFile] = useState<File | null>(null);
  const [pfxPassword, setPfxPassword] = useState<string>("");
  const [pfxReason, setPfxReason] = useState<string>("PFX approval");
  const [pfxLocation, setPfxLocation] = useState<string>("Headquarters");
  const [pfxFieldName, setPfxFieldName] = useState<string>("");
  const [pfxStatus, setPfxStatus] = useState<string>("");

  const [timestampPdfFile, setTimestampPdfFile] = useState<File | null>(null);
  const [timestampImageFile, setTimestampImageFile] = useState<File | null>(
    null
  );
  const [timestampReason, setTimestampReason] = useState<string>(
    "Document time-stamp"
  );
  const [timestampLocation, setTimestampLocation] =
    useState<string>("Server room");
  const [timestampFieldName, setTimestampFieldName] = useState<string>("");
  const [timestampStatus, setTimestampStatus] = useState<string>("");

  const onFileSelect =
    (setter: (file: File | null) => void) =>
    (event: ChangeEvent<HTMLInputElement>) => {
      const file = event.target.files && event.target.files[0];
      setter(file ?? null);
    };

  const handleFileChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    if (event.target.files && event.target.files.length > 0) {
      setPdfFile(event.target.files[0]);
    }
  };

  const buildAuthHeaders = () => {
    const headers: Record<string, string> = { "Content-Type": "application/json" };
    if (apiToken.trim()) {
      headers.Authorization = `Bearer ${apiToken.trim()}`;
    }
    return headers;
  };

  const ensureToken = () => {
    if (!apiToken.trim()) {
      setError("Please paste an API token first.");
      setAppState("error");
      return false;
    }
    return true;
  };

  const startSigningProcess = () => {
    if (!pdfFile) {
      setError("Please upload a PDF file first.");
      return;
    }
    setError("");
    setAppState("selectingCert");
  };

  const handleCertificateSelected = async (
    publicKey: string,
    signHash: (x: string) => Promise<string>
  ) => {
    if (!pdfFile) return;
    if (!ensureToken()) return;

    setAppState("signing");
    try {
      const pdfContent = await fileToBase64(pdfFile);

      const presignResponse = await fetch(`${API_BASE}/api/presign`, {
        method: "POST",
        headers: buildAuthHeaders(),
        body: JSON.stringify({
          certificatePem: publicKey,
          pdfContent,
          location,
          reason,
          signRect: DEFAULT_SIGN_RECT,
          signPageNumber: DEFAULT_SIGN_PAGE,
        }),
      });

      if (!presignResponse.ok) {
        throw new Error(
          `Server returned an error during presign: ${await presignResponse.text()}`
        );
      }

      const { id, hashToSign } = await presignResponse.json();
      setAppState("signingContinue");
      // This will now use the LATEST version of signHash from the hook
      const signedHash = await signHash(hashToSign);
      if (!signedHash) {
        throw new Error("Failed to sign the hash on the client-side.");
      }

      const signResponse = await fetch(`${API_BASE}/api/sign`, {
        method: "POST",
        headers: buildAuthHeaders(),
        body: JSON.stringify({ id, signedHash }),
      });

      if (!signResponse.ok) {
        throw new Error(
          `Server returned an error during final signing: ${await signResponse.text()}`
        );
      }

      const { result: signedPdfBase64 } = await signResponse.json();

      downloadBase64Pdf(signedPdfBase64, `signed_${pdfFile.name}`);
      setAppState("done");
      // eslint-disable-next-line
    } catch (err: any) {
      console.error(err);
      setError(err.message);
      setAppState("error");
    }
  };

  const { CertificateSelector } = useSignature(handleCertificateSelected);

  const resetState = () => {
    setPdfFile(null);
    setError("");
    setAppState("initial");
  };

  const handlePfxSigning = async () => {
    if (!pfxPdfFile || !pfxFile) {
      setPfxStatus("Please pick both a PDF and a .pfx/.p12 file.");
      return;
    }
    if (!ensureToken()) {
      setPfxStatus("Please paste an API token first.");
      return;
    }

    try {
      setPfxStatus("Signing with local certificate...");
      const [pdfContent, pfxContent] = await Promise.all([
        fileToBase64(pfxPdfFile),
        fileToBase64(pfxFile),
      ]);

      const response = await fetch(`${API_BASE}/api/sign-pfx`, {
        method: "POST",
        headers: buildAuthHeaders(),
        body: JSON.stringify({
          pdfContent,
          pfxContent,
          pfxPassword,
          reason: pfxReason,
          location: pfxLocation,
          fieldName: pfxFieldName || undefined,
          signRect: DEFAULT_SIGN_RECT,
          signPageNumber: DEFAULT_SIGN_PAGE,
        }),
      });

      if (!response.ok) {
        throw new Error(await response.text());
      }

      const { result } = await response.json();
      downloadBase64Pdf(result, `pfx_signed_${pfxPdfFile.name}`);
      setPfxStatus("✅ Signed PDF downloaded.");
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setPfxStatus(`Error: ${message}`);
    }
  };

  const handleTimestamp = async () => {
    if (!timestampPdfFile) {
      setTimestampStatus("Please pick a PDF to timestamp.");
      return;
    }
    if (!ensureToken()) {
      setTimestampStatus("Please paste an API token first.");
      return;
    }

    try {
      setTimestampStatus("Submitting document to TSA...");
      const pdfContent = await fileToBase64(timestampPdfFile);
      const signImageContent = timestampImageFile
        ? await fileToBase64(timestampImageFile)
        : undefined;

      const response = await fetch(`${API_BASE}/api/timestamp`, {
        method: "POST",
        headers: buildAuthHeaders(),
        body: JSON.stringify({
          pdfContent,
          reason: timestampReason,
          location: timestampLocation,
          fieldName: timestampFieldName || undefined,
          signImageContent,
          signRect: DEFAULT_SIGN_RECT,
          signPageNumber: DEFAULT_SIGN_PAGE,
        }),
      });

      if (!response.ok) {
        throw new Error(await response.text());
      }

      const { result } = await response.json();
      downloadBase64Pdf(result, `timestamped_${timestampPdfFile.name}`);
      setTimestampStatus("✅ Timestamped PDF downloaded.");
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setTimestampStatus(`Error: ${message}`);
    }
  };

  return (
    <div className="App">
      <header className="App-header">
        <h1>PDF Remote Signing Client</h1>
      </header>
      <main>
        {appState === "initial" && (
          <div className="form-container">
            <div className="form-field">
              <label htmlFor="api-token">API token</label>
              <input
                type="text"
                id="api-token"
                value={apiToken}
                onChange={(e) => setApiToken(e.target.value)}
                placeholder="Paste Bearer token"
              />
            </div>
            <div className="form-field">
              <label htmlFor="pdf-upload">1. Upload PDF Document</label>
              <input
                type="file"
                id="pdf-upload"
                accept=".pdf"
                onChange={handleFileChange}
              />
            </div>
            <div className="form-field">
              <label htmlFor="reason">2. Reason for Signing</label>
              <input
                type="text"
                id="reason"
                value={reason}
                onChange={(e) => setReason(e.target.value)}
              />
            </div>
            <div className="form-field">
              <label htmlFor="location">3. Location</label>
              <input
                type="text"
                id="location"
                value={location}
                onChange={(e) => setLocation(e.target.value)}
              />
            </div>
            <button onClick={startSigningProcess} disabled={!pdfFile}>
              Select Certificate & Sign
            </button>
          </div>
        )}

        {appState === "selectingCert" && (
          <PickCertificate onCancel={() => setAppState("initial")}>
            {CertificateSelector}
          </PickCertificate>
        )}

        {appState === "signing" && (
          <div className="status-container">
            <h3>Signing in progress...</h3>
            <p>
              Please wait. Communicating with the server and signing the
              document.
            </p>
          </div>
        )}

        {(appState === "done" || appState === "error") && (
          <div className="status-container">
            {appState === "done" && <h3>✅ Success!</h3>}
            {appState === "done" && (
              <p>Your document has been signed and downloaded.</p>
            )}
            {appState === "error" && <h3>❌ Error</h3>}
            {appState === "error" && <p className="error-message">{error}</p>}
            <button onClick={resetState}>Sign Another Document</button>
          </div>
        )}

        <section className="demo-section">
          <h2>Sign with local .pfx/.p12</h2>
          <p className="section-description">
            Upload a PDF and a password-protected PKCS#12 file to have the
            server complete the signature in a single step.
          </p>
          <div className="form-field">
            <label htmlFor="pfx-pdf">PDF document</label>
            <input
              id="pfx-pdf"
              type="file"
              accept=".pdf"
              onChange={onFileSelect(setPfxPdfFile)}
            />
          </div>
          <div className="form-field">
            <label htmlFor="pfx-file">.pfx / .p12 file</label>
            <input
              id="pfx-file"
              type="file"
              accept=".pfx,.p12"
              onChange={onFileSelect(setPfxFile)}
            />
          </div>
          <div className="form-field">
            <label htmlFor="pfx-password">PFX password</label>
            <input
              id="pfx-password"
              type="password"
              value={pfxPassword}
              onChange={(e) => setPfxPassword(e.target.value)}
            />
          </div>
          <div className="form-field">
            <label htmlFor="pfx-reason">Reason</label>
            <input
              id="pfx-reason"
              type="text"
              value={pfxReason}
              onChange={(e) => setPfxReason(e.target.value)}
            />
          </div>
          <div className="form-field">
            <label htmlFor="pfx-location">Location</label>
            <input
              id="pfx-location"
              type="text"
              value={pfxLocation}
              onChange={(e) => setPfxLocation(e.target.value)}
            />
          </div>
          <div className="form-field">
            <label htmlFor="pfx-field">Field name (optional)</label>
            <input
              id="pfx-field"
              type="text"
              value={pfxFieldName}
              placeholder="Signature_Pfx"
              onChange={(e) => setPfxFieldName(e.target.value)}
            />
          </div>
          <button
            onClick={handlePfxSigning}
            disabled={!pfxPdfFile || !pfxFile || !pfxPassword}
          >
            Sign PDF with PFX
          </button>
          {pfxStatus && (
            <p
              className={`status-text ${
                pfxStatus.startsWith("Error") ? "error" : "success"
              }`}
            >
              {pfxStatus}
            </p>
          )}
        </section>

        <section className="demo-section">
          <h2>Apply TSA timestamp only</h2>
          <p className="section-description">
            Demonstrates the `/timestamp` endpoint that adds a visible stamp and
            RFC3161 DocTimeStamp without a signing certificate.
          </p>
          <div className="form-field">
            <label htmlFor="timestamp-pdf">PDF document</label>
            <input
              id="timestamp-pdf"
              type="file"
              accept=".pdf"
              onChange={onFileSelect(setTimestampPdfFile)}
            />
          </div>
          <div className="form-field">
            <label htmlFor="timestamp-image">
              Optional image (logo/signature)
            </label>
            <input
              id="timestamp-image"
              type="file"
              accept="image/*"
              onChange={onFileSelect(setTimestampImageFile)}
            />
          </div>
          <div className="form-field">
            <label htmlFor="timestamp-reason">Reason</label>
            <input
              id="timestamp-reason"
              type="text"
              value={timestampReason}
              onChange={(e) => setTimestampReason(e.target.value)}
            />
          </div>
          <div className="form-field">
            <label htmlFor="timestamp-location">Location</label>
            <input
              id="timestamp-location"
              type="text"
              value={timestampLocation}
              onChange={(e) => setTimestampLocation(e.target.value)}
            />
          </div>
          <div className="form-field">
            <label htmlFor="timestamp-field">Field name (optional)</label>
            <input
              id="timestamp-field"
              type="text"
              value={timestampFieldName}
              placeholder="Timestamp_1"
              onChange={(e) => setTimestampFieldName(e.target.value)}
            />
          </div>
          <button onClick={handleTimestamp} disabled={!timestampPdfFile}>
            Apply timestamp
          </button>
          {timestampStatus && (
            <p
              className={`status-text ${
                timestampStatus.startsWith("Error") ? "error" : "success"
              }`}
            >
              {timestampStatus}
            </p>
          )}
        </section>
      </main>
    </div>
  );
}

export default App;
