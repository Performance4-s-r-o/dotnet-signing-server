import type {
  ISelectionSuccessEvent,
  PeculiarFortifyCertificatesCustomEvent,
} from "@peculiar/fortify-webcomponents";
import "@peculiar/fortify-webcomponents/dist/peculiar/peculiar.css";
import type { SocketCrypto } from "@webcrypto-local/client";

import { PeculiarFortifyCertificates } from "@peculiar/fortify-webcomponents-react";
import { Convert } from "pvtsutils";
import { createRef, useState } from "react";
import { cryptoLogin } from "../utils/fortify";

const useSignature = (
  onCertificateSelection: (
    publicKey: string,
    signHash: (x: string) => Promise<string>
  ) => void
) => {
  const [publicKey, setPublicKey] = useState<string>();
  const privateKeyRef = createRef<CryptoKey>();
  const providerRef = createRef<SocketCrypto>();

  const getPublicKey = async (
    event: PeculiarFortifyCertificatesCustomEvent<ISelectionSuccessEvent>
  ) => {
    const provider = await event.detail.socketProvider.getCrypto(
      event.detail.providerId
    );
    const cert = await provider.certStorage.getItem(event.detail.certificateId);
    if (!cert) throw new Error("Cannot get certificate from the provider");

    const rawCert = await provider.certStorage.exportCert("raw", cert);
    const base64 = Convert.ToBase64(rawCert);
    const base64Formatted = base64.match(/.{1,64}/g)?.join("\n");
    const publicKey = `-----BEGIN CERTIFICATE-----\n${base64Formatted}\n-----END CERTIFICATE-----`;

    const privateKey = await provider.keyStorage.getItem(
      event.detail.privateKeyId
    );
    if (!privateKey)
      throw new Error("Cannot get private key from the provider");

    const logedInProvider = await cryptoLogin(provider.id);
    if (logedInProvider) providerRef.current = logedInProvider;
    privateKeyRef.current = privateKey;

    setPublicKey(publicKey);
    return publicKey;
  };

  const signHash = async (hash: string) => {
    const provider = providerRef.current;
    const privateKey = privateKeyRef.current;

    if (!privateKey || !provider) {
      throw new Error(
        "Private key or crypto provider is not available. Cannot sign hash."
      );
    }

    const algorithm = {
      name: privateKey.algorithm.name,
      hash: "SHA-256",
    };

    const signedData = await provider.subtle.sign(
      algorithm,
      privateKey,
      Convert.FromHex(hash)
    );
    return Convert.ToHex(signedData);
  };

  const CertificateSelector = (
    <PeculiarFortifyCertificates
      onSelectionSuccess={async (event) => {
        const publicKey = await getPublicKey(event);
        onCertificateSelection(publicKey, signHash);
      }}
      filters={{ onlyWithPrivateKey: true }}
    />
  );

  return {
    CertificateSelector,
    getPublicKey,
    publicKey,
    signHash,
  };
};

export default useSignature;
