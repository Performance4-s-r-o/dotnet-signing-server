import React, { useState } from "react";
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

function App() {
  const [pdfFile, setPdfFile] = useState<File | null>(null);
  const [reason, setReason] = useState<string>("Contract Agreement");
  const [location, setLocation] = useState<string>("Remote Location");
  const [appState, setAppState] = useState<AppState>("initial");
  const [error, setError] = useState<string>("");

  const handleFileChange = (event: React.ChangeEvent<HTMLInputElement>) => {
    if (event.target.files && event.target.files.length > 0) {
      setPdfFile(event.target.files[0]);
    }
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

    setAppState("signing");
    try {
      const pdfContent = await fileToBase64(pdfFile);

      const presignResponse = await fetch("http://localhost:8080/presign", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          certificatePem: publicKey,
          pdfContent,
          location,
          reason,
          signRect: { x: 50, y: 50, width: 200, height: 50 },
          signPageNumber: 1,
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

      const signResponse = await fetch("http://localhost:8080/sign", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
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

  return (
    <div className="App">
      <header className="App-header">
        <h1>PDF Remote Signing Client</h1>
      </header>
      <main>
        {appState === "initial" && (
          <div className="form-container">
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
      </main>
    </div>
  );
}

export default App;
