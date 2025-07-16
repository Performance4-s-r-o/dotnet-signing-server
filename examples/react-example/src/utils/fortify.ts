import type { SocketCrypto, SocketProvider } from "@webcrypto-local/client";

// eslint-disable-next-line
type WSType = any;

let ws: SocketProvider;
export const initWs = async (): Promise<WSType> => {
  // eslint-disable-next-line
  const storage = await (window as any).WebcryptoSocket.BrowserStorage.create();
  // eslint-disable-next-line
  return new (window as any).WebcryptoSocket.SocketProvider({ storage });
};

export const connectToFortify = async (): Promise<SocketProvider> => {
  if (!ws) ws = await initWs();
  return await new Promise((resolve, reject) => {
    ws.connect("127.0.0.1:31337")
      .on("error", (e) => {
        console.error(e);
        reject(
          new Error(
            "Cannot connect to Fortify. Make sure the application is running."
          )
        );
      })
      .on("listening", () => resolve(ws));
  });
};

export const cryptoLogin = async (
  providerId?: string
): Promise<SocketCrypto | undefined> => {
  if (!providerId) return Promise.resolve(undefined);
  const conn = await connectToFortify();
  const isLoggedIn = await conn.isLoggedIn();
  if (!isLoggedIn) {
    await conn.challenge();
    await conn.login();
  }
  return await conn.getCrypto(providerId);
};
