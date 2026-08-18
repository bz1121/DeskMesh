import {
  type FormEvent,
  type KeyboardEvent,
  type PointerEvent,
  type WheelEvent,
  forwardRef,
  useCallback,
  useEffect,
  useImperativeHandle,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  ApiError,
  apiRequest,
  type ConnectionState,
  getSessionToken,
  type PeerSummary,
  type RemoteDesktopDisplay,
  type RemoteDesktopDisplayCatalog,
  type RemoteDesktopSettings,
  jsonRequest,
} from "../web/api";

type RemoteDesktopPanelProps = {
  peers: PeerSummary[];
  connection: ConnectionState;
  pageVisible: boolean;
  onOpenPage: () => void;
  onNotice: (notice: {
    tone: "success" | "warning" | "danger";
    message: string;
  }) => void;
  onSeamlessSessionChange: (session: SeamlessSessionSnapshot | null) => void;
};

export type RemoteDesktopPhase =
  | "idle"
  | "loadingDisplays"
  | "connecting"
  | "waitingFirstFrame"
  | "connected"
  | "error";

export type SeamlessSessionSnapshot = {
  targetDeviceId: string;
  targetDeviceName: string;
  phase: RemoteDesktopPhase;
  message: string;
};

export type RemoteDesktopPanelHandle = {
  startSeamless: (targetDeviceId: string) => void;
  toggleSeamless: (targetDeviceId?: string) => void;
  stopSeamless: (reason?: string) => void;
};

type StreamHeader = {
  type: "hello";
  protocolVersion: 2;
  display: RemoteDesktopDisplay;
  frameWidth: number;
  frameHeight: number;
  framesPerSecond: number;
  jpegQuality: number;
};

const FIRST_FRAME_TIMEOUT_MS = 8_000;

const RemoteDesktopPanel = forwardRef<RemoteDesktopPanelHandle, RemoteDesktopPanelProps>(
  function RemoteDesktopPanel(
    {
      peers,
      connection,
      pageVisible,
      onOpenPage,
      onNotice,
      onSeamlessSessionChange,
    },
    ref,
  ) {
  const [settings, setSettings] = useState<RemoteDesktopSettings>({
    enabled: false,
    framesPerSecond: 30,
    jpegQuality: 60,
  });
  const [settingsLoaded, setSettingsLoaded] = useState(false);
  const [targetId, setTargetId] = useState("");
  const [displays, setDisplays] = useState<RemoteDesktopDisplay[]>([]);
  const [displayIndex, setDisplayIndex] = useState(0);
  const [displayGeneration, setDisplayGeneration] = useState<number | null>(null);
  const [loadingDisplays, setLoadingDisplays] = useState(false);
  const [saving, setSaving] = useState(false);
  const [phase, setPhase] = useState<RemoteDesktopPhase>("idle");
  const [message, setMessage] = useState("选择一台在线设备并读取屏幕。");
  const [streamHeader, setStreamHeader] = useState<StreamHeader | null>(null);
  const [actualFps, setActualFps] = useState(0);
  const [seamlessSession, setSeamlessSession] = useState(false);
  const [seamlessOverlay, setSeamlessOverlay] = useState(false);
  const [activeTargetName, setActiveTargetName] = useState("");
  const socketRef = useRef<WebSocket | null>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const viewerRef = useRef<HTMLDivElement>(null);
  const pendingFrame = useRef<ArrayBuffer | null>(null);
  const drawingFrame = useRef(false);
  const latestPointer = useRef<{ x: number; y: number } | null>(null);
  const pointerAnimation = useRef(0);
  const displayRequest = useRef(0);
  const frameMeter = useRef({ startedAt: 0, frames: 0 });
  const firstFrameTimer = useRef(0);
  const heartbeatTimer = useRef(0);
  const firstFrameDrawn = useRef(false);
  const streamAttempt = useRef(0);
  const seamlessSessionRef = useRef(false);
  const activeTargetIdRef = useRef("");
  const activeTargetNameRef = useRef("");
  const returnFocusRef = useRef<HTMLElement | null>(null);

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
      .catch(() => undefined)
      .finally(() => {
        if (!stopped) setSettingsLoaded(true);
      });
    return () => {
      stopped = true;
    };
  }, []);

  const clearFirstFrameTimer = useCallback(() => {
    window.clearTimeout(firstFrameTimer.current);
    firstFrameTimer.current = 0;
  }, []);

  const clearCanvas = useCallback(() => {
    const canvas = canvasRef.current;
    if (!canvas) return;
    canvas.getContext("2d", { alpha: false })?.clearRect(0, 0, canvas.width, canvas.height);
    canvas.width = 1;
    canvas.height = 1;
  }, []);

  const closeTransport = useCallback(() => {
    clearFirstFrameTimer();
    window.clearInterval(heartbeatTimer.current);
    heartbeatTimer.current = 0;
    const socket = socketRef.current;
    socketRef.current = null;
    if (socket?.readyState === WebSocket.OPEN) {
      try {
        socket.send(JSON.stringify({ type: "release" }));
      } catch {
        // Closing the socket below is the final release fallback.
      }
    }
    if (socket && socket.readyState < WebSocket.CLOSING) {
      socket.close(1000, "用户关闭远程桌面");
    }
    window.cancelAnimationFrame(pointerAnimation.current);
    pointerAnimation.current = 0;
    pendingFrame.current = null;
    latestPointer.current = null;
    frameMeter.current = { startedAt: 0, frames: 0 };
    firstFrameDrawn.current = false;
    setActualFps(0);
    setStreamHeader(null);
  }, [clearFirstFrameTimer]);

  const endSession = useCallback((
    reason: string,
    tone?: "success" | "warning" | "danger",
  ) => {
    const wasSeamless = seamlessSessionRef.current;
    streamAttempt.current += 1;
    displayRequest.current += 1;
    closeTransport();
    clearCanvas();
    seamlessSessionRef.current = false;
    activeTargetIdRef.current = "";
    activeTargetNameRef.current = "";
    setSeamlessSession(false);
    setSeamlessOverlay(false);
    setActiveTargetName("");
    setLoadingDisplays(false);
    setPhase(tone === "danger" || tone === "warning" ? "error" : "idle");
    setMessage(reason);
    if (wasSeamless && tone) onNotice({ tone, message: reason });
    if (wasSeamless) {
      const returnFocus = returnFocusRef.current;
      returnFocusRef.current = null;
      window.requestAnimationFrame(() => {
        if (returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
      });
    }
  }, [clearCanvas, closeTransport, onNotice]);

  const stopSeamless = useCallback((reason = "已返回本机。") => {
    if (!seamlessSessionRef.current) return;
    endSession(reason, "success");
  }, [endSession]);

  useEffect(() => {
    if (!seamlessSession || !targetId) {
      onSeamlessSessionChange(null);
      return;
    }
    onSeamlessSessionChange({
      targetDeviceId: targetId,
      targetDeviceName: activeTargetName || targetId,
      phase,
      message,
    });
  }, [activeTargetName, message, onSeamlessSessionChange, phase, seamlessSession, targetId]);

  useEffect(() => {
    if (!seamlessOverlay) return;
    const handleEscape = (event: globalThis.KeyboardEvent) => {
      if (event.key !== "Escape") return;
      event.preventDefault();
      event.stopPropagation();
      stopSeamless("已按 Esc 返回本机。");
    };
    window.addEventListener("keydown", handleEscape, true);
    return () => window.removeEventListener("keydown", handleEscape, true);
  }, [seamlessOverlay, stopSeamless]);

  useEffect(() => () => {
    streamAttempt.current += 1;
    displayRequest.current += 1;
    closeTransport();
    clearCanvas();
  }, [clearCanvas, closeTransport]);

  const drawPendingFrames = useCallback(async () => {
    if (drawingFrame.current) return;
    drawingFrame.current = true;
    let decodedAttempt = streamAttempt.current;
    try {
      while (pendingFrame.current) {
        const attempt = streamAttempt.current;
        decodedAttempt = attempt;
        const bytes = pendingFrame.current;
        pendingFrame.current = null;
        const bitmap = await createImageBitmap(new Blob([bytes], { type: "image/jpeg" }));
        if (attempt !== streamAttempt.current) {
          bitmap.close();
          continue;
        }
        const canvas = canvasRef.current;
        const context = canvas?.getContext("2d", { alpha: false });
        if (!canvas || !context) {
          bitmap.close();
          throw new Error("Canvas unavailable");
        }
        if (canvas.width !== bitmap.width) canvas.width = bitmap.width;
        if (canvas.height !== bitmap.height) canvas.height = bitmap.height;
        context.drawImage(bitmap, 0, 0);
        bitmap.close();

        if (!firstFrameDrawn.current) {
          firstFrameDrawn.current = true;
          clearFirstFrameTimer();
          setPhase("connected");
          setMessage(`正在查看 ${activeTargetNameRef.current || "远端设备"}`);
          if (seamlessSessionRef.current) setSeamlessOverlay(true);
          window.requestAnimationFrame(() => canvas.focus({ preventScroll: true }));
        }
      }
    } catch {
      // A decode from a superseded socket must never tear down the newer
      // session that replaced it.
      if (decodedAttempt !== streamAttempt.current) return;
      const wasSeamless = seamlessSessionRef.current;
      endSession(
        wasSeamless
          ? "收到画面但浏览器解码失败，已退出无缝模式；可改用直接信号模式。"
          : "浏览器无法解码远程画面。",
        "danger",
      );
    } finally {
      drawingFrame.current = false;
      // A new socket may have queued its first frame while the previous
      // decode was still finishing. Drain it now instead of waiting for a
      // second frame that might never arrive.
      if (pendingFrame.current) void drawPendingFrames();
    }
  }, [clearFirstFrameTimer, endSession]);

  const startStream = useCallback(async (
    selectedTargetId: string,
    selectedDisplayIndex: number,
    selectedGeneration: number,
    isSeamless: boolean,
    targetName: string,
  ) => {
    streamAttempt.current += 1;
    const attempt = streamAttempt.current;
    closeTransport();
    clearCanvas();
    seamlessSessionRef.current = isSeamless;
    activeTargetIdRef.current = selectedTargetId;
    activeTargetNameRef.current = targetName;
    setSeamlessSession(isSeamless);
    setSeamlessOverlay(false);
    setActiveTargetName(targetName);
    setTargetId(selectedTargetId);
    setDisplayIndex(selectedDisplayIndex);
    setPhase("connecting");
    setMessage(`正在通过加密局域网连接 ${targetName}…`);
    firstFrameTimer.current = window.setTimeout(() => {
      if (attempt !== streamAttempt.current || firstFrameDrawn.current) return;
      endSession(
        `已连接到 ${targetName}，但 8 秒内没有收到画面。显示器未切源，本机控制保持不变；可重试或改用直接信号模式。`,
        "danger",
      );
    }, FIRST_FRAME_TIMEOUT_MS);

    try {
      const url = new URL("/api/v1/remote-desktop/stream", window.location.href);
      url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
      url.searchParams.set("targetDeviceId", selectedTargetId);
      url.searchParams.set("displayIndex", String(selectedDisplayIndex));
      url.searchParams.set("generation", String(selectedGeneration));
      url.searchParams.set("token", await getSessionToken());
      if (attempt !== streamAttempt.current) return;

      const socket = new WebSocket(url, "lanswitch.remote-desktop.v2");
      socket.binaryType = "arraybuffer";
      socketRef.current = socket;
      socket.addEventListener("open", () => {
        if (socketRef.current !== socket || attempt !== streamAttempt.current) return;
        const heartbeat = () => {
          if (socket.readyState === WebSocket.OPEN) {
            socket.send(JSON.stringify({ type: "heartbeat" }));
          }
        };
        heartbeat();
        heartbeatTimer.current = window.setInterval(heartbeat, 500);
      });
      socket.addEventListener("message", (event) => {
        if (socketRef.current !== socket || attempt !== streamAttempt.current) return;
        if (typeof event.data === "string") {
          try {
            const header = JSON.parse(event.data) as StreamHeader;
            if (header.type !== "hello" || header.protocolVersion !== 2) throw new Error();
            setStreamHeader(header);
            setPhase("waitingFirstFrame");
            setMessage(`已连接 ${targetName}，正在等待第一帧…`);
          } catch {
            endSession("远程桌面协议无效，已返回本机。", "danger");
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
        if (socketRef.current !== socket || attempt !== streamAttempt.current) return;
        const reason = event.reason || `${targetName} 的远程画面已断开，已返回本机。`;
        endSession(reason, isSeamless ? "warning" : event.code === 1000 ? undefined : "danger");
      });
      socket.addEventListener("error", () => {
        if (socketRef.current !== socket || attempt !== streamAttempt.current) return;
        endSession("远程桌面连接失败，显示器未切源，本机控制保持不变。", "danger");
      });
    } catch (error) {
      if (attempt !== streamAttempt.current) return;
      endSession(
        error instanceof Error ? error.message : "无法建立远程桌面连接。",
        "danger",
      );
    }
  }, [clearCanvas, closeTransport, drawPendingFrames, endSession]);

  const loadDisplays = useCallback(async (selectedTargetId = targetId) => {
    const requestId = ++displayRequest.current;
    if (!selectedTargetId) {
      setDisplays([]);
      setDisplayGeneration(null);
      return;
    }
    setDisplayGeneration(null);
    setLoadingDisplays(true);
    try {
      const result = await apiRequest<RemoteDesktopDisplayCatalog>(
        `/api/v1/remote-desktop/displays?targetDeviceId=${encodeURIComponent(selectedTargetId)}`,
        {},
        10_000,
      );
      if (requestId !== displayRequest.current) return;
      setDisplays(result.displays);
      setDisplayGeneration(result.generation);
      setDisplayIndex(result.displays.find((display) => display.primary)?.index ?? result.displays[0]?.index ?? 0);
      setMessage(result.displays.length ? "屏幕信息已读取，可以开始连接。" : "远端没有可用屏幕。");
    } catch (error) {
      if (requestId !== displayRequest.current) return;
      setDisplays([]);
      setDisplayGeneration(null);
      setPhase("error");
      setMessage(describeRemoteDesktopError(error, "远端设备"));
    } finally {
      if (requestId === displayRequest.current) setLoadingDisplays(false);
    }
  }, [targetId]);

  const startSeamless = useCallback(async (selectedTargetId: string) => {
    if (!selectedTargetId) return;
    if (
      seamlessSessionRef.current &&
      activeTargetIdRef.current === selectedTargetId
    ) {
      return;
    }
    const peer = capablePeers.find((candidate) => candidate.id === selectedTargetId);
    if (!peer) {
      onNotice({
        tone: "warning",
        message: "目标设备当前不可用于无缝远程；请确认它已配对、在线并支持远程桌面。",
      });
      return;
    }

    returnFocusRef.current =
      document.activeElement instanceof HTMLElement ? document.activeElement : null;

    streamAttempt.current += 1;
    displayRequest.current += 1;
    closeTransport();
    clearCanvas();
    seamlessSessionRef.current = true;
    activeTargetIdRef.current = selectedTargetId;
    activeTargetNameRef.current = peer.name || selectedTargetId;
    setSeamlessSession(true);
    setSeamlessOverlay(false);
    setActiveTargetName(peer.name || selectedTargetId);
    setTargetId(selectedTargetId);
    setDisplays([]);
    setDisplayGeneration(null);
    setPhase("loadingDisplays");
    setMessage(`正在读取 ${peer.name || "远端设备"} 的屏幕信息…`);

    const requestId = ++displayRequest.current;
    setLoadingDisplays(true);
    try {
      const result = await apiRequest<RemoteDesktopDisplayCatalog>(
        `/api/v1/remote-desktop/displays?targetDeviceId=${encodeURIComponent(selectedTargetId)}`,
        {},
        10_000,
      );
      if (requestId !== displayRequest.current || !seamlessSessionRef.current) return;
      if (result.displays.length === 0) {
        endSession("对方没有可用屏幕，无法进入无缝模式。", "warning");
        return;
      }
      const nextDisplayIndex =
        result.displays.find((display) => display.primary)?.index ?? result.displays[0].index;
      setDisplays(result.displays);
      setDisplayGeneration(result.generation);
      setDisplayIndex(nextDisplayIndex);
      await startStream(
        selectedTargetId,
        nextDisplayIndex,
        result.generation,
        true,
        peer.name || selectedTargetId,
      );
    } catch (error) {
      if (requestId !== displayRequest.current || !seamlessSessionRef.current) return;
      endSession(describeRemoteDesktopError(error, peer.name || "对方"), "danger");
    } finally {
      if (requestId === displayRequest.current) setLoadingDisplays(false);
    }
  }, [capablePeers, clearCanvas, closeTransport, endSession, onNotice, startStream]);

  const toggleSeamless = useCallback((selectedTargetId?: string) => {
    if (seamlessSessionRef.current) {
      stopSeamless("切换快捷键已返回本机。");
      return;
    }
    if (selectedTargetId) void startSeamless(selectedTargetId);
  }, [startSeamless, stopSeamless]);

  useImperativeHandle(
    ref,
    () => ({ startSeamless, toggleSeamless, stopSeamless }),
    [startSeamless, stopSeamless, toggleSeamless],
  );

  async function connect() {
    if (!targetId || displays.length === 0 || displayGeneration === null) return;
    const targetName = peers.find((peer) => peer.id === targetId)?.name || targetId;
    await startStream(targetId, displayIndex, displayGeneration, false, targetName);
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
    endSession("请选择屏幕并重新连接。");
    setTargetId(nextTargetId);
    setDisplays([]);
    setDisplayIndex(0);
    if (nextTargetId) void loadDisplays(nextTargetId);
  }

  function send(messageValue: object) {
    const socket = socketRef.current;
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(messageValue));
  }

  function requestSecureAttention() {
    if (phase !== "connected") return;
    send({ type: "secure-attention" });
    onNotice({
      tone: "warning",
      message: "已请求目标电脑显示 Ctrl+Alt+Del 界面；目标端必须已安装 UAC 组件、启用锁屏会话控制，并允许软件安全注意序列。",
    });
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

  const sessionOpen =
    phase === "loadingDisplays" ||
    phase === "connecting" ||
    phase === "waitingFirstFrame" ||
    phase === "connected";

  return (
    <section
      id="remote-desktop"
      className={`content-section remote-desktop-section page-view${pageVisible ? " is-page-visible" : " is-page-background"}`}
      aria-labelledby="remote-desktop-title"
      aria-hidden={!pageVisible && !seamlessOverlay && !sessionOpen}
    >
      {!pageVisible && sessionOpen && !seamlessOverlay ? (
        <div className="remote-background-activity" role="status" aria-live="polite">
          <span className="status-dot online" aria-hidden="true" />
          <div>
            <strong>远程桌面仍在后台运行</strong>
            <small>{activeTargetName || message}</small>
          </div>
          <button type="button" className="secondary-button" onClick={onOpenPage}>
            返回远程桌面
          </button>
          <button
            type="button"
            className="danger-button"
            onClick={() => endSession("远程桌面已关闭。")}
          >
            断开
          </button>
        </div>
      ) : null}
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
            disabled={!settingsLoaded || connection !== "online" || saving}
            onChange={(event) => setSettings((current) => ({ ...current, enabled: event.target.checked }))}
          />
        </div>
        <label>
          <span>帧率</span>
          <select
            value={settings.framesPerSecond}
            disabled={!settingsLoaded || saving}
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
            disabled={!settingsLoaded || saving}
            onChange={(event) => setSettings((current) => ({ ...current, jpegQuality: Number(event.target.value) }))}
          >
            <option value={45}>节省带宽</option>
            <option value={60}>平衡（推荐）</option>
            <option value={75}>清晰</option>
            <option value={85}>很清晰</option>
          </select>
        </label>
        <button type="submit" className="secondary-button" disabled={!settingsLoaded || connection !== "online" || saving}>
          {!settingsLoaded ? "正在读取设置…" : saving ? "保存中…" : "保存权限与画质"}
        </button>
      </form>

      <div className="remote-desktop-toolbar">
        <label>
          <span>远程设备</span>
          <select value={targetId} onChange={(event) => changeTarget(event.target.value)} disabled={sessionOpen}>
            <option value="">选择在线设备</option>
            {capablePeers.map((peer) => <option key={peer.id} value={peer.id}>{peer.name || peer.id}</option>)}
          </select>
        </label>
        <label>
          <span>远程屏幕</span>
          <select
            value={displayIndex}
            onChange={(event) => setDisplayIndex(Number(event.target.value))}
            disabled={!displays.length || sessionOpen}
          >
            {displays.length === 0 ? <option value={0}>暂无屏幕</option> : displays.map((display) => (
              <option key={`${display.deviceName}-${display.index}`} value={display.index}>
                {display.primary ? "主屏 · " : ""}{display.deviceName} · {display.width}×{display.height}
              </option>
            ))}
          </select>
        </label>
        <button type="button" className="secondary-button" onClick={() => void loadDisplays()} disabled={!targetId || loadingDisplays || sessionOpen}>
          {loadingDisplays ? "读取中…" : "刷新屏幕"}
        </button>
        {sessionOpen ? (
          <button type="button" className="danger-button" onClick={() => endSession("远程桌面已关闭。")}>断开远程桌面</button>
        ) : (
          <button type="button" className="primary-button" onClick={() => void connect()} disabled={!targetId || displays.length === 0}>
            打开远程桌面
          </button>
        )}
      </div>

      <div
        ref={viewerRef}
        className={`remote-viewer phase-${phase}${seamlessOverlay ? " is-seamless-overlay" : ""}`}
        aria-busy={phase !== "idle" && phase !== "connected" && phase !== "error"}
      >
        <div className="remote-viewer-status" role="status" aria-live="polite">
          <span className={`status-dot ${phase === "connected" ? "online" : ""}`} />
          <strong>{message}</strong>
          {streamHeader ? (
            <small>
              {streamHeader.display.width}×{streamHeader.display.height} → {streamHeader.frameWidth}×{streamHeader.frameHeight}
              {actualFps > 0 ? ` · 实际 ${actualFps} FPS / 目标 ${streamHeader.framesPerSecond} FPS` : ""}
            </small>
          ) : null}
          {seamlessOverlay ? (
            <>
              <span className="remote-overlay-hint">按 Esc 返回本机</span>
              <button type="button" className="secondary-button" onClick={requestSecureAttention}>
                发送 Ctrl+Alt+Del
              </button>
              <button type="button" className="danger-button remote-return-button" onClick={() => stopSeamless()}>
                返回本机
              </button>
            </>
          ) : (
            <>
              <button type="button" className="small-button" disabled={phase !== "connected"} onClick={requestSecureAttention}>
                Ctrl+Alt+Del
              </button>
              <button type="button" className="small-button" disabled={phase !== "connected"} onClick={() => void viewerRef.current?.requestFullscreen()}>
                全屏
              </button>
            </>
          )}
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
              <strong>{remotePhaseTitle(phase)}</strong>
              <span>{remotePhaseDetail(phase)}</span>
            </div>
          ) : null}
        </div>
      </div>
      <p className="remote-desktop-note">
        远程桌面会话会同时尝试传输画面、画面内输入与系统音频；音频失败不会中断画面和输入。90 FPS 是目标上限，实际帧率取决于远端 CPU、分辨率和局域网。安装 UAC 组件后可控制 UAC；目标端另行开启锁屏扩展并配置 Windows 策略后，可解锁当前已登录的锁定会话。开机登录、注销后的登录、BIOS 和部分反作弊程序仍需在远端本机操作。
      </p>
    </section>
  );
  },
);

export default RemoteDesktopPanel;

function describeRemoteDesktopError(error: unknown, targetName: string) {
  if (error instanceof ApiError && error.status === 409) {
    return `对方未允许远程桌面，请在 ${targetName} 的 DeskMesh 开启“允许已配对设备查看和控制本机”。`;
  }
  return error instanceof Error ? error.message : `无法读取 ${targetName} 的远程屏幕。`;
}

function remotePhaseTitle(phase: RemoteDesktopPhase) {
  const labels: Record<RemoteDesktopPhase, string> = {
    idle: "远程画面尚未打开",
    loadingDisplays: "正在读取远端屏幕…",
    connecting: "正在建立加密连接…",
    waitingFirstFrame: "已连接，正在等待第一帧…",
    connected: "远程画面已连接",
    error: "远程画面不可用",
  };
  return labels[phase];
}

function remotePhaseDetail(phase: RemoteDesktopPhase) {
  if (phase === "waitingFirstFrame") return "收到并成功绘制第一张画面后才会进入无缝全屏。";
  if (phase === "loadingDisplays" || phase === "connecting") return "显示器不会切换输入，本机控制保持不变。";
  if (phase === "error") return "可重试，或在上方改用直接信号模式。";
  return "选择设备和屏幕后开始连接。";
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
