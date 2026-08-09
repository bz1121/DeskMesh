import {
  type FormEvent,
  type KeyboardEvent,
  type PointerEvent,
  type WheelEvent,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  apiRequest,
  type ConnectionState,
  getSessionToken,
  type PeerSummary,
  type RemoteDesktopDisplay,
  type RemoteDesktopSettings,
  jsonRequest,
} from "../web/api";

type RemoteDesktopPanelProps = {
  peers: PeerSummary[];
  connection: ConnectionState;
  onNotice: (notice: {
    tone: "success" | "warning" | "danger";
    message: string;
  }) => void;
};

type StreamHeader = {
  type: "hello";
  protocolVersion: number;
  display: RemoteDesktopDisplay;
  frameWidth: number;
  frameHeight: number;
  framesPerSecond: number;
  jpegQuality: number;
};

export default function RemoteDesktopPanel({
  peers,
  connection,
  onNotice,
}: RemoteDesktopPanelProps) {
  const [settings, setSettings] = useState<RemoteDesktopSettings>({
    enabled: true,
    framesPerSecond: 30,
    jpegQuality: 60,
  });
  const [targetId, setTargetId] = useState("");
  const [displays, setDisplays] = useState<RemoteDesktopDisplay[]>([]);
  const [displayIndex, setDisplayIndex] = useState(0);
  const [loadingDisplays, setLoadingDisplays] = useState(false);
  const [saving, setSaving] = useState(false);
  const [phase, setPhase] = useState<"idle" | "connecting" | "connected" | "error">("idle");
  const [message, setMessage] = useState("选择一台在线设备并读取屏幕。");
  const [streamHeader, setStreamHeader] = useState<StreamHeader | null>(null);
  const [actualFps, setActualFps] = useState(0);
  const socketRef = useRef<WebSocket | null>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const viewerRef = useRef<HTMLDivElement>(null);
  const pendingFrame = useRef<ArrayBuffer | null>(null);
  const drawingFrame = useRef(false);
  const latestPointer = useRef<{ x: number; y: number } | null>(null);
  const pointerAnimation = useRef(0);
  const displayRequest = useRef(0);
  const frameMeter = useRef({ startedAt: 0, frames: 0 });

  const capablePeers = useMemo(
    () =>
      peers.filter(
        (peer) =>
          peer.online &&
          peer.paired !== false &&
          (peer.capabilities ?? []).some(
            (capability) => capability.toLowerCase() === "remote-desktop",
          ),
      ),
    [peers],
  );

  useEffect(() => {
    let stopped = false;
    void apiRequest<RemoteDesktopSettings>("/api/v1/remote-desktop")
      .then((value) => {
        if (!stopped) setSettings(value);
      })
      .catch(() => undefined);
    return () => {
      stopped = true;
    };
  }, []);

  const disconnect = useCallback((reason = "远程桌面连接已关闭。") => {
    const socket = socketRef.current;
    socketRef.current = null;
    if (socket && socket.readyState < WebSocket.CLOSING) socket.close(1000, "用户关闭远程桌面");
    window.cancelAnimationFrame(pointerAnimation.current);
    pointerAnimation.current = 0;
    pendingFrame.current = null;
    frameMeter.current = { startedAt: 0, frames: 0 };
    setActualFps(0);
    setStreamHeader(null);
    setPhase("idle");
    setMessage(reason);
  }, []);

  useEffect(() => () => disconnect("远程桌面已关闭。"), [disconnect]);

  const loadDisplays = useCallback(async (selectedTargetId = targetId) => {
    const requestId = ++displayRequest.current;
    if (!selectedTargetId) {
      setDisplays([]);
      return;
    }
    setLoadingDisplays(true);
    try {
      const result = await apiRequest<RemoteDesktopDisplay[]>(
        `/api/v1/remote-desktop/displays?targetDeviceId=${encodeURIComponent(selectedTargetId)}`,
        {},
        10_000,
      );
      if (requestId !== displayRequest.current) return;
      setDisplays(result);
      setDisplayIndex(result.find((display) => display.primary)?.index ?? result[0]?.index ?? 0);
      setMessage(result.length ? "屏幕信息已读取，可以开始连接。" : "远端没有可用屏幕。");
    } catch (error) {
      if (requestId !== displayRequest.current) return;
      setDisplays([]);
      setPhase("error");
      setMessage(error instanceof Error ? error.message : "无法读取远端屏幕。");
    } finally {
      if (requestId === displayRequest.current) setLoadingDisplays(false);
    }
  }, [targetId]);

  const drawPendingFrames = useCallback(async () => {
    if (drawingFrame.current) return;
    drawingFrame.current = true;
    try {
      while (pendingFrame.current) {
        const bytes = pendingFrame.current;
        pendingFrame.current = null;
        const bitmap = await createImageBitmap(new Blob([bytes], { type: "image/jpeg" }));
        const canvas = canvasRef.current;
        if (canvas) {
          if (canvas.width !== bitmap.width) canvas.width = bitmap.width;
          if (canvas.height !== bitmap.height) canvas.height = bitmap.height;
          canvas.getContext("2d", { alpha: false })?.drawImage(bitmap, 0, 0);
        }
        bitmap.close();
      }
    } catch {
      setPhase("error");
      setMessage("浏览器无法解码远程画面。");
    } finally {
      drawingFrame.current = false;
    }
  }, []);

  async function connect() {
    if (!targetId || displays.length === 0) return;
    disconnect("正在建立远程桌面连接。");
    setPhase("connecting");
    setMessage("正在通过加密局域网连接远程画面…");
    try {
      const url = new URL("/api/v1/remote-desktop/stream", window.location.href);
      url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
      url.searchParams.set("targetDeviceId", targetId);
      url.searchParams.set("displayIndex", String(displayIndex));
      url.searchParams.set("token", await getSessionToken());
      const socket = new WebSocket(url, "lanswitch.remote-desktop.v1");
      socket.binaryType = "arraybuffer";
      socketRef.current = socket;
      socket.addEventListener("message", (event) => {
        if (typeof event.data === "string") {
          try {
            const header = JSON.parse(event.data) as StreamHeader;
            if (header.type !== "hello" || header.protocolVersion !== 1) throw new Error();
            setStreamHeader(header);
            setPhase("connected");
            setMessage(`正在查看 ${header.display.deviceName} · ${header.framesPerSecond} FPS`);
          } catch {
            socket.close(1002, "远程桌面协议无效");
          }
          return;
        }
        if (event.data instanceof ArrayBuffer) {
          const now = performance.now();
          const meter = frameMeter.current;
          if (meter.startedAt === 0) meter.startedAt = now;
          meter.frames += 1;
          const elapsed = now - meter.startedAt;
          if (elapsed >= 1000) {
            setActualFps(Math.round((meter.frames * 1000) / elapsed));
            frameMeter.current = { startedAt: now, frames: 0 };
          }
          pendingFrame.current = event.data;
          void drawPendingFrames();
        }
      });
      socket.addEventListener("close", (event) => {
        if (socketRef.current !== socket) return;
        socketRef.current = null;
        setStreamHeader(null);
        setPhase(event.code === 1000 ? "idle" : "error");
        setMessage(event.reason || "远程桌面连接已断开。");
      });
      socket.addEventListener("error", () => {
        if (socketRef.current === socket) {
          setPhase("error");
          setMessage("远程桌面连接失败，请查看诊断日志。");
        }
        socket.close();
      });
    } catch (error) {
      setPhase("error");
      setMessage(error instanceof Error ? error.message : "无法建立远程桌面连接。");
    }
  }

  async function saveSettings(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setSaving(true);
    try {
      const result = await apiRequest<RemoteDesktopSettings>(
        "/api/v1/remote-desktop",
        jsonRequest("PUT", settings),
      );
      setSettings(result);
      onNotice({
        tone: "success",
        message: result.enabled
          ? "已允许配对设备查看并控制本机桌面。"
          : "远程桌面接入已关闭。",
      });
    } catch (error) {
      onNotice({
        tone: "danger",
        message: error instanceof Error ? error.message : "远程桌面设置保存失败。",
      });
    } finally {
      setSaving(false);
    }
  }

  function changeTarget(nextTargetId: string) {
    displayRequest.current += 1;
    setLoadingDisplays(false);
    disconnect("请选择屏幕并重新连接。");
    setTargetId(nextTargetId);
    setDisplays([]);
    setDisplayIndex(0);
    if (nextTargetId) void loadDisplays(nextTargetId);
  }

  function send(messageValue: object) {
    const socket = socketRef.current;
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(messageValue));
  }

  function coordinates(event: { clientX: number; clientY: number }) {
    const canvas = canvasRef.current;
    if (!canvas) return null;
    const bounds = canvas.getBoundingClientRect();
    if (bounds.width <= 0 || bounds.height <= 0) return null;
    return {
      x: Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width)),
      y: Math.max(0, Math.min(1, (event.clientY - bounds.top) / bounds.height)),
    };
  }

  function handlePointerMove(event: PointerEvent<HTMLCanvasElement>) {
    const point = coordinates(event);
    if (!point || phase !== "connected") return;
    latestPointer.current = point;
    if (pointerAnimation.current) return;
    pointerAnimation.current = window.requestAnimationFrame(() => {
      pointerAnimation.current = 0;
      if (latestPointer.current) send({ type: "pointer", ...latestPointer.current });
    });
  }

  function handlePointerButton(event: PointerEvent<HTMLCanvasElement>, down: boolean) {
    event.preventDefault();
    if (phase !== "connected" || event.button < 0 || event.button > 2) return;
    const point = coordinates(event);
    if (!point) return;
    if (down) {
      event.currentTarget.focus();
      event.currentTarget.setPointerCapture(event.pointerId);
    } else if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
    send({ type: "mouse-button", button: event.button, down, ...point });
  }

  function handleWheel(event: WheelEvent<HTMLCanvasElement>) {
    event.preventDefault();
    if (phase !== "connected") return;
    const point = coordinates(event);
    if (!point) return;
    const delta = Math.max(-1200, Math.min(1200, Math.round(-event.deltaY)));
    send({ type: "wheel", delta, ...point });
  }

  function handleKey(event: KeyboardEvent<HTMLCanvasElement>, down: boolean) {
    const virtualKey = browserKeyToVirtualKey(event.code, event.key);
    if (!virtualKey || phase !== "connected") return;
    event.preventDefault();
    event.stopPropagation();
    send({ type: "key", virtualKey, down, extended: isExtendedKey(event.code) });
  }

  return (
    <section id="remote-desktop" className="content-section remote-desktop-section" aria-labelledby="remote-desktop-title">
      <div className="section-heading">
        <div>
          <span className="eyebrow">局域网远程控制</span>
          <h2 id="remote-desktop-title">不切换显示器，也能看到并操作对方桌面</h2>
        </div>
        <p>画面与输入只在已配对设备间传输；无需两台电脑连接同一台显示器。</p>
      </div>

      <form className="remote-desktop-settings" onSubmit={saveSettings}>
        <div className="remote-permission-toggle">
          <span>
            <strong>允许已配对设备查看和控制本机</strong>
            <small>关闭后会终止正在查看本机的会话</small>
          </span>
          <input
            id="remote-desktop-enabled"
            type="checkbox"
            aria-label="允许已配对设备查看和控制本机"
            checked={settings.enabled}
            disabled={connection !== "online" || saving}
            onChange={(event) => setSettings((current) => ({ ...current, enabled: event.target.checked }))}
          />
        </div>
        <label>
          <span>帧率</span>
          <select
            value={settings.framesPerSecond}
            disabled={saving}
            onChange={(event) => setSettings((current) => ({ ...current, framesPerSecond: Number(event.target.value) }))}
          >
            <option value={10}>10 FPS（省流量）</option>
            <option value={20}>20 FPS（低负载）</option>
            <option value={30}>30 FPS（推荐）</option>
            <option value={60}>60 FPS（流畅）</option>
            <option value={90}>90 FPS（实验，硬件允许时）</option>
          </select>
        </label>
        <label>
          <span>画质</span>
          <select
            value={settings.jpegQuality}
            disabled={saving}
            onChange={(event) => setSettings((current) => ({ ...current, jpegQuality: Number(event.target.value) }))}
          >
            <option value={45}>节省带宽</option>
            <option value={60}>平衡（推荐）</option>
            <option value={75}>清晰</option>
            <option value={85}>很清晰</option>
          </select>
        </label>
        <button type="submit" className="secondary-button" disabled={connection !== "online" || saving}>
          {saving ? "保存中…" : "保存权限与画质"}
        </button>
      </form>

      <div className="remote-desktop-toolbar">
        <label>
          <span>远程设备</span>
          <select value={targetId} onChange={(event) => changeTarget(event.target.value)} disabled={phase === "connected"}>
            <option value="">选择在线设备</option>
            {capablePeers.map((peer) => <option key={peer.id} value={peer.id}>{peer.name || peer.id}</option>)}
          </select>
        </label>
        <label>
          <span>远程屏幕</span>
          <select
            value={displayIndex}
            onChange={(event) => setDisplayIndex(Number(event.target.value))}
            disabled={!displays.length || phase === "connected"}
          >
            {displays.length === 0 ? <option value={0}>暂无屏幕</option> : displays.map((display) => (
              <option key={`${display.deviceName}-${display.index}`} value={display.index}>
                {display.primary ? "主屏 · " : ""}{display.deviceName} · {display.width}×{display.height}
              </option>
            ))}
          </select>
        </label>
        <button type="button" className="secondary-button" onClick={() => void loadDisplays()} disabled={!targetId || loadingDisplays || phase === "connected"}>
          {loadingDisplays ? "读取中…" : "刷新屏幕"}
        </button>
        {phase === "connected" || phase === "connecting" ? (
          <button type="button" className="danger-button" onClick={() => disconnect()}>断开远程桌面</button>
        ) : (
          <button type="button" className="primary-button" onClick={() => void connect()} disabled={!targetId || displays.length === 0}>
            打开远程桌面
          </button>
        )}
      </div>

      <div ref={viewerRef} className={`remote-viewer phase-${phase}`}>
        <div className="remote-viewer-status">
          <span className={`status-dot ${phase === "connected" ? "online" : ""}`} />
          <strong>{message}</strong>
          {streamHeader ? (
            <small>
              {streamHeader.display.width}×{streamHeader.display.height} → {streamHeader.frameWidth}×{streamHeader.frameHeight}
              {actualFps > 0 ? ` · 实际 ${actualFps} FPS / 目标 ${streamHeader.framesPerSecond} FPS` : ""}
            </small>
          ) : null}
          <button type="button" className="small-button" disabled={phase !== "connected"} onClick={() => void viewerRef.current?.requestFullscreen()}>
            全屏
          </button>
        </div>
        <div className="remote-canvas-wrap">
          <canvas
            ref={canvasRef}
            className="remote-canvas"
            tabIndex={0}
            aria-label="远程桌面画面，点击后可发送键盘和鼠标输入"
            onPointerMove={handlePointerMove}
            onPointerDown={(event) => handlePointerButton(event, true)}
            onPointerUp={(event) => handlePointerButton(event, false)}
            onWheel={handleWheel}
            onKeyDown={(event) => handleKey(event, true)}
            onKeyUp={(event) => handleKey(event, false)}
            onBlur={() => send({ type: "release" })}
            onContextMenu={(event) => event.preventDefault()}
          />
          {phase !== "connected" ? (
            <div className="remote-viewer-empty">
              <strong>{phase === "connecting" ? "正在建立加密连接…" : "远程画面尚未打开"}</strong>
              <span>选择设备和屏幕后开始连接。</span>
            </div>
          ) : null}
        </div>
      </div>
      <p className="remote-desktop-note">
        远程桌面会话会同时尝试传输画面、画面内输入与系统音频；音频失败不会中断画面和输入。90 FPS 是目标上限，实际帧率取决于远端 CPU、分辨率和局域网。仅支持登录后的普通 Windows 桌面；UAC、锁屏、Ctrl+Alt+Del、BIOS 和部分反作弊程序仍需在远端本机操作。
      </p>
    </section>
  );
}

function browserKeyToVirtualKey(code: string, key: string) {
  if (/^Key[A-Z]$/.test(code)) return code.charCodeAt(3);
  if (/^Digit[0-9]$/.test(code)) return code.charCodeAt(5);
  if (/^F(?:[1-9]|1[0-9]|2[0-4])$/.test(code)) return 0x70 + Number(code.slice(1)) - 1;
  if (/^Numpad[0-9]$/.test(code)) return 0x60 + Number(code.slice(6));
  const map: Record<string, number> = {
    Backspace: 0x08, Tab: 0x09, Enter: 0x0d, NumpadEnter: 0x0d,
    ShiftLeft: 0xa0, ShiftRight: 0xa1, ControlLeft: 0xa2, ControlRight: 0xa3,
    AltLeft: 0xa4, AltRight: 0xa5, Pause: 0x13, CapsLock: 0x14, Escape: 0x1b,
    Space: 0x20, PageUp: 0x21, PageDown: 0x22, End: 0x23, Home: 0x24,
    ArrowLeft: 0x25, ArrowUp: 0x26, ArrowRight: 0x27, ArrowDown: 0x28,
    PrintScreen: 0x2c, Insert: 0x2d, Delete: 0x2e, MetaLeft: 0x5b, MetaRight: 0x5c,
    NumpadMultiply: 0x6a, NumpadAdd: 0x6b, NumpadSubtract: 0x6d,
    NumpadDecimal: 0x6e, NumpadDivide: 0x6f, NumLock: 0x90, ScrollLock: 0x91,
    Semicolon: 0xba, Equal: 0xbb, Comma: 0xbc, Minus: 0xbd, Period: 0xbe,
    Slash: 0xbf, Backquote: 0xc0, BracketLeft: 0xdb, Backslash: 0xdc,
    BracketRight: 0xdd, Quote: 0xde,
  };
  return map[code] ?? (key.length === 1 ? key.toUpperCase().charCodeAt(0) : 0);
}

function isExtendedKey(code: string) {
  return code.startsWith("Arrow") || [
    "Insert", "Delete", "Home", "End", "PageUp", "PageDown",
    "ControlRight", "AltRight", "MetaLeft", "MetaRight", "NumpadEnter", "NumpadDivide",
  ].includes(code);
}
