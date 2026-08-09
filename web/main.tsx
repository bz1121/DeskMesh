import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import ControlConsole from "../app/page";
import RootLayout from "../app/layout";
import "../app/globals.css";

const container = document.getElementById("root");

if (!container) {
  throw new Error("DeskMesh 控制台缺少根节点");
}

createRoot(container).render(
  <StrictMode>
    <RootLayout>
      <ControlConsole />
    </RootLayout>
  </StrictMode>,
);
