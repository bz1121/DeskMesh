import type { PropsWithChildren } from "react";
import AuthGate from "./AuthGate";

export const appMetadata = {
  title: "DeskMesh · 桌联控制中心",
  description: "在局域网内安全切换键鼠、同步剪贴板、传送文件并协调显示器输入源。",
} as const;

export default function RootLayout({ children }: PropsWithChildren) {
  return <AuthGate>{children}</AuthGate>;
}
