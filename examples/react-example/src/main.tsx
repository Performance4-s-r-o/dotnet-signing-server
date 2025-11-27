import { createRoot } from "react-dom/client";
import App from "./App.tsx";
import { initLogging } from "./logging";

initLogging();

createRoot(document.getElementById("root")!).render(<App />);
