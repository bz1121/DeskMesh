import {
  type ChangeEvent,
  type DragEvent,
  type FormEvent,
  type KeyboardEvent,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  ApiError,
  apiRequest,
  type AgentStatus,
  type AudioSettings,
  type ClipboardPolicy,
  type ClipboardSnapshot,
  type ConnectionState,
  type DisplayInfo,
  type DiagnosticLog,
  type FileOffer,
  getSessionToken,
  type HotkeySettings,
  jsonRequest,
  type LocalPairingCode,
  type PeerSummary,
  resultAccepted,
  resetSessionToken,
  type SecurityStatus,
  unwrapList,
  type WriteResult,
} from "../web/api";
import RemoteDesktopPanel from "./RemoteDesktopPanel";

const DEFAULT_POLICY: ClipboardPolicy = {
  enabled: false,
  direction: "disabled",
  allowText: true,
  allowImages: false,
  allowFiles: false,
  maxTextBytes: 262_144,
  maxImageBytes: 10_485_760,
  maxFileBytes: 104_857_600,
};

const MAX_FILE_BYTES = 2 * 1024 * 1024 * 1024;
const FILE_UPLOAD_CANCELLED_MESSAGE = "已取消本地暂存，未创建传送提议。";

type Notice = {
  tone: "success" | "warning" | "danger";
  message: string;
};

type SnapshotData = {
  status: AgentStatus | null;
  peers: PeerSummary[];
  clipboard: ClipboardSnapshot | null;
  offers: FileOffer[];
  displays: DisplayInfo[];
  security: SecurityStatus | null;
  hotkeys: HotkeySettings | null;
  audio: AudioSettings | null;
  diagnostics: DiagnosticLog[];
};

export default function ControlConsole() {
  const [connection, setConnection] = useState<ConnectionState>("checking");
  const [snapshot, setSnapshot] = useState<SnapshotData>({
    status: null,
    peers: [],
    clipboard: null,
    offers: [],
    displays: [],
    security: null,
    hotkeys: null,
    audio: null,
    diagnostics: [],
  });
  const [policy, setPolicy] = useState<ClipboardPolicy>(DEFAULT_POLICY);
  const [busy, setBusy] = useState<string | null>(null);
  const [notice, setNotice] = useState<Notice | null>(null);
  const [lastSync, setLastSync] = useState<Date | null>(null);
  const [eventsConnected, setEventsConnected] = useState(false);
  const [pairingAddress, setPairingAddress] = useState("");
  const [pairingAddressSource, setPairingAddressSource] = useState<string | null>(
    null,
  );
  const [pairingCode, setPairingCode] = useState("");
  const [fileTarget, setFileTarget] = useState("");
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [uploadProgress, setUploadProgress] = useState<number | null>(null);
  const [isFileDragging, setIsFileDragging] = useState(false);
  const [fileError, setFileError] = useState<string | null>(null);
  const [fileOfferCreated, setFileOfferCreated] = useState(false);
  const [fileCancelled, setFileCancelled] = useState(false);
  const [fileCancelling, setFileCancelling] = useState(false);
  const [selectedMonitor, setSelectedMonitor] = useState("");
  const [mappingTarget, setMappingTarget] = useState("");
  const [mappingLabel, setMappingLabel] = useState("");
  const [mappingValue, setMappingValue] = useState("");
  const [compatibilityRiskAccepted, setCompatibilityRiskAccepted] =
    useState(false);
  const [localHotkey, setLocalHotkey] = useState("Ctrl+Alt+Shift+F11");
  const [toggleHotkey, setToggleHotkey] = useState("Ctrl+Alt+Shift+F12");
  const [audioEnabled, setAudioEnabled] = useState(true);
  const [audioVolume, setAudioVolume] = useState(100);
  const refreshing = useRef(false);
  const hotkeysDirty = useRef(false);
  const audioDirty = useRef(false);
  const pairingAddressInput = useRef<HTMLInputElement>(null);
  const pairingAddressEdited = useRef(false);
  const fileInput = useRef<HTMLInputElement>(null);
  const fileDragDepth = useRef(0);
  const fileUploadRequest = useRef<XMLHttpRequest | null>(null);
  const fileUploadCancelRequested = useRef(false);

  const refreshSnapshot = useCallback(async (quiet = false) => {
    if (refreshing.current) return;
    refreshing.current = true;

    try {
      const status = await apiRequest<AgentStatus>("/api/v1/status");
      setConnection("online");

      const [peersResult, clipboardResult, offersResult, displaysResult, securityResult, hotkeysResult, audioResult, diagnosticsResult] =
        await Promise.allSettled([
          apiRequest<unknown>("/api/v1/peers"),
          apiRequest<ClipboardSnapshot | ClipboardPolicy>("/api/v1/clipboard"),
          apiRequest<unknown>("/api/v1/files/offers"),
          apiRequest<unknown>("/api/v1/displays"),
          apiRequest<SecurityStatus | { security?: SecurityStatus }>(
            "/api/v1/security",
          ),
          apiRequest<HotkeySettings>("/api/v1/hotkeys"),
          apiRequest<AudioSettings>("/api/v1/audio"),
          apiRequest<unknown>("/api/v1/diagnostics"),
        ]);

      const peers =
        peersResult.status === "fulfilled"
          ? unwrapList<PeerSummary>(peersResult.value, ["peers", "items", "devices"])
          : [];
      const offers =
        offersResult.status === "fulfilled"
          ? unwrapList<FileOffer>(offersResult.value, ["offers", "items", "transfers"])
          : [];
      const displays =
        displaysResult.status === "fulfilled"
          ? unwrapList<DisplayInfo>(displaysResult.value, [
              "displays",
              "items",
              "monitors",
            ])
          : [];

      let clipboard: ClipboardSnapshot | null = null;
      if (clipboardResult.status === "fulfilled" && clipboardResult.value) {
        const raw = clipboardResult.value as ClipboardSnapshot &
          Partial<ClipboardPolicy>;
        const incomingPolicy = normalizePolicy(raw.policy ?? raw);
        clipboard = {
          latest: raw.latest ?? status.clipboard?.latest ?? null,
          policy: incomingPolicy,
        };
        setPolicy((current) => ({
          ...current,
          ...incomingPolicy,
        }));
      } else if (status.clipboard) {
        clipboard = { latest: status.clipboard.latest ?? null };
        setPolicy((current) => ({
          ...current,
          enabled: status.clipboard?.enabled ?? current.enabled,
          direction: normalizeDirection(
            status.clipboard?.direction,
            current.direction,
          ),
        }));
      }

      let security = status.security ?? null;
      if (securityResult.status === "fulfilled" && securityResult.value) {
        const raw = securityResult.value;
        const fromEndpoint: SecurityStatus | undefined =
          "security" in raw ? raw.security : (raw as SecurityStatus);
        if (fromEndpoint) {
          security = {
            ...security,
            ...fromEndpoint,
            certificateFingerprint:
              fromEndpoint.certificateFingerprint ||
              fromEndpoint.fingerprint ||
              security?.certificateFingerprint,
          };
        }
      }

      const hotkeys =
        hotkeysResult.status === "fulfilled" ? hotkeysResult.value : null;
      const audio = audioResult.status === "fulfilled" ? audioResult.value : null;
      const diagnostics =
        diagnosticsResult.status === "fulfilled"
          ? unwrapList<DiagnosticLog>(diagnosticsResult.value, ["diagnostics", "items", "logs"])
          : [];
      if (hotkeys && !hotkeysDirty.current) {
        setLocalHotkey(hotkeys.switchToLocal);
        setToggleHotkey(hotkeys.toggleRemote);
      }
      if (audio && !audioDirty.current) {
        setAudioEnabled(audio.enabled);
        setAudioVolume(audio.volume);
      }
      setSnapshot({ status, peers, clipboard, offers, displays, security, hotkeys, audio, diagnostics });
      setLastSync(new Date());
    } catch (error) {
      setConnection("offline");
      setEventsConnected(false);
      if (!quiet) {
        setNotice({
          tone: "warning",
          message: getErrorMessage(error),
        });
      }
    } finally {
      refreshing.current = false;
    }
  }, []);

  useEffect(() => {
    const initialTimer = window.setTimeout(() => void refreshSnapshot(), 0);
    const timer = window.setInterval(() => void refreshSnapshot(true), 7_000);
    return () => {
      window.clearTimeout(initialTimer);
      window.clearInterval(timer);
    };
  }, [refreshSnapshot]);

  useEffect(() => {
    let stopped = false;
    let socket: WebSocket | null = null;
    let retryTimer = 0;
    let refreshTimer = 0;

    const connect = async () => {
      if (stopped) return;
      const url = new URL("/api/v1/events", window.location.href);
      url.protocol = url.protocol === "https:" ? "wss:" : "ws:";

      try {
        url.searchParams.set("token", await getSessionToken());
        if (stopped) return;
        socket = new WebSocket(url);
        socket.addEventListener("open", () => setEventsConnected(true));
        socket.addEventListener("message", () => {
          window.clearTimeout(refreshTimer);
          refreshTimer = window.setTimeout(
            () => void refreshSnapshot(true),
            180,
          );
        });
        socket.addEventListener("close", () => {
          setEventsConnected(false);
          resetSessionToken();
          if (!stopped) retryTimer = window.setTimeout(() => void connect(), 3_000);
        });
        socket.addEventListener("error", () => socket?.close());
      } catch {
        setEventsConnected(false);
        retryTimer = window.setTimeout(() => void connect(), 3_000);
      }
    };

    void connect();
    return () => {
      stopped = true;
      window.clearTimeout(retryTimer);
      window.clearTimeout(refreshTimer);
      socket?.close();
    };
  }, [refreshSnapshot]);

  const onlinePeers = useMemo(
    () =>
      snapshot.peers.filter(
        (peer) => connection === "online" && peer.online && peer.paired !== false,
      ),
    [connection, snapshot.peers],
  );
  const discoveredUnpairedPeers = useMemo(
    () =>
      snapshot.peers.filter(
        (peer) => peer.paired === false && peer.online && Boolean(peer.address),
      ),
    [snapshot.peers],
  );

  useEffect(() => {
    if (
      pairingAddressEdited.current ||
      pairingAddress.trim() ||
      discoveredUnpairedPeers.length !== 1
    ) {
      return;
    }
    const peer = discoveredUnpairedPeers[0];
    setPairingAddress(peer.address ?? "");
    setPairingAddressSource(peer.name || "局域网设备");
  }, [discoveredUnpairedPeers, pairingAddress]);

  const runAction = useCallback(
    async (
      action: string,
      request: () => Promise<WriteResult>,
      successMessage: string,
    ) => {
      setBusy(action);
      setNotice(null);
      try {
        const result = await request();
        if (!resultAccepted(result)) {
          throw new ApiError(result.error || "Agent 未接受此操作");
        }
        setNotice({ tone: "success", message: successMessage });
        await refreshSnapshot(true);
      } catch (error) {
        setNotice({ tone: "danger", message: getErrorMessage(error) });
      } finally {
        setBusy(null);
      }
    },
    [refreshSnapshot],
  );

  const status = snapshot.status;
  const activeDeviceName =
    status?.focus?.activeDeviceName || status?.deviceName || "等待本机代理";
  const activeDeviceId = status?.focus?.activeDeviceId;
  const latestClipboard =
    snapshot.clipboard?.latest ?? status?.clipboard?.latest ?? null;
  const security = snapshot.security ?? status?.security ?? null;
  const physicalFollowEnabled = Boolean(
    status?.display?.physicalFollowEnabled,
  );
  const physicalFollowLostDdc = Boolean(
    status?.display?.message?.includes("切换后 DDC 通道不可读"),
  );
  const physicalFollowWaitingRemote = Boolean(
    status?.display?.message?.includes("等待远端 Agent"),
  );
  const writeOnlyEnabled = Boolean(status?.display?.writeOnlyEnabled);
  const writeOnlyMonitorId = status?.display?.writeOnlyMonitorId || "";
  const writeOnlyConfirmedInputs = status?.display?.writeOnlyConfirmedInputs ?? [];
  const writeOnlyAutomaticReady = Boolean(
    status?.display?.writeOnlyAutomaticReady,
  );
  const effectiveFileTarget = onlinePeers.some((peer) => peer.id === fileTarget)
    ? fileTarget
    : "";
  const fileTargetPeer = onlinePeers.find(
    (peer) => peer.id === effectiveFileTarget,
  );
  const fileUploadBusy = busy === "file-upload";
  const fileCanSubmit = Boolean(
    connection === "online" &&
      selectedFile &&
      selectedFile.size > 0 &&
      selectedFile.size <= MAX_FILE_BYTES &&
      fileTargetPeer &&
      !fileUploadBusy,
  );
  const fileStatus = fileError
    ? { tone: "danger", message: fileError }
    : fileCancelling
      ? { tone: "warning", message: "正在取消本机上的文件暂存…" }
      : fileUploadBusy
        ? {
            tone: "active",
            message: `正在向 ${fileTargetPeer?.name || fileTargetPeer?.address || "目标设备"} 提交文件提议，请保持本页打开。`,
          }
        : fileCancelled
          ? { tone: "warning", message: FILE_UPLOAD_CANCELLED_MESSAGE }
          : fileOfferCreated && !selectedFile
            ? { tone: "success", message: "文件提议已创建，正在等待接收端确认。" }
            : connection !== "online"
              ? { tone: "warning", message: "本机 Agent 离线，恢复连接后才能发送。" }
              : onlinePeers.length === 0
                ? { tone: "warning", message: "没有已配对的在线设备，暂时不能发送文件。" }
                : !fileTargetPeer
                  ? { tone: "neutral", message: "请先选择一个在线目标设备。" }
                  : selectedFile
                    ? {
                        tone: "ready",
                        message: `已准备将 ${selectedFile.name} 发送到 ${fileTargetPeer.name || fileTargetPeer.address || fileTargetPeer.id}。`,
                      }
                    : {
                        tone: "neutral",
                        message: "拖入或选择一个文件后，再创建传送请求。",
                      };
  const mappingTargets = [
    ...(status?.deviceId
      ? [
          {
            id: status.deviceId,
            label: `${status.deviceName || "本机"}（本机）`,
            defaultMappingLabel: `${status.deviceName || "本机"}输入`,
          },
        ]
      : []),
    ...snapshot.peers
      .filter((peer) => peer.paired && peer.id !== status?.deviceId)
      .map((peer) => ({
        id: peer.id,
        label: `${peer.name || peer.address || peer.id}${peer.online ? "" : "（离线）"}`,
        defaultMappingLabel: `${peer.name || "远端设备"}输入`,
      })),
  ];
  const effectiveMappingTarget = mappingTargets.some(
    (device) => device.id === mappingTarget,
  )
    ? mappingTarget
    : (mappingTargets[0]?.id ?? "");
  const monitorIds = snapshot.displays.map(displayId).filter(Boolean);
  const effectiveSelectedMonitor = monitorIds.includes(selectedMonitor)
    ? selectedMonitor
    : (monitorIds[0] ?? "");
  const selectedDisplay = snapshot.displays.find(
    (display) => displayId(display) === effectiveSelectedMonitor,
  );
  const stableFollowDisplay = snapshot.displays.find(
    (display) => display.supported && display.stable,
  );
  const stableFollowMappingCount = new Set(
    (stableFollowDisplay?.mappings ?? [])
      .map((mapping) => mapping.vcpValue)
      .filter((value): value is number => typeof value === "number"),
  ).size;
  const physicalFollowHasMappings = stableFollowMappingCount >= 2;
  const physicalFollowCanEnable = Boolean(
    !writeOnlyEnabled &&
      status?.display?.supported &&
      status.display.stable &&
      physicalFollowHasMappings,
  );
  const pairingRequests = Array.isArray(security?.pending)
    ? security.pending
    : [];
  const connectionLabel =
    connection === "online"
      ? "本机代理在线"
      : connection === "checking"
        ? "正在连接本机代理"
        : "本机代理离线";

  function chooseDiscoveredPeer(peer: PeerSummary) {
    if (!peer.address) return;
    pairingAddressEdited.current = true;
    setPairingAddress(peer.address);
    setPairingAddressSource(peer.name || "局域网设备");
    document
      .getElementById("security")
      ?.scrollIntoView({ behavior: "smooth", block: "start" });
    window.setTimeout(() => pairingAddressInput.current?.focus(), 350);
  }

  async function handlePairing(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    await runAction(
      "pair",
      () =>
        apiRequest<WriteResult>(
          "/api/v1/pairings",
          jsonRequest("POST", {
            address: pairingAddress.trim(),
            code: pairingCode.trim(),
          }),
        ),
      "已提交配对请求，请在另一台设备确认。",
    );
    setPairingCode("");
  }

  async function refreshAndCopyPairingCode() {
    setBusy("pairing-code-refresh");
    setNotice(null);
    try {
      const issued = await apiRequest<LocalPairingCode>(
        "/api/v1/security/pairing-code/refresh",
        jsonRequest("POST"),
      );
      setSnapshot((current) => ({
        ...current,
        security: {
          ...(current.security ?? {}),
          pairingCode: issued.pairingCode,
          expiresAt: issued.expiresAt,
        },
      }));
      try {
        await navigator.clipboard.writeText(issued.pairingCode);
        setNotice({
          tone: "success",
          message: "新配对码已生成并复制；旧码已立即作废。",
        });
      } catch {
        setNotice({
          tone: "warning",
          message: "新配对码已生成，但浏览器未允许复制；请手动复制页面中的新码。",
        });
      }
    } catch (error) {
      setNotice({ tone: "danger", message: getErrorMessage(error) });
    } finally {
      setBusy(null);
    }
  }

  async function handlePolicySave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const nextPolicy: ClipboardPolicy = policy.enabled
      ? {
          ...policy,
          direction:
            policy.direction === "disabled" ? "bidirectional" : policy.direction,
        }
      : { ...policy, direction: "disabled" };
    setPolicy(nextPolicy);
    await runAction(
      "clipboard-policy",
      () =>
        apiRequest<WriteResult>(
          "/api/v1/clipboard/policy",
          jsonRequest("PATCH", {
            enabled: nextPolicy.enabled,
            direction: directionToWire(nextPolicy.direction),
            textEnabled: nextPolicy.allowText,
            imageEnabled: nextPolicy.allowImages,
            allowText: nextPolicy.allowText,
            allowImages: nextPolicy.allowImages,
            allowFiles: nextPolicy.allowFiles,
            maxTextBytes: nextPolicy.maxTextBytes,
            maxImageBytes: nextPolicy.maxImageBytes,
            maxFileBytes: nextPolicy.maxFileBytes,
          }),
        ),
      nextPolicy.enabled ? "剪贴板策略已保存。" : "剪贴板同步已暂停。",
    );
  }

  async function handleHotkeySave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy("hotkeys");
    setNotice(null);
    try {
      const result = await apiRequest<HotkeySettings>(
        "/api/v1/hotkeys",
        jsonRequest("PUT", {
          switchToLocal: localHotkey,
          toggleRemote: toggleHotkey,
        }),
      );
      setLocalHotkey(result.switchToLocal);
      setToggleHotkey(result.toggleRemote);
      hotkeysDirty.current = false;
      setSnapshot((current) => ({ ...current, hotkeys: result }));
      setNotice({
        tone: "success",
        message: "快捷键已保存并立即生效，无需重启 Agent。",
      });
    } catch (error) {
      setNotice({ tone: "danger", message: getErrorMessage(error) });
    } finally {
      setBusy(null);
    }
  }

  async function handleAudioSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setBusy("audio-settings");
    setNotice(null);
    try {
      const result = await apiRequest<AudioSettings>(
        "/api/v1/audio",
        jsonRequest("PUT", { enabled: audioEnabled, volume: audioVolume }),
      );
      audioDirty.current = false;
      setAudioEnabled(result.enabled);
      setAudioVolume(result.volume);
      setSnapshot((current) => ({ ...current, audio: result }));
      setNotice({
        tone: "success",
        message: result.enabled
          ? "音频跟随已启用；切到远端后将从本机默认耳机播放。"
          : "音频跟随已关闭。",
      });
    } catch (error) {
      setNotice({ tone: "danger", message: getErrorMessage(error) });
    } finally {
      setBusy(null);
    }
  }

  function chooseSingleFile(files: File[]) {
    if (files.length === 0) return;
    setUploadProgress(null);
    setFileOfferCreated(false);
    setFileCancelled(false);
    if (files.length !== 1) {
      setSelectedFile(null);
      setFileError("一次只能发送 1 个文件，请只拖入一个文件。");
      return;
    }

    const file = files[0];
    if (file.size <= 0) {
      setSelectedFile(null);
      setFileError("不能发送空文件，请选择有内容的文件。");
      return;
    }
    if (file.size > MAX_FILE_BYTES) {
      setSelectedFile(null);
      setFileError(
        `“${file.name}”大小为 ${formatBytes(file.size)}，超过单文件 2 GB 上限。`,
      );
      return;
    }

    setSelectedFile(file);
    setFileError(null);
  }

  function handleFileChange(event: ChangeEvent<HTMLInputElement>) {
    chooseSingleFile(Array.from(event.target.files ?? []));
    event.target.value = "";
  }

  function openFilePicker() {
    if (fileUploadBusy) return;
    fileInput.current?.click();
  }

  function handleFileDropKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key !== "Enter" && event.key !== " ") return;
    event.preventDefault();
    openFilePicker();
  }

  function handleFileDragEnter(event: DragEvent<HTMLDivElement>) {
    if (!Array.from(event.dataTransfer.types).includes("Files")) return;
    event.preventDefault();
    event.stopPropagation();
    fileDragDepth.current += 1;
    setIsFileDragging(true);
  }

  function handleFileDragOver(event: DragEvent<HTMLDivElement>) {
    if (!Array.from(event.dataTransfer.types).includes("Files")) return;
    event.preventDefault();
    event.stopPropagation();
    event.dataTransfer.dropEffect = fileUploadBusy ? "none" : "copy";
  }

  function handleFileDragLeave(event: DragEvent<HTMLDivElement>) {
    event.preventDefault();
    event.stopPropagation();
    fileDragDepth.current = Math.max(0, fileDragDepth.current - 1);
    if (fileDragDepth.current === 0) setIsFileDragging(false);
  }

  function handleFileDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault();
    event.stopPropagation();
    fileDragDepth.current = 0;
    setIsFileDragging(false);
    if (fileUploadBusy) return;
    chooseSingleFile(Array.from(event.dataTransfer.files));
  }

  function cancelLocalFileUpload() {
    if (!fileUploadBusy || fileCancelling) return;
    const request = fileUploadRequest.current;
    if (request?.readyState === XMLHttpRequest.DONE) return;
    fileUploadCancelRequested.current = true;
    setFileCancelling(true);
    if (request && request.readyState !== XMLHttpRequest.DONE) request.abort();
  }

  async function handleFileUpload(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (connection !== "online") {
      setFileError("本机 Agent 当前离线，无法发送文件。");
      return;
    }
    if (!selectedFile) {
      setFileError("请先拖入或选择一个文件。");
      return;
    }
    if (selectedFile.size <= 0 || selectedFile.size > MAX_FILE_BYTES) {
      chooseSingleFile([selectedFile]);
      return;
    }
    if (!effectiveFileTarget || !fileTargetPeer) {
      setFileError("请选择一个已配对且在线的目标设备。");
      return;
    }

    setBusy("file-upload");
    setNotice(null);
    setFileError(null);
    setFileOfferCreated(false);
    setFileCancelled(false);
    setFileCancelling(false);
    fileUploadCancelRequested.current = false;
    setUploadProgress(0);
    try {
      const result = await uploadFile(
        selectedFile,
        effectiveFileTarget,
        setUploadProgress,
        (request) => {
          fileUploadRequest.current = request;
        },
        () => fileUploadCancelRequested.current,
      );
      if (!resultAccepted(result)) {
        throw new ApiError(result.error || "Agent 未接受文件发送请求");
      }
      setNotice({ tone: "success", message: "文件发送请求已提交。" });
      setSelectedFile(null);
      setFileOfferCreated(true);
      fileUploadRequest.current = null;
      setFileCancelling(false);
      setUploadProgress(null);
      setBusy(null);
      await refreshSnapshot(true);
    } catch (error) {
      const message = getErrorMessage(error);
      if (message === FILE_UPLOAD_CANCELLED_MESSAGE) {
        setFileError(null);
        setFileCancelled(true);
        setNotice({ tone: "warning", message });
      } else {
        setFileError(message);
        setNotice({ tone: "danger", message });
      }
    } finally {
      fileUploadRequest.current = null;
      fileUploadCancelRequested.current = false;
      setFileCancelling(false);
      setUploadProgress(null);
      setBusy(null);
    }
  }

  async function handleMappingSave(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const vcpValue = parseVcpValue(mappingValue);
    if (
      !effectiveSelectedMonitor ||
      !effectiveMappingTarget ||
      vcpValue === null
    ) {
      setNotice({ tone: "warning", message: "请填写有效的显示器输入值。" });
      return;
    }

    await runAction(
      "display-map",
      () =>
        saveDisplayMapping(
          {
            monitorId: effectiveSelectedMonitor,
            deviceId: effectiveMappingTarget,
            label:
              mappingLabel.trim() ||
              mappingTargets.find((device) => device.id === effectiveMappingTarget)
                ?.defaultMappingLabel ||
              "设备输入",
            vcpValue,
          },
          !writeOnlyEnabled,
        ),
      writeOnlyEnabled
        ? "只写输入映射已保存；未发送命令，也没有读取验证。"
        : "显示器输入映射已保存。",
    );
  }

  async function handleCompatibilityToggle() {
    const monitorId = writeOnlyEnabled
      ? writeOnlyMonitorId
      : effectiveSelectedMonitor;
    if (!monitorId) {
      setNotice({ tone: "warning", message: "请先探测并选择要绑定的显示器。" });
      return;
    }
    if (!writeOnlyEnabled) {
      if (!selectedDisplay?.writeOnlyEligible) {
        setNotice({
          tone: "warning",
          message: "该显示器尚未取得稳定身份，不能安全启用只写兼容模式。",
        });
        return;
      }
      if (!compatibilityRiskAccepted) {
        setNotice({ tone: "warning", message: "请先勾选黑屏恢复确认。" });
        return;
      }
      const confirmed = window.confirm(
        "开启只写 DDC 兼容模式后，DeskMesh 无法读取显示器当前输入源，也无法确认切源是否真的成功。\n\n测试可能让屏幕暂时黑屏。请确认两台电脑都在输出信号，并将显示器实体摇杆留在手边。\n\n确定继续吗？",
      );
      if (!confirmed) return;
    }

    await runAction(
      "display-compatibility",
      () =>
        apiRequest<WriteResult>(
          "/api/v1/displays/compatibility",
          jsonRequest("PUT", {
            enabled: !writeOnlyEnabled,
            monitorId,
            acknowledgeRisk: !writeOnlyEnabled && compatibilityRiskAccepted,
          }),
        ),
      writeOnlyEnabled
        ? "只写 DDC 兼容模式已关闭。"
        : "只写 DDC 兼容模式已开启；尚未发送切源命令。",
    );
  }

  async function handleCompatibilityTest(vcpValue: 0x0f | 0x11) {
    const inputLabel = vcpValue === 0x0f ? "DP（0x0F）" : "HDMI1（0x11）";
    const monitorId = writeOnlyMonitorId || effectiveSelectedMonitor;
    if (!monitorId) return;
    const confirmed = window.confirm(
      `即将向显示器发送一次 ${inputLabel} 切源命令。\n\n软件无法读回验证，屏幕可能黑屏。请确保该接口已有电脑输出信号，并准备使用显示器实体摇杆恢复。\n\n确定发送吗？`,
    );
    if (!confirmed) return;

    setBusy("display-compatibility-test");
    setNotice(null);
    try {
      const result = await apiRequest<
        WriteResult & { commandIssued?: boolean; confirmationId?: string; message?: string }
      >(
        "/api/v1/displays/compatibility/test",
        jsonRequest("POST", {
          monitorId,
          vcpValue,
          acknowledgeRisk: true,
        }),
      );
      if (!resultAccepted(result) || !result.commandIssued || !result.confirmationId) {
        throw new ApiError(result.error || "显示器未接受只写切源命令");
      }

      const worked = window.confirm(
        `${inputLabel} 命令已经发送，但软件无法读取结果。\n\n屏幕是否确实切换到了 ${inputLabel.split("（")[0]}？\n\n选择“确定”会把此输入值标记为已确认；选择“取消”会将它保留为未确认，自动切换不会使用它。`,
      );
      await apiRequest<WriteResult>(
        "/api/v1/displays/compatibility/test/confirm",
        jsonRequest("POST", {
          confirmationId: result.confirmationId,
          monitorId,
          vcpValue,
          worked,
        }),
      );
      setNotice({
        tone: worked ? "success" : "warning",
        message: worked
          ? `${inputLabel} 已由你目视确认。`
          : `${inputLabel} 未通过目视确认，不会用于自动切换。`,
      });
      await refreshSnapshot(true);
    } catch (error) {
      setNotice({ tone: "danger", message: getErrorMessage(error) });
    } finally {
      setBusy(null);
    }
  }

  async function handleForgetPeer(peer: PeerSummary) {
    const peerName = peer.name || peer.address || "此设备";
    const confirmed = window.confirm(
      `确定忘记“${peerName}”吗？\n\n这会立即撤销该设备的信任与当前键鼠会话，控制权会释放回本机。再次连接必须重新配对。\n\n此操作无法在网页中撤销。`,
    );
    if (!confirmed) return;

    await runAction(
      `forget-${peer.id}`,
      async () => {
        const result = await apiRequest<{ removed?: boolean }>(
          `/api/v1/peers/${encodeURIComponent(peer.id)}`,
          { method: "DELETE" },
        );
        return result.removed
          ? { accepted: true }
          : { accepted: false, error: "Agent 未找到该设备，可能已被忘记。" };
      },
      `已忘记 ${peerName}，信任关系和键鼠会话均已撤销。`,
    );
  }

  return (
    <div className="app-shell">
      <a className="skip-link" href="#main-content">
        跳到主要内容
      </a>

      <aside className="sidebar" aria-label="主导航">
        <div className="brand-block">
          <span className="brand-mark" aria-hidden="true">
            {/* The same Vite-served asset is shared by the web console and tray package. */}
            <img src="/lanswitch-icon.png" alt="" width="42" height="42" />
          </span>
          <div>
            <strong>DeskMesh</strong>
            <span>本地控制台</span>
          </div>
        </div>

        <nav className="side-nav" aria-label="控制台分区">
          <a href="#overview">总览</a>
          <a href="#devices">设备与切换</a>
          <a href="#remote-desktop">远程桌面</a>
          <a href="#clipboard">剪贴板</a>
          <a href="#files">文件传送</a>
          <a href="#display">显示器校准</a>
          <a href="#security">安全与配对</a>
          <a href="#diagnostics">诊断日志</a>
        </nav>

        <div className="sidebar-foot">
          <span className={`status-dot ${connection}`} aria-hidden="true" />
          <span>{connectionLabel}</span>
          {status?.version ? <small>Agent {status.version}</small> : null}
        </div>
      </aside>

      <main id="main-content" className="main-content">
        <header className="topbar">
          <div>
            <p className="eyebrow">局域网协同 / 本机</p>
            <h1>控制中心</h1>
          </div>
          <div className="topbar-actions">
            <div
              className={`connection-chip ${connection}`}
              role="status"
              aria-live="polite"
            >
              <span className={`status-dot ${connection}`} aria-hidden="true" />
              {connectionLabel}
            </div>
            <button
              className="icon-button"
              type="button"
              onClick={() => void refreshSnapshot()}
              disabled={busy === "refresh"}
              aria-label={busy === "refresh" ? "正在刷新所有状态" : "刷新所有状态"}
              aria-busy={busy === "refresh"}
              title="刷新状态"
            >
              ↻
            </button>
          </div>
        </header>

        {connection === "offline" ? (
          <section className="offline-banner" aria-labelledby="offline-title">
            <div className="offline-symbol" aria-hidden="true">
              !
            </div>
            <div>
              <h2 id="offline-title">等待 DeskMesh Agent</h2>
              <p>
                控制台已经打开，但无法连接本机服务。请启动 Agent，确认它监听当前地址后再刷新；离线期间不会发送切换、剪贴板或文件操作。
              </p>
            </div>
            <button type="button" onClick={() => void refreshSnapshot()}>
              重新连接
            </button>
          </section>
        ) : null}

        {notice ? (
          <div
            className={`notice ${notice.tone}`}
            role={notice.tone === "danger" ? "alert" : "status"}
            aria-live={notice.tone === "danger" ? "assertive" : "polite"}
            aria-atomic="true"
          >
            <span>{notice.message}</span>
            <button type="button" onClick={() => setNotice(null)} aria-label="关闭提示">
              ×
            </button>
          </div>
        ) : null}

        <section id="overview" className="overview-grid" aria-labelledby="overview-title">
          <article className="focus-card">
            <div className="section-heading inverse">
              <div>
                <p className="eyebrow">当前控制目标</p>
                <h2 id="overview-title">{activeDeviceName}</h2>
              </div>
              <span className={`focus-state ${status?.focus?.isRemote ? "remote" : "local"}`}>
                {status?.focus?.isRemote ? "远端控制" : "本机控制"}
              </span>
            </div>

            <p className="focus-copy">
              {connection === "online"
                ? focusDescription(status)
                : "Agent 上线后，这里会显示真实的控制权状态。"}
            </p>

            <div className="focus-actions">
              <button
                className="primary-button light"
                type="button"
                disabled={
                  connection !== "online" ||
                  !status?.focus?.isRemote ||
                  busy === "focus-release"
                }
                onClick={() =>
                  void runAction(
                    "focus-release",
                    () =>
                      apiRequest<WriteResult>(
                        "/api/v1/focus/release",
                        jsonRequest("POST"),
                      ),
                    "控制权已安全回到本机。",
                  )
                }
              >
                {busy === "focus-release" ? "正在释放…" : "释放到本机"}
              </button>
              <span>紧急热键：{security?.emergencyHotkey || "等待 Agent"}</span>
            </div>
          </article>

          <div className="metric-stack">
            <Metric
              label="发现的远端"
              value={connection === "online" ? String(snapshot.peers.length) : "—"}
              hint={
                connection === "online"
                  ? `${onlinePeers.length} 台已配对在线`
                  : "无在线数据"
              }
            />
            <Metric
              label="事件通道"
              value={eventsConnected ? "实时" : "轮询"}
              hint={lastSync ? `更新于 ${formatClock(lastSync)}` : "尚未同步"}
            />
            <Metric
              label="实体切源跟随"
              value={
                connection === "online"
                  ? physicalFollowEnabled
                    ? physicalFollowWaitingRemote
                      ? "等待远端确认"
                      : "已启用"
                    : physicalFollowLostDdc
                      ? "已自动停用"
                    : "未启用"
                  : "—"
              }
              hint={
                writeOnlyEnabled
                  ? "只写模式无法使用实体跟随"
                  : physicalFollowWaitingRemote
                    ? "原输入端 DDC/CI 已失联，正在由另一台 Agent 检测新画面"
                  : physicalFollowLostDdc
                    ? "切源后原输入端失去 DDC/CI"
                  : status?.display?.supported
                    ? status.display.stable === false
                    ? "DDC/CI 状态不稳定"
                      : physicalFollowHasMappings
                        ? "可在显示器校准中启用"
                        : "需先保存本机与远端两个输入映射"
                    : "等待能力检测"
              }
            />
          </div>
        </section>

        <section id="devices" className="content-section" aria-labelledby="devices-title">
          <SectionHeader
            id="devices-title"
            eyebrow="设备与切换"
            title="把输入交给正确的设备"
            description="切换请求由本机 Agent 验证并执行；离线设备不会被当作可用目标。"
          />

          <div className="device-grid">
            <article className="device-card local-device">
              <div className="device-card-head">
                <span className="device-kind">本机</span>
                <span className={`availability ${connection === "online" ? "online" : "offline"}`}>
                  {connection === "online" ? "在线" : "离线"}
                </span>
              </div>
              <h3>{status?.deviceName || "等待本机代理"}</h3>
              <p>{networkLabel(status?.network)}</p>
              <dl className="compact-list">
                <div>
                  <dt>角色</dt>
                  <dd>{roleLabel(status?.role)}</dd>
                </div>
                <div>
                  <dt>运行时间</dt>
                  <dd>{formatUptime(status?.uptimeSeconds)}</dd>
                </div>
              </dl>
              <button
                type="button"
                className="secondary-button"
                disabled={
                  connection !== "online" ||
                  !status?.focus?.isRemote ||
                  busy === "focus-release"
                }
                onClick={() =>
                  void runAction(
                    "focus-release",
                    () =>
                      apiRequest<WriteResult>(
                        "/api/v1/focus/release",
                        jsonRequest("POST"),
                      ),
                    "控制权已回到本机。",
                  )
                }
              >
                {activeDeviceId === status?.deviceId && !status?.focus?.isRemote
                  ? "当前设备"
                  : "切回本机"}
              </button>
            </article>

            {snapshot.peers.length > 0 ? (
              snapshot.peers.map((peer) => {
                const peerOnline = connection === "online" && Boolean(peer.online);
                const isActive = activeDeviceId === peer.id && status?.focus?.isRemote;
                const localDisplayMapping = selectedDisplay?.mappings?.find(
                  (mapping) => mapping.deviceId === status?.deviceId,
                );
                const peerDisplayMapping = selectedDisplay?.mappings?.find(
                  (mapping) => mapping.deviceId === peer.id,
                );
                const screenSwitchReady = Boolean(
                  localDisplayMapping &&
                    peerDisplayMapping &&
                    localDisplayMapping.vcpValue !== peerDisplayMapping.vcpValue,
                );
                return (
                  <article className={`device-card ${isActive ? "active" : ""}`} key={peer.id}>
                    <div className="device-card-head">
                      <span className="device-kind">远端</span>
                      <span className={`availability ${peerOnline ? "online" : "offline"}`}>
                        {peerOnline ? "在线" : "离线"}
                      </span>
                    </div>
                    <h3>{peer.name || "未命名设备"}</h3>
                    <p>{peer.address || "地址由 Agent 管理"}</p>
                    <dl className="compact-list">
                      <div>
                        <dt>延迟</dt>
                        <dd>{peerOnline && peer.latencyMs != null ? `${peer.latencyMs} ms` : "—"}</dd>
                      </div>
                      <div>
                        <dt>配对</dt>
                        <dd>{peer.paired ? "已信任" : "未配对"}</dd>
                      </div>
                    </dl>
                    <div className="capability-row" aria-label="设备能力">
                      {(peer.capabilities ?? []).slice(0, 4).map((capability) => (
                        <span key={capability}>{capabilityLabel(capability)}</span>
                      ))}
                      {!peer.capabilities?.length ? <span>等待能力信息</span> : null}
                    </div>
                    <div className="device-actions">
                      <button
                        type="button"
                        className={isActive ? "secondary-button" : "primary-button"}
                        disabled={
                          !peerOnline ||
                          busy === `switch-${peer.id}` ||
                          isActive ||
                          (!peer.paired && !peer.address)
                        }
                        onClick={() =>
                          peer.paired
                            ? void runAction(
                                `switch-${peer.id}`,
                                () =>
                                  apiRequest<WriteResult>(
                                    "/api/v1/focus/switch",
                                    jsonRequest("POST", { targetDeviceId: peer.id }),
                                    15_000,
                                  ),
                                screenSwitchReady
                                  ? `正在把画面与控制权切换到 ${peer.name || "远端设备"}。`
                                  : "键鼠控制已切换；显示器尚未完成双向校准，请用实体键切换画面。",
                              )
                            : chooseDiscoveredPeer(peer)
                        }
                      >
                        {isActive
                          ? "当前控制目标"
                          : busy === `switch-${peer.id}`
                            ? "正在切换…"
                            : peer.paired
                              ? screenSwitchReady
                                ? "切换画面与键鼠"
                                : "仅切键鼠（画面未校准）"
                              : "使用此地址配对"}
                      </button>
                      {peer.paired ? (
                        <button
                          type="button"
                          className="danger-button"
                          disabled={
                            connection !== "online" ||
                            busy === `forget-${peer.id}`
                          }
                          onClick={() => void handleForgetPeer(peer)}
                        >
                          {busy === `forget-${peer.id}` ? "撤销中…" : "忘记设备"}
                        </button>
                      ) : null}
                    </div>
                  </article>
                );
              })
            ) : (
              <EmptyCard
                title={connection === "online" ? "尚未发现远端设备" : "设备列表不可用"}
                description={
                  connection === "online"
                    ? "确认另一台电脑已启动 Agent。新版会同时使用组播和同网段广播；仍未发现时可在下方手动填写地址。"
                    : "本机 Agent 上线后会自动加载真实设备状态。"
                }
              />
            )}
          </div>

          <form className="audio-follow-panel" onSubmit={handleAudioSave}>
            <div className="audio-follow-copy">
              <span className="eyebrow">局域网系统音频</span>
              <h3>远端声音从本机耳机播放</h3>
              <p>
                切到远端后自动抓取远端正在播放的系统声音，通过加密连接送到本机默认输出；不会传输麦克风。
              </p>
              <span className={`audio-phase phase-${snapshot.audio?.phase || "idle"}`}>
                {audioPhaseLabel(snapshot.audio)}
              </span>
            </div>
            <div className="audio-follow-controls">
              <div className="toggle-row audio-toggle">
                <span>
                  <strong>音频随控制目标</strong>
                  <small>{audioEnabled ? "已启用" : "已关闭"}</small>
                </span>
                <input
                  id="audio-follow-enabled"
                  type="checkbox"
                  aria-label="音频随控制目标"
                  checked={audioEnabled}
                  disabled={connection !== "online" || busy === "audio-settings"}
                  onChange={(event) => {
                    audioDirty.current = true;
                    setAudioEnabled(event.target.checked);
                  }}
                />
              </div>
              <label className="audio-volume-control" htmlFor="audio-follow-volume">
                <span>本机播放音量 <strong>{audioVolume}%</strong></span>
                <input
                  id="audio-follow-volume"
                  type="range"
                  min="0"
                  max="100"
                  step="5"
                  value={audioVolume}
                  disabled={!audioEnabled || connection !== "online" || busy === "audio-settings"}
                  onChange={(event) => {
                    audioDirty.current = true;
                    setAudioVolume(Number(event.target.value));
                  }}
                />
              </label>
              <button
                type="submit"
                className="secondary-button"
                disabled={connection !== "online" || busy === "audio-settings"}
              >
                {busy === "audio-settings" ? "保存中…" : "保存音频设置"}
              </button>
            </div>
          </form>
        </section>

        <RemoteDesktopPanel
          peers={onlinePeers}
          connection={connection}
          onNotice={setNotice}
        />

        <div className="split-sections">
          <section id="clipboard" className="panel-section" aria-labelledby="clipboard-title">
            <SectionHeader
              id="clipboard-title"
              eyebrow="剪贴板"
              title="同步策略"
              description="默认只同步文本；敏感内容可随时暂停。"
              compact
            />

            <div className="latest-clipboard">
              <div className="latest-label">
                <span>最近内容</span>
                <span>{latestClipboard ? formatRelativeTime(latestClipboard.updatedAt) : "暂无"}</span>
              </div>
              {latestClipboard ? (
                <>
                  <p>{latestClipboard.textPreview || `已接收 ${kindLabel(latestClipboard.kind)}内容`}</p>
                  <small>
                    {latestClipboard.sourceDeviceName || "未知来源"} · {formatBytes(latestClipboard.sizeBytes)}
                  </small>
                </>
              ) : (
                <p className="muted-copy">Agent 尚未报告剪贴板记录。</p>
              )}
            </div>

            <form className="settings-form" onSubmit={handlePolicySave}>
              <div className="toggle-row">
                <span>
                  <strong id="clipboard-enabled-label">启用剪贴板同步</strong>
                  <small>关闭后不读取也不发送剪贴板</small>
                </span>
                <input
                  id="clipboard-enabled"
                  type="checkbox"
                  aria-labelledby="clipboard-enabled-label"
                  checked={policy.enabled}
                  onChange={(event) =>
                    setPolicy((current) => ({
                      ...current,
                      enabled: event.target.checked,
                      direction: event.target.checked
                        ? current.direction === "disabled"
                          ? "bidirectional"
                          : current.direction
                        : "disabled",
                    }))
                  }
                />
              </div>

              <label className="field-label">
                同步方向
                <select
                  value={policy.enabled ? policy.direction : "disabled"}
                  disabled={!policy.enabled}
                  onChange={(event) =>
                    setPolicy((current) => ({
                      ...current,
                      direction: event.target.value as ClipboardPolicy["direction"],
                    }))
                  }
                >
                  <option value="bidirectional">双向同步</option>
                  <option value="sendOnly">仅发送</option>
                  <option value="receiveOnly">仅接收</option>
                  <option value="disabled">禁用</option>
                </select>
              </label>

              <fieldset className="check-group" disabled={!policy.enabled}>
                <legend>允许的内容</legend>
                <label>
                  <input
                    type="checkbox"
                    checked={policy.allowText}
                    onChange={(event) =>
                      setPolicy((current) => ({ ...current, allowText: event.target.checked }))
                    }
                  />
                  文本
                </label>
                <label>
                  <input
                    type="checkbox"
                    checked={policy.allowImages}
                    onChange={(event) =>
                      setPolicy((current) => ({ ...current, allowImages: event.target.checked }))
                    }
                  />
                  图片
                </label>
              </fieldset>

              <button
                className="primary-button full-width"
                type="submit"
                disabled={connection !== "online" || busy === "clipboard-policy"}
              >
                {busy === "clipboard-policy" ? "正在保存…" : "保存同步策略"}
              </button>
            </form>
          </section>

          <section id="files" className="panel-section" aria-labelledby="files-title">
            <SectionHeader
              id="files-title"
              eyebrow="文件传送"
              title="发送到已配对设备"
              description="文件先创建传送请求，接收端确认后才开始处理。"
              compact
            />

            <form className="upload-form" onSubmit={handleFileUpload}>
              <label className="field-label">
                目标设备（必选）
                <select
                  value={effectiveFileTarget}
                  onChange={(event) => {
                    setFileTarget(event.target.value);
                    setFileError(null);
                    setFileOfferCreated(false);
                    setFileCancelled(false);
                    if (!selectedFile) setUploadProgress(null);
                  }}
                  disabled={onlinePeers.length === 0 || fileUploadBusy}
                >
                  <option value="">
                    {onlinePeers.length === 0 ? "没有在线设备" : "请选择在线设备"}
                  </option>
                  {onlinePeers.map((peer) => (
                    <option key={peer.id} value={peer.id}>
                      {peer.name || peer.address || peer.id}
                      {peer.address && peer.name ? ` · ${peer.address}` : ""}
                    </option>
                  ))}
                </select>
              </label>

              <div
                className={`file-target-card${fileTargetPeer ? " is-ready" : ""}`}
              >
                <span>发送目标</span>
                <strong>
                  {fileTargetPeer
                    ? fileTargetPeer.name || fileTargetPeer.address || fileTargetPeer.id
                    : "尚未选择"}
                </strong>
                <small>
                  {fileTargetPeer?.address
                    ? `${fileTargetPeer.address} · 已配对且在线`
                    : "只会列出已配对且当前在线的设备"}
                </small>
              </div>

              <div
                className={`file-drop${isFileDragging ? " is-dragging" : ""}${fileUploadBusy ? " is-disabled" : ""}`}
                role="button"
                tabIndex={fileUploadBusy ? -1 : 0}
                aria-disabled={fileUploadBusy}
                aria-label={
                  selectedFile
                    ? `已选择 ${selectedFile.name}，点击或按回车键重新选择`
                    : "拖入文件，或点击、按回车键选择文件"
                }
                aria-describedby="file-drop-limit"
                onClick={openFilePicker}
                onKeyDown={handleFileDropKeyDown}
                onDragEnter={handleFileDragEnter}
                onDragOver={handleFileDragOver}
                onDragLeave={handleFileDragLeave}
                onDrop={handleFileDrop}
              >
                <input
                  ref={fileInput}
                  type="file"
                  tabIndex={-1}
                  aria-hidden="true"
                  onChange={handleFileChange}
                />
                <span className="file-symbol" aria-hidden="true">
                  {isFileDragging ? "↓" : selectedFile ? "✓" : "＋"}
                </span>
                <strong>
                  {isFileDragging
                    ? "松开鼠标即可添加"
                    : selectedFile
                      ? selectedFile.name
                      : "拖动文件到这里"}
                </strong>
                <small>
                  {selectedFile
                    ? `${formatBytes(selectedFile.size)} · 点击或按回车键可重新选择`
                    : "也可以点击此区域，或按回车键选择文件"}
                </small>
                <span className="file-limit" id="file-drop-limit">
                  每次 1 个文件，最大 2 GB；文件只发送到所选局域网设备
                </span>
              </div>

              <div
                className={`file-upload-status is-${fileStatus.tone}`}
                role={fileStatus.tone === "danger" ? "alert" : "status"}
                aria-live="polite"
              >
                <span aria-hidden="true" />
                <p>{fileStatus.message}</p>
              </div>

              {uploadProgress != null ? (
                <div className="progress-block" aria-live="polite">
                  <div>
                    <span>正在提交到本机 Agent</span>
                    <strong>{uploadProgress}%</strong>
                  </div>
                  <progress
                    value={uploadProgress}
                    max="100"
                    aria-label="文件提交到本机 Agent 的进度"
                    aria-valuetext={`${uploadProgress}%`}
                  />
                </div>
              ) : null}

              {fileUploadBusy ? (
                <button
                  className="danger-button small-button full-width"
                  type="button"
                  disabled={fileCancelling}
                  onClick={cancelLocalFileUpload}
                >
                  {fileCancelling ? "正在取消本地暂存…" : "取消本地暂存"}
                </button>
              ) : null}

              <button
                className="primary-button full-width"
                type="submit"
                disabled={!fileCanSubmit}
              >
                {fileUploadBusy
                  ? "正在提交文件…"
                  : fileTargetPeer
                    ? `发送到 ${fileTargetPeer.name || fileTargetPeer.address || "目标设备"}`
                    : "选择目标设备后发送"}
              </button>
            </form>

            <div className="offer-list" aria-label="文件传送请求">
              <h3>最近请求</h3>
              {snapshot.offers.length > 0 ? (
                snapshot.offers.slice(0, 4).map((offer) => {
                  const id = offerId(offer);
                  const incoming = offer.direction === "incoming";
                  const offerStatus = (offer.state ?? offer.status)?.toLowerCase();
                  const decisionFailed = offerStatus === "decision-failed";
                  const pending = [
                    "pending",
                    "offered",
                    "waiting",
                    "awaiting-confirmation",
                    undefined,
                  ].includes(offerStatus);
                  return (
                    <article className="offer-row" key={id || `${offer.fileName}-${offer.createdAt}`}>
                      <div>
                        <strong>{offer.fileName || "未命名文件"}</strong>
                        <small>
                          {incoming
                            ? `来自 ${offer.sourceDeviceName || "远端设备"}`
                            : `发送到 ${offer.targetDeviceName || "远端设备"}`} · {formatBytes(offer.length ?? offer.sizeBytes)}
                        </small>
                        {offer.error ? (
                          <span className="offer-error">{offer.error}</span>
                        ) : null}
                      </div>
                      {incoming && (pending || decisionFailed) && id ? (
                        <div className="inline-actions">
                          <button
                            type="button"
                            className="text-button"
                            disabled={busy === `offer-${id}`}
                            onClick={() =>
                              void runAction(
                                `offer-${id}`,
                                () =>
                                  apiRequest<WriteResult>(
                                    `/api/v1/files/offers/${encodeURIComponent(id)}/decision`,
                                    jsonRequest("POST", { accept: false }),
                                  ),
                                decisionFailed
                                  ? "已重新通知对方拒绝决定。"
                                  : "已拒绝文件请求。",
                              )
                            }
                          >
                            {decisionFailed ? "重新通知拒绝" : "拒绝"}
                          </button>
                          <button
                            type="button"
                            className="small-button"
                            disabled={busy === `offer-${id}`}
                            onClick={() =>
                              void runAction(
                                `offer-${id}`,
                                () =>
                                  apiRequest<WriteResult>(
                                    `/api/v1/files/offers/${encodeURIComponent(id)}/decision`,
                                    jsonRequest("POST", { accept: true }),
                                    600_000,
                                  ),
                                decisionFailed
                                  ? "已重新通知对方接受决定。"
                                  : "已选择保存位置并接受文件请求。",
                              )
                            }
                          >
                            {busy === `offer-${id}`
                              ? decisionFailed
                                ? "重新通知中…"
                                : "等待选择…"
                              : decisionFailed
                                ? "重新通知接受"
                                : "接收并选择位置"}
                          </button>
                        </div>
                      ) : (
                        <span className="offer-state">{offerStateLabel(offer.state ?? offer.status)}</span>
                      )}
                    </article>
                  );
                })
              ) : (
                <p className="empty-line">暂无真实传送记录。</p>
              )}
            </div>
          </section>
        </div>

        <section id="display" className="content-section" aria-labelledby="display-title">
          <SectionHeader
            id="display-title"
            eyebrow="显示器校准"
            title="让画面与控制权一起切换"
            description="先探测 DDC/CI 能力；无法读取 VCP 0x60 时，可显式启用只写兼容模式测试 HDMI1 与 DP。"
          />

          <div className="display-grid">
            <article className="monitor-panel">
              <div className="monitor-frame" aria-hidden="true">
                <div className="monitor-screen">
                  <span>{selectedDisplay ? displayName(selectedDisplay) : "未发现显示器"}</span>
                  <small>
                    {writeOnlyEnabled
                      ? "当前输入未知（只写模式不读取）"
                      : selectedDisplay?.currentLabel ||
                      (selectedDisplay?.currentInput != null
                        ? `输入 ${String(selectedDisplay.currentInput)}`
                        : "等待 DDC/CI")}
                  </small>
                </div>
                <div className="monitor-stand" />
              </div>

              <div className="monitor-status-row">
                <span>
                  <i className={`status-dot ${selectedDisplay?.supported ? "online" : "offline"}`} />
                  {selectedDisplay?.supported
                    ? "DDC/CI 可读取"
                    : selectedDisplay?.writeOnlyEligible
                      ? "读取不可用，可尝试只写模式"
                      : "能力未知或不可安全测试"}
                </span>
                <span>
                  {writeOnlyEnabled
                    ? "只写模式：无法读回验证"
                    : selectedDisplay?.stable === false
                      ? "读数不可用或不稳定"
                      : "连接状态正常"}
                </span>
              </div>

              <label className="field-label">
                显示器
                <select
                  value={effectiveSelectedMonitor}
                  onChange={(event) => setSelectedMonitor(event.target.value)}
                  disabled={snapshot.displays.length === 0}
                >
                  {snapshot.displays.length === 0 ? <option value="">没有探测结果</option> : null}
                  {snapshot.displays.map((display) => (
                    <option key={displayId(display)} value={displayId(display)}>
                      {displayName(display)}
                    </option>
                  ))}
                </select>
              </label>

              <button
                className="secondary-button full-width"
                type="button"
                disabled={
                  connection !== "online" ||
                  busy === "display-probe"
                }
                onClick={() =>
                  void runAction(
                    "display-probe",
                    () =>
                      apiRequest<WriteResult>(
                        "/api/v1/displays/probe",
                        jsonRequest("POST"),
                      ),
                    "DDC/CI 探测完成；本操作没有发送切源命令。",
                  )
                }
              >
                {busy === "display-probe" ? "正在探测…" : "探测 DDC/CI 能力"}
              </button>
            </article>

            <form className="mapping-form" onSubmit={handleMappingSave}>
              <div>
                <span className="step-number">01</span>
                <div>
                  <h3>选择本机或已配对设备</h3>
                  <p>本机和离线的已配对设备也可映射；文件发送目标仍只允许在线远端。</p>
                </div>
              </div>
              <label className="field-label">
                设备
                <select
                  value={effectiveMappingTarget}
                  onChange={(event) => setMappingTarget(event.target.value)}
                  disabled={mappingTargets.length === 0}
                >
                  {mappingTargets.length === 0 ? (
                    <option value="">等待本机设备信息</option>
                  ) : null}
                  {mappingTargets.map((device) => (
                    <option key={device.id} value={device.id}>
                      {device.label}
                    </option>
                  ))}
                </select>
              </label>

              <div>
                <span className="step-number">02</span>
                <div>
                  <h3>填写显示器输入值</h3>
                  <p>可填写十进制或十六进制，例如 17 或 0x11。</p>
                </div>
              </div>
              <div className="field-pair">
                <label className="field-label">
                  显示名称
                  <input
                    type="text"
                    value={mappingLabel}
                    onChange={(event) => setMappingLabel(event.target.value)}
                    placeholder="例如：台式机 DP"
                  />
                </label>
                <label className="field-label">
                  VCP 输入值
                  {writeOnlyEnabled ? (
                    <select
                      value={mappingValue}
                      onChange={(event) => setMappingValue(event.target.value)}
                      required
                    >
                      <option value="">选择白名单输入源</option>
                      <option
                        value="0x0F"
                        disabled={!writeOnlyConfirmedInputs.includes(0x0f)}
                      >
                        DP — 0x0F{writeOnlyConfirmedInputs.includes(0x0f) ? "（已确认）" : "（先测试）"}
                      </option>
                      <option
                        value="0x11"
                        disabled={!writeOnlyConfirmedInputs.includes(0x11)}
                      >
                        HDMI1 — 0x11{writeOnlyConfirmedInputs.includes(0x11) ? "（已确认）" : "（先测试）"}
                      </option>
                    </select>
                  ) : (
                    <>
                      <input
                        type="text"
                        inputMode="text"
                        value={mappingValue}
                        onChange={(event) => setMappingValue(event.target.value)}
                        placeholder="例如当前读数 7"
                        required
                      />
                      {selectedDisplay?.currentInput != null ? (
                        <button
                          type="button"
                          className="text-button current-input-action"
                          onClick={() =>
                            setMappingValue(String(selectedDisplay.currentInput))
                          }
                        >
                          填入当前实际读数 {String(selectedDisplay.currentInput)}
                        </button>
                      ) : null}
                    </>
                  )}
                </label>
              </div>

              <button
                className="primary-button"
                type="submit"
                disabled={
                  connection !== "online" ||
                  !effectiveSelectedMonitor ||
                  !effectiveMappingTarget ||
                  busy === "display-map"
                }
              >
                {busy === "display-map" ? "正在保存…" : "保存输入源映射"}
              </button>
              <p className="form-footnote">
                只写模式下请先用下方按钮逐项测试，再保存对应设备映射。如果屏幕黑屏，请使用显示器实体摇杆恢复输入源。
              </p>
            </form>
          </div>

          <article
            className={`compatibility-setting ${writeOnlyEnabled ? "is-enabled" : ""}`}
            aria-labelledby="write-only-title"
          >
            <div className="compatibility-copy">
              <span className="experimental-badge">高风险实验功能</span>
              <h3 id="write-only-title">只写 DDC 兼容模式</h3>
              <p>
                适用于显示器无法读取 VCP 0x60、但可能仍接受切源写入命令的情况。DeskMesh
                只允许 DP 0x0F 与 HDMI1 0x11，不会扫描或尝试其他值。
              </p>
              <p className="compatibility-warning">
                命令返回成功只代表 Windows 已发送请求，不代表显示器实际完成切换；该模式不会开启实体按钮跟随。
              </p>
              <p className="compatibility-readiness" aria-live="polite">
                快捷键联动：{writeOnlyAutomaticReady
                  ? "已完成双向确认和本机/远端映射，可以使用"
                  : `尚未就绪（已确认 ${writeOnlyConfirmedInputs.length}/2 个输入，还需保存本机和远端的不同映射）`}
              </p>
              <label className="risk-confirmation">
                <input
                  type="checkbox"
                  checked={compatibilityRiskAccepted}
                  onChange={(event) => setCompatibilityRiskAccepted(event.target.checked)}
                  disabled={writeOnlyEnabled}
                />
                <span>我确认两台电脑都在输出信号，并能用显示器实体摇杆恢复黑屏。</span>
              </label>
            </div>
            <div className="compatibility-actions">
              <span className="follow-state" aria-live="polite">
                {writeOnlyEnabled
                  ? writeOnlyAutomaticReady
                    ? "已开启 · 双向联动就绪"
                    : "已开启 · 等待双向校准"
                  : selectedDisplay?.writeOnlyEligible
                    ? "可手动启用"
                    : "请先完成显示器探测"}
              </span>
              <button
                type="button"
                className={writeOnlyEnabled ? "danger-button" : "secondary-button"}
                disabled={
                  connection !== "online" ||
                  busy === "display-compatibility" ||
                  (!writeOnlyEnabled &&
                    (!selectedDisplay?.writeOnlyEligible || !compatibilityRiskAccepted))
                }
                onClick={() => void handleCompatibilityToggle()}
              >
                {busy === "display-compatibility"
                  ? "正在保存…"
                  : writeOnlyEnabled
                    ? "关闭兼容模式"
                    : "开启兼容模式"}
              </button>
              <div className="compatibility-test-buttons">
                <button
                  type="button"
                  className="secondary-button"
                  disabled={
                    connection !== "online" ||
                    !writeOnlyEnabled ||
                    busy === "display-compatibility-test"
                  }
                  onClick={() => void handleCompatibilityTest(0x11)}
                >
                  {busy === "display-compatibility-test"
                    ? "测试处理中…"
                    : writeOnlyConfirmedInputs.includes(0x11)
                      ? "重新测试 HDMI1 · 已确认"
                      : "测试 HDMI1 · 0x11"}
                </button>
                <button
                  type="button"
                  className="secondary-button"
                  disabled={
                    connection !== "online" ||
                    !writeOnlyEnabled ||
                    busy === "display-compatibility-test"
                  }
                  onClick={() => void handleCompatibilityTest(0x0f)}
                >
                  {busy === "display-compatibility-test"
                    ? "测试处理中…"
                    : writeOnlyConfirmedInputs.includes(0x0f)
                      ? "重新测试 DP · 已确认"
                      : "测试 DP · 0x0F"}
                </button>
              </div>
            </div>
          </article>

          <article
            className={`follow-setting ${physicalFollowEnabled ? "is-enabled" : ""}`}
            aria-labelledby="physical-follow-title"
          >
            <div className="follow-setting-copy">
              <span className="experimental-badge">实验功能</span>
              <h3 id="physical-follow-title">实体按钮切源后，键鼠自动跟随</h3>
              <p>
                当你用显示器实体按键切换输入源时，Agent 会读取当前输入并把键鼠控制权切到对应设备。必须先为输入源保存设备映射。
              </p>
              <small id="physical-follow-help">
                只在键盘鼠标实际连接的电脑开启，另一台保持关闭并自动充当检测端。两台电脑都要保存本机与对端的输入映射；连续两次得到相同且稳定的读数后才会切换，未知输入不会触发。
              </small>
            </div>
            <div className="follow-setting-control">
              <span className="follow-state" aria-live="polite">
                {connection !== "online"
                  ? "等待 Agent"
                    : physicalFollowEnabled
                      ? physicalFollowWaitingRemote
                        ? "已开启，等待远端 Agent 确认"
                        : "已开启"
                    : physicalFollowLostDdc
                      ? "切源后 DDC 通道失联，已自动停用"
                    : writeOnlyEnabled
                      ? "只写模式无法读取当前输入，实体跟随不可用"
                    : !status?.display?.supported
                      ? "需先完成 DDC/CI 探测"
                      : !status.display.stable
                        ? "等待稳定读数"
                        : !physicalFollowHasMappings
                          ? `还需 ${Math.max(0, 2 - stableFollowMappingCount)} 个不同输入映射`
                        : "已关闭"}
              </span>
              <input
                className="follow-switch"
                type="checkbox"
                role="switch"
                checked={physicalFollowEnabled}
                aria-labelledby="physical-follow-title"
                aria-describedby="physical-follow-help"
                disabled={
                  connection !== "online" ||
                  busy === "display-follow" ||
                  (!physicalFollowEnabled && !physicalFollowCanEnable)
                }
                onChange={(event) => {
                  const enabled = event.target.checked;
                  void runAction(
                    "display-follow",
                    () =>
                      apiRequest<WriteResult>(
                        "/api/v1/displays/follow",
                        jsonRequest("PATCH", { enabled }),
                      ),
                    enabled
                      ? "实体切源跟随已开启；连续两次稳定读数后才会切换键鼠。"
                      : "实体切源跟随已关闭。",
                  );
                }}
              />
            </div>
          </article>
        </section>

        <section id="security" className="content-section" aria-labelledby="security-title">
          <SectionHeader
            id="security-title"
            eyebrow="安全与配对"
            title="信任只留在你的局域网"
            description="新设备必须使用一次性配对码建立信任；所有状态均来自本机 Agent。"
          />

          <div className="security-grid">
            <article className="security-summary">
              <div className="security-badge" aria-hidden="true">✓</div>
              <div>
                <span>当前安全状态</span>
                <h3>
                  {connection !== "online"
                    ? "等待安全信息"
                    : security?.paired
                      ? "已建立可信配对"
                      : "尚未配对远端设备"}
                </h3>
              </div>
              <dl>
                <div>
                  <dt>传输</dt>
                  <dd>{security?.transport || security?.encryption || "等待 Agent"}</dd>
                </div>
                <div>
                  <dt>紧急释放</dt>
                  <dd>{security?.emergencyHotkey || "未报告"}</dd>
                </div>
                <div>
                  <dt>网络范围</dt>
                  <dd>{security?.sameSubnetOnly === false ? "允许已配置网络" : "仅本地子网"}</dd>
                </div>
              </dl>
              <div className="fingerprint-block">
                <span>本机证书指纹</span>
                <code>{security?.certificateFingerprint || "Agent 上线后显示"}</code>
                <button
                  type="button"
                  className="text-button"
                  disabled={!security?.certificateFingerprint}
                  onClick={() =>
                    void copyFingerprint(security?.certificateFingerprint, setNotice)
                  }
                >
                  复制指纹
                </button>
              </div>
            </article>

            <form className="pairing-form hotkey-form" onSubmit={handleHotkeySave}>
              <h3>全局切换快捷键</h3>
              <p>
                点击输入框后直接按组合键。为避免误触，必须包含 Ctrl、Alt、Shift
                中至少两个修饰键，主键支持 A-Z、0-9 和 F1-F24。
              </p>
              <label className="field-label">
                切到本机
                <input
                  type="text"
                  value={localHotkey}
                  readOnly
                  aria-describedby="hotkey-safety-note"
                  onKeyDown={(event) => {
                    const gesture = hotkeyFromKeyboardEvent(event);
                    if (!gesture) return;
                    event.preventDefault();
                    hotkeysDirty.current = true;
                    setLocalHotkey(gesture);
                  }}
                  onFocus={(event) => event.currentTarget.select()}
                />
              </label>
              <label className="field-label">
                切换到已配对远端 / 切换目标
                <input
                  type="text"
                  value={toggleHotkey}
                  readOnly
                  aria-describedby="hotkey-safety-note"
                  onKeyDown={(event) => {
                    const gesture = hotkeyFromKeyboardEvent(event);
                    if (!gesture) return;
                    event.preventDefault();
                    hotkeysDirty.current = true;
                    setToggleHotkey(gesture);
                  }}
                  onFocus={(event) => event.currentTarget.select()}
                />
              </label>
              <div className="hotkey-emergency" id="hotkey-safety-note">
                <span>固定紧急回本机</span>
                <strong>{snapshot.hotkeys?.emergency || "Ctrl+Alt+Shift+Esc"}</strong>
                <small>此组合永远保留，不能修改或重复使用。</small>
              </div>
              <div className="hotkey-actions">
                <button
                  type="button"
                  className="secondary-button"
                  onClick={() => {
                    hotkeysDirty.current = true;
                    setLocalHotkey("Ctrl+Alt+Shift+F11");
                    setToggleHotkey("Ctrl+Alt+Shift+F12");
                  }}
                >
                  恢复默认
                </button>
                <button
                  type="submit"
                  className="primary-button"
                  disabled={connection !== "online" || busy === "hotkeys"}
                >
                  {busy === "hotkeys" ? "正在保存…" : "保存并立即生效"}
                </button>
              </div>
            </form>

            <form className="pairing-form" onSubmit={handlePairing}>
              <h3>添加另一台电脑</h3>
              <p>
                地址对应哪台电脑，就必须填写那台电脑刚生成的配对码；本机码只供另一台电脑连接本机时使用。
              </p>
              <div className="local-pair-code" aria-live="polite">
                <span>本机一次性配对码</span>
                <strong>{security?.pairingCode || "等待 Agent"}</strong>
                <small>
                  {security?.expiresAt
                    ? `有效期至 ${formatDateTime(security.expiresAt)}`
                    : "可将此码填入另一台电脑"}
                </small>
                <button
                  type="button"
                  className="secondary-button"
                  disabled={
                    connection !== "online" || busy === "pairing-code-refresh"
                  }
                  onClick={() => void refreshAndCopyPairingCode()}
                >
                  {busy === "pairing-code-refresh"
                    ? "正在生成…"
                    : "生成并复制新码"}
                </button>
              </div>
              <label className="field-label">
                设备地址
                <input
                  ref={pairingAddressInput}
                  type="text"
                  value={pairingAddress}
                  onChange={(event) => {
                    pairingAddressEdited.current = true;
                    setPairingAddressSource(null);
                    setPairingAddress(event.target.value);
                  }}
                  placeholder="例如：192.168.1.24:45832"
                  autoComplete="off"
                  required
                />
                {pairingAddressSource ? (
                  <small className="discovery-hint">
                    已自动采用局域网发现的 {pairingAddressSource} 地址。
                  </small>
                ) : null}
              </label>
              <label className="field-label">
                配对码
                <input
                  type="text"
                  value={pairingCode}
                  onChange={(event) => setPairingCode(event.target.value)}
                  placeholder="6 位一次性配对码"
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  required
                />
              </label>
              <button
                className="primary-button full-width"
                type="submit"
                disabled={
                  connection !== "online" ||
                  !pairingAddress.trim() ||
                  !pairingCode.trim() ||
                  busy === "pair"
                }
              >
                {busy === "pair" ? "正在发起配对…" : "验证并配对"}
              </button>
              <small>配对码只用于本次握手，控制台不会长期保存。</small>
            </form>
          </div>

          {pairingRequests.length > 0 ? (
            <div className="pairing-requests" aria-labelledby="pairing-requests-title">
              <div className="pairing-requests-heading">
                <div>
                  <p className="eyebrow">安全短语确认</p>
                  <h3 id="pairing-requests-title">待确认与最近配对</h3>
                </div>
                <p>只有两台电脑显示完全相同的安全短语时才能批准。</p>
              </div>
              <div className="pairing-request-grid">
                {pairingRequests.slice(0, 4).map((request) => {
                  const isIncoming = request.direction === "incoming";
                  const isPending = request.status === "pending";
                  return (
                    <article className="pairing-request" key={request.id}>
                      <header>
                        <div>
                          <span>{isIncoming ? "传入请求" : "传出请求"}</span>
                          <h4>{request.deviceName || "未命名设备"}</h4>
                        </div>
                        <span className={`pairing-status ${request.status || "pending"}`}>
                          {pairingStatusLabel(request.status)}
                        </span>
                      </header>
                      <p className="pairing-address">
                        {request.address || "地址未知"}
                        {request.port ? `:${request.port}` : ""}
                      </p>
                      <div className="sas-block">
                        <span>两端安全短语</span>
                        <strong>{request.sas || "未提供"}</strong>
                      </div>
                      <p className="sas-guidance">
                        {isIncoming
                          ? "请与发起配对的电脑逐字核对；任何一段不同都应拒绝。"
                          : "请到另一台电脑核对相同短语，并在对方控制台批准。"}
                      </p>
                      {request.error ? (
                        <p className="pairing-error" role="alert">
                          {request.error}
                        </p>
                      ) : null}
                      {isIncoming && isPending ? (
                        <div className="pairing-actions">
                          <button
                            type="button"
                            className="secondary-button"
                            disabled={
                              connection !== "online" ||
                              busy === `pairing-confirm-${request.id}`
                            }
                            onClick={() =>
                              void runAction(
                                `pairing-confirm-${request.id}`,
                                () =>
                                  apiRequest<WriteResult>(
                                    `/api/v1/pairings/${encodeURIComponent(request.id)}/confirm`,
                                    jsonRequest("POST", { approve: false }),
                                  ),
                                "已拒绝此配对请求。",
                              )
                            }
                          >
                            拒绝
                          </button>
                          <button
                            type="button"
                            className="primary-button"
                            disabled={
                              connection !== "online" ||
                              busy === `pairing-confirm-${request.id}`
                            }
                            onClick={() =>
                              void runAction(
                                `pairing-confirm-${request.id}`,
                                () =>
                                  apiRequest<WriteResult>(
                                    `/api/v1/pairings/${encodeURIComponent(request.id)}/confirm`,
                                    jsonRequest("POST", { approve: true }),
                                  ),
                                "设备身份已确认并建立信任。",
                              )
                            }
                          >
                            短语一致，批准
                          </button>
                        </div>
                      ) : null}
                    </article>
                  );
                })}
              </div>
            </div>
          ) : null}
        </section>

        <section id="diagnostics" className="content-section" aria-labelledby="diagnostics-title">
          <SectionHeader
            id="diagnostics-title"
            eyebrow="故障诊断"
            title="看清楚是谁触发了切换"
            description="最多保留本次运行最近 200 条记录，包含控制切换、输入 WebSocket、DDC/CI、实体跟随和网络心跳。"
          />
          <div className="diagnostic-toolbar">
            <span>{snapshot.diagnostics.length} 条记录</span>
            <div>
              <button
                type="button"
                className="secondary-button"
                disabled={snapshot.diagnostics.length === 0}
                onClick={() => {
                  const text = snapshot.diagnostics
                    .slice()
                    .reverse()
                    .map((entry) =>
                      `${entry.at} [${entry.level}] ${entry.source}: ${entry.message}`,
                    )
                    .join("\n");
                  void navigator.clipboard.writeText(text).then(
                    () => setNotice({ tone: "success", message: "诊断日志已复制。" }),
                    () => setNotice({ tone: "warning", message: "浏览器未允许复制，请手动选择日志。" }),
                  );
                }}
              >
                复制日志
              </button>
              <button
                type="button"
                className="text-button"
                disabled={connection !== "online" || busy === "clear-diagnostics" || snapshot.diagnostics.length === 0}
                onClick={() =>
                  void runAction(
                    "clear-diagnostics",
                    () => apiRequest<WriteResult>("/api/v1/diagnostics", { method: "DELETE" }),
                    "诊断日志已清空。",
                  )
                }
              >
                清空
              </button>
            </div>
          </div>
          <div className="diagnostic-log" role="log" aria-live="polite">
            {snapshot.diagnostics.length === 0 ? (
              <p className="diagnostic-empty">暂无记录。执行一次切换后，这里会显示完整原因。</p>
            ) : (
              snapshot.diagnostics.map((entry) => (
                <article className={`diagnostic-entry ${entry.level}`} key={entry.id}>
                  <time dateTime={entry.at}>{formatDateTime(entry.at)}</time>
                  <strong>{entry.source}</strong>
                  <span>{entry.message}</span>
                </article>
              ))
            )}
          </div>
        </section>

        <footer className="page-footer">
          <span>DeskMesh 桌联本地控制台</span>
          <span>{lastSync ? `最后同步 ${formatClock(lastSync)}` : "尚未连接 Agent"}</span>
        </footer>
      </main>
    </div>
  );
}

function Metric({ label, value, hint }: { label: string; value: string; hint: string }) {
  return (
    <article className="metric-card">
      <span>{label}</span>
      <strong>{value}</strong>
      <small>{hint}</small>
    </article>
  );
}

function SectionHeader({
  id,
  eyebrow,
  title,
  description,
  compact = false,
}: {
  id: string;
  eyebrow: string;
  title: string;
  description: string;
  compact?: boolean;
}) {
  return (
    <div className={`section-heading ${compact ? "compact" : ""}`}>
      <div>
        <p className="eyebrow">{eyebrow}</p>
        <h2 id={id}>{title}</h2>
      </div>
      <p>{description}</p>
    </div>
  );
}

function EmptyCard({ title, description }: { title: string; description: string }) {
  return (
    <article className="empty-card">
      <span aria-hidden="true">···</span>
      <h3>{title}</h3>
      <p>{description}</p>
    </article>
  );
}

function normalizePolicy(value: unknown): Partial<ClipboardPolicy> {
  if (!value || typeof value !== "object") return {};
  const policy = value as Record<string, unknown>;
  const result: Partial<ClipboardPolicy> = {};
  if (typeof policy.enabled === "boolean") result.enabled = policy.enabled;
  if (typeof policy.direction === "string") {
    result.direction = normalizeDirection(policy.direction, "bidirectional");
  }
  const allowText = policy.allowText ?? policy.textEnabled;
  const allowImages = policy.allowImages ?? policy.imageEnabled;
  if (typeof allowText === "boolean") result.allowText = allowText;
  if (typeof allowImages === "boolean") result.allowImages = allowImages;
  if (typeof policy.allowFiles === "boolean") result.allowFiles = policy.allowFiles;
  if (typeof policy.maxTextBytes === "number") result.maxTextBytes = policy.maxTextBytes;
  if (typeof policy.maxImageBytes === "number") result.maxImageBytes = policy.maxImageBytes;
  if (typeof policy.maxFileBytes === "number") result.maxFileBytes = policy.maxFileBytes;
  return result;
}

function normalizeDirection(
  value: string | undefined,
  fallback: ClipboardPolicy["direction"],
): ClipboardPolicy["direction"] {
  const directions: Record<string, ClipboardPolicy["direction"]> = {
    disabled: "disabled",
    sendOnly: "sendOnly",
    "send-only": "sendOnly",
    receiveOnly: "receiveOnly",
    "receive-only": "receiveOnly",
    bidirectional: "bidirectional",
  };
  return value ? directions[value] || fallback : fallback;
}

function directionToWire(direction: ClipboardPolicy["direction"]) {
  if (direction === "sendOnly") return "send-only";
  if (direction === "receiveOnly") return "receive-only";
  return direction;
}

function getErrorMessage(error: unknown) {
  if (!(error instanceof Error)) return "操作失败，请检查本机 Agent。";
  const message = error.message.trim();
  if (/^(the operation|a task) was canceled\.?$/i.test(message)) {
    return "切换确认超时，已安全恢复本机控制；请确认两台 Agent 在线后重试。";
  }
  return message;
}

function focusDescription(status: AgentStatus | null) {
  if (!status?.focus) return "Agent 尚未报告控制权状态。";
  const phase = phaseLabel(status.focus.phase);
  const epoch = status.focus.epoch != null ? ` · 会话 ${status.focus.epoch}` : "";
  return `${phase}${epoch}。切换时会先释放按键状态，断线则自动回退本机。`;
}

function phaseLabel(phase?: string) {
  const labels: Record<string, string> = {
    local: "本机输入正常",
    preparingRemote: "正在准备远端控制",
    remote: "输入正在发送到远端",
    recovering: "正在恢复本机输入",
    switching: "正在切换控制目标",
  };
  return phase ? labels[phase] || phase : "控制状态正常";
}

function roleLabel(role?: string) {
  const labels: Record<string, string> = {
    controller: "控制端",
    target: "受控端",
    peer: "对等设备",
    local: "本机",
  };
  return role ? labels[role.toLowerCase()] || role : "等待 Agent";
}

function capabilityLabel(value: string) {
  const labels: Record<string, string> = {
    input: "键鼠",
    keyboard: "键盘",
    mouse: "鼠标",
    clipboard: "剪贴板",
    files: "文件",
    display: "显示器",
    ddc: "DDC/CI",
    audio: "音频",
    "remote-desktop": "远程桌面",
  };
  return labels[value.toLowerCase()] || value;
}

function audioPhaseLabel(audio: AudioSettings | null) {
  if (!audio) return "等待 Agent 报告音频状态";
  const labels: Record<string, string> = {
    idle: "等待切换到远端",
    connecting: "正在连接远端音频",
    playing: "正在本机耳机播放",
    sending: "正在向控制端发送声音",
    error: "音频连接异常",
  };
  return `${labels[audio.phase] || audio.phase} · ${audio.message}`;
}

function kindLabel(kind?: string) {
  const labels: Record<string, string> = {
    text: "文本",
    image: "图片",
    file: "文件",
  };
  return kind ? labels[kind.toLowerCase()] || kind : "未知";
}

function hotkeyFromKeyboardEvent(event: KeyboardEvent<HTMLInputElement>) {
  if (event.repeat || event.metaKey) return null;
  let mainKey = "";
  if (/^Key[A-Z]$/.test(event.code)) mainKey = event.code.slice(3);
  else if (/^Digit[0-9]$/.test(event.code)) mainKey = event.code.slice(5);
  else if (/^F(?:[1-9]|1[0-9]|2[0-4])$/.test(event.key.toUpperCase())) {
    mainKey = event.key.toUpperCase();
  }
  if (!mainKey) return null;

  const parts: string[] = [];
  if (event.ctrlKey) parts.push("Ctrl");
  if (event.altKey) parts.push("Alt");
  if (event.shiftKey) parts.push("Shift");
  parts.push(mainKey);
  return parts.join("+");
}

function networkLabel(network: unknown) {
  if (typeof network === "string" && network) return network;
  if (network && typeof network === "object") {
    const data = network as Record<string, unknown>;
    const address = data.address ?? data.localAddress ?? data.ip;
    const adapter = data.adapterName ?? data.interfaceName ?? data.name;
    const addresses = Array.isArray(data.addresses)
      ? data.addresses.filter(
          (value): value is string => typeof value === "string" && Boolean(value),
        )
      : [];
    if (addresses.length) return `局域网 · ${addresses.join(" / ")}`;
    if (address && adapter) return `${String(adapter)} · ${String(address)}`;
    if (address) return String(address);
    if (adapter) return String(adapter);
  }
  return "网络信息由 Agent 管理";
}

function formatUptime(seconds?: number) {
  if (seconds == null || !Number.isFinite(seconds)) return "—";
  if (seconds < 60) return `${Math.floor(seconds)} 秒`;
  if (seconds < 3_600) return `${Math.floor(seconds / 60)} 分钟`;
  if (seconds < 86_400) return `${Math.floor(seconds / 3_600)} 小时`;
  return `${Math.floor(seconds / 86_400)} 天`;
}

function formatBytes(bytes?: number) {
  if (bytes == null || !Number.isFinite(bytes)) return "大小未知";
  if (bytes < 1_024) return `${bytes} B`;
  if (bytes < 1_048_576) return `${(bytes / 1_024).toFixed(1)} KB`;
  if (bytes < 1_073_741_824) return `${(bytes / 1_048_576).toFixed(1)} MB`;
  return `${(bytes / 1_073_741_824).toFixed(1)} GB`;
}

function formatClock(date: Date) {
  return new Intl.DateTimeFormat("zh-CN", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  }).format(date);
}

function formatRelativeTime(value?: string) {
  if (!value) return "时间未知";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  const diffSeconds = Math.max(0, Math.round((Date.now() - date.getTime()) / 1_000));
  if (diffSeconds < 10) return "刚刚";
  if (diffSeconds < 60) return `${diffSeconds} 秒前`;
  if (diffSeconds < 3_600) return `${Math.floor(diffSeconds / 60)} 分钟前`;
  return new Intl.DateTimeFormat("zh-CN", {
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  }).format(date);
}

function formatDateTime(value: string) {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat("zh-CN", {
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  }).format(date);
}

function displayId(display: DisplayInfo) {
  return display.monitorId || display.id || "";
}

function displayName(display: DisplayInfo) {
  return display.monitorName || display.name || displayId(display) || "未命名显示器";
}

function parseVcpValue(value: string) {
  const clean = value.trim();
  if (!clean) return null;
  const parsed = clean.toLowerCase().startsWith("0x")
    ? Number.parseInt(clean.slice(2), 16)
    : Number.parseInt(clean, 10);
  return Number.isInteger(parsed) && parsed >= 0 && parsed <= 255 ? parsed : null;
}

function offerId(offer: FileOffer) {
  return offer.id || offer.transferId || "";
}

function offerStateLabel(state?: string) {
  const labels: Record<string, string> = {
    pending: "等待确认",
    offered: "等待确认",
    "awaiting-confirmation": "等待确认",
    sending: "传送中",
    accepted: "已接受",
    transferring: "传送中",
    completed: "已完成",
    rejected: "已拒绝",
    "decision-failed": "通知失败，可重试",
    failed: "失败",
    cancelled: "已取消",
  };
  return state ? labels[state.toLowerCase()] || state : "处理中";
}

function pairingStatusLabel(status?: string) {
  const labels: Record<string, string> = {
    pending: "等待确认",
    approved: "已批准",
    rejected: "已拒绝",
    expired: "已过期",
    failed: "失败",
  };
  return status ? labels[status.toLowerCase()] || status : "等待确认";
}

async function saveDisplayMapping(mapping: {
  monitorId: string;
  deviceId: string;
  label: string;
  vcpValue: number;
}, probeAfter: boolean) {
  const result = await apiRequest<WriteResult>(
    "/api/v1/displays/mapping",
    jsonRequest("PUT", mapping),
  );
  if (probeAfter) {
    await apiRequest<unknown>(
      "/api/v1/displays/probe",
      jsonRequest("POST", { monitorId: mapping.monitorId }),
    );
  }
  return result;
}

async function uploadFile(
  file: File,
  targetDeviceId: string,
  onProgress: (progress: number) => void,
  onRequestCreated: (request: XMLHttpRequest) => void,
  isCancellationRequested: () => boolean,
) {
  const request = new XMLHttpRequest();
  onRequestCreated(request);
  let sessionToken: string;
  try {
    sessionToken = await getSessionToken();
  } catch (error) {
    if (isCancellationRequested()) {
      throw new ApiError(FILE_UPLOAD_CANCELLED_MESSAGE);
    }
    throw error;
  }
  if (isCancellationRequested()) {
    throw new ApiError(FILE_UPLOAD_CANCELLED_MESSAGE);
  }

  return new Promise<WriteResult>((resolve, reject) => {
    const data = new FormData();
    data.append("targetDeviceId", targetDeviceId);
    data.append("file", file);

    request.open("POST", "/api/v1/files/offers");
    request.setRequestHeader("X-LanSwitch-Client", sessionToken);
    request.responseType = "json";
    request.upload.addEventListener("progress", (event) => {
      if (event.lengthComputable) {
        onProgress(Math.min(99, Math.round((event.loaded / event.total) * 100)));
      }
    });
    request.addEventListener("load", () => {
      if (request.status >= 200 && request.status < 300) {
        onProgress(100);
        resolve((request.response ?? {}) as WriteResult);
      } else {
        if (request.status === 403) resetSessionToken();
        const message =
          request.response && typeof request.response === "object"
            ? String(
                request.response.error ??
                  request.response.message ??
                  request.response.detail ??
                  request.response.title ??
                  "",
              )
            : "";
        reject(new ApiError(message || `文件请求失败（HTTP ${request.status}）`, request.status));
      }
    });
    request.addEventListener("error", () => reject(new ApiError("无法连接本机 Agent")));
    request.addEventListener("abort", () =>
      reject(new ApiError(FILE_UPLOAD_CANCELLED_MESSAGE)),
    );
    if (isCancellationRequested()) {
      reject(new ApiError(FILE_UPLOAD_CANCELLED_MESSAGE));
      return;
    }
    request.send(data);
  });
}

async function copyFingerprint(
  fingerprint: string | undefined,
  setNotice: (notice: Notice) => void,
) {
  if (!fingerprint) return;
  try {
    await navigator.clipboard.writeText(fingerprint);
    setNotice({ tone: "success", message: "证书指纹已复制。" });
  } catch {
    setNotice({
      tone: "warning",
      message: "浏览器未允许写入剪贴板，请手动选择指纹复制。",
    });
  }
}
