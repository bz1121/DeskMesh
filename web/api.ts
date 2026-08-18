export type ConnectionState = "checking" | "online" | "offline";

export const MINIMUM_ADMIN_PASSWORD_LENGTH = 6;

export type AgentStatus = {
  deviceId?: string;
  deviceName?: string;
  role?: string;
  version?: string;
  uptimeSeconds?: number;
  network?: unknown;
  switchMode?: SwitchMode;
  focus?: {
    epoch?: number;
    activeDeviceId?: string;
    activeDeviceName?: string;
    phase?: string;
    isRemote?: boolean;
  };
  clipboard?: {
    enabled?: boolean;
    direction?: ClipboardPolicy["direction"];
    latest?: ClipboardEntry | null;
  };
  display?: {
    supported?: boolean;
    stable?: boolean;
    currentInput?: number | string;
    currentLabel?: string;
    physicalFollowEnabled?: boolean;
    writeOnlyEnabled?: boolean;
    writeOnlyMonitorId?: string;
    writeOnlyConfirmedInputs?: number[];
    writeOnlyAutomaticReady?: boolean;
    message?: string;
  };
  security?: SecurityStatus;
};

export type PeerSummary = {
  id: string;
  name?: string;
  address?: string;
  online?: boolean;
  latencyMs?: number;
  lastSeen?: string;
  capabilities?: string[];
  paired?: boolean;
  fingerprint?: string;
};

export type ClipboardEntry = {
  kind?: string;
  textPreview?: string;
  sizeBytes?: number;
  sourceDeviceName?: string;
  updatedAt?: string;
};

export type ClipboardPolicy = {
  enabled: boolean;
  direction: "disabled" | "sendOnly" | "receiveOnly" | "bidirectional";
  allowText: boolean;
  allowImages: boolean;
  allowFiles: boolean;
  maxTextBytes?: number;
  maxImageBytes?: number;
  maxFileBytes?: number;
};

export type ClipboardSnapshot = {
  policy?: Partial<ClipboardPolicy>;
  latest?: ClipboardEntry | null;
  textEnabled?: boolean;
  imageEnabled?: boolean;
};

export type FileOffer = {
  id?: string;
  transferId?: string;
  fileName?: string;
  length?: number;
  sizeBytes?: number;
  sha256?: string;
  chunkSize?: number;
  chunkCount?: number;
  sourceDeviceId?: string;
  sourceDeviceName?: string;
  targetDeviceId?: string;
  targetDeviceName?: string;
  direction?: "incoming" | "outgoing" | string;
  state?: string;
  status?: string;
  error?: string | null;
  createdAt?: string;
};

export type DisplayInfo = {
  id?: string;
  monitorId?: string;
  name?: string;
  monitorName?: string;
  supported?: boolean;
  stable?: boolean;
  currentInput?: number | string;
  currentLabel?: string;
  enabled?: boolean;
  writeOnlyEligible?: boolean;
  mappings?: Array<{
    deviceId?: string;
    label?: string;
    vcpValue?: number;
  }>;
};

export type SecurityStatus = {
  paired?: boolean;
  transport?: string;
  certificateFingerprint?: string;
  fingerprint?: string;
  emergencyHotkey?: string;
  sameSubnetOnly?: boolean;
  encryption?: string;
  pairingCode?: string;
  expiresAt?: string;
  pending?: PairingRequest[];
};

export type AuthStatus = {
  configured: boolean;
  authenticated: boolean;
  state?:
    | "setup-required"
    | "recovery-required"
    | "login-required"
    | "authenticated"
    | string;
  recoveryRequired?: boolean;
  username?: string | null;
  role?: string | null;
  sessionExpiresAt?: string | null;
  idleExpiresAt?: string | null;
  activeSessionCount?: number | null;
  failedAttempts?: number | null;
  lockedUntil?: string | null;
  lastLoginAt?: string | null;
};

export type SessionEnvelope = {
  token: string | null;
  auth?: AuthStatus;
};

export type SessionRevokeResult = {
  revoked?: number;
  auth: AuthStatus;
};

export type PrivilegedBridgeStatus = {
  packaged: boolean;
  installed: boolean;
  secureDesktopActive: boolean;
  message?: string | null;
};

export type AiAssistantStatus = {
  enabled: boolean;
  baseUrl: string;
  model: string;
  autoApplySafeFixes: boolean;
  apiKeyConfigured: boolean;
  secretError?: string | null;
};

export type AiAssistantUpdate = {
  enabled: boolean;
  baseUrl: string;
  model: string;
  autoApplySafeFixes: boolean;
  apiKey?: string | null;
  clearApiKey?: boolean;
};

export type AiRepairAction = {
  id: string;
  description: string;
  risk: "low" | "medium" | string;
  reason: string;
};

export type AiRepairProposal = {
  id: string;
  summary: string;
  confidence: number;
  actions: AiRepairAction[];
  expiresAt: string;
};

export type AiRepairExecution = {
  proposalId: string;
  automatic: boolean;
  results: Array<{ id: string; succeeded: boolean; message: string }>;
  completedAt: string;
};

export type HotkeySettings = {
  switchToLocal: string;
  toggleRemote: string;
  emergency: string;
};

export type SwitchMode = "directSignal" | "seamlessRemote";

export type SwitchModeSettings = {
  mode: SwitchMode;
};

export type AudioSettings = {
  enabled: boolean;
  volume: number;
  phase: "idle" | "connecting" | "playing" | "sending" | "error" | string;
  activeDeviceId?: string | null;
  activeDeviceName?: string | null;
  message: string;
};

export type RemoteDesktopSettings = {
  enabled: boolean;
  framesPerSecond: number;
  jpegQuality: number;
};

export type RemoteDesktopDisplay = {
  index: number;
  deviceName: string;
  width: number;
  height: number;
  left: number;
  top: number;
  primary: boolean;
};

export type RemoteDesktopDisplayCatalog = {
  generation: number;
  displays: RemoteDesktopDisplay[];
};

export type DiagnosticLog = {
  id: number;
  at: string;
  level: "success" | "info" | "warning" | "error" | string;
  source: string;
  message: string;
};

export type LocalPairingCode = {
  pairingCode: string;
  expiresAt: string;
};

export type PairingRequest = {
  id: string;
  direction?: "incoming" | "outgoing" | string;
  deviceId?: string;
  deviceName?: string;
  address?: string;
  port?: number;
  fingerprint?: string;
  sas?: string;
  status?: string;
  createdAt?: string;
  error?: string | null;
};

export type WriteResult = {
  requestId?: string;
  accepted?: boolean;
  error?: string | null;
  [key: string]: unknown;
};

export class ApiError extends Error {
  status: number;

  constructor(message: string, status = 0) {
    super(message);
    this.name = "ApiError";
    this.status = status;
  }
}

export const AUTH_REQUIRED_EVENT = "deskmesh:auth-required";

export type AuthRequiredEventDetail = {
  status: 401 | 423 | 428;
};

export type ApiRequestOptions = {
  notifyAuthFailure?: boolean;
  skipSessionToken?: boolean;
};

function notifyAuthRequired(status: 401 | 423 | 428) {
  window.dispatchEvent(
    new CustomEvent<AuthRequiredEventDetail>(AUTH_REQUIRED_EVENT, {
      detail: { status },
    }),
  );
}

let sessionTokenPromise: Promise<string> | null = null;

export function getSessionToken(): Promise<string> {
  if (sessionTokenPromise) return sessionTokenPromise;

  sessionTokenPromise = fetch("/api/v1/session", {
    method: "GET",
    headers: { Accept: "application/json" },
    cache: "no-store",
    credentials: "same-origin",
  })
    .then(async (response) => {
      if (!response.ok) {
        if (
          response.status === 401 ||
          response.status === 423 ||
          response.status === 428
        ) {
          sessionTokenPromise = null;
          notifyAuthRequired(response.status);
        }
        throw new ApiError(
          `无法建立本机安全会话（HTTP ${response.status}）`,
          response.status,
        );
      }
      const body = (await response.json()) as Partial<SessionEnvelope>;
      if (typeof body.token !== "string" || !body.token) {
        const status = body.auth?.recoveryRequired ||
          body.auth?.state === "recovery-required"
          ? 423
          : body.auth?.configured === false ||
              body.auth?.state === "setup-required"
            ? 428
            : 401;
        sessionTokenPromise = null;
        notifyAuthRequired(status);
        throw new ApiError("本机控制台会话已失效，请重新登录。", status);
      }
      return body.token;
    })
    .catch((error) => {
      sessionTokenPromise = null;
      if (error instanceof ApiError) throw error;
      throw new ApiError("无法建立本机安全会话");
    });

  return sessionTokenPromise;
}

export function resetSessionToken() {
  sessionTokenPromise = null;
}

export async function apiRequest<T>(
  path: string,
  init: RequestInit = {},
  timeoutMs = 6500,
  options: ApiRequestOptions = {},
): Promise<T> {
  const controller = new AbortController();
  const timer = window.setTimeout(() => controller.abort(), timeoutMs);
  const headers = new Headers(init.headers);
  headers.set("Accept", "application/json");
  const method = (init.method ?? "GET").toUpperCase();

  try {
    if (
      method !== "GET" &&
      method !== "HEAD" &&
      options.skipSessionToken !== true
    ) {
      headers.set("X-LanSwitch-Client", await getSessionToken());
    }
    const response = await fetch(path, {
      ...init,
      headers,
      cache: "no-store",
      credentials: "same-origin",
      signal: controller.signal,
    });

    const contentType = response.headers.get("content-type") ?? "";
    const body = contentType.includes("json")
      ? await response.json().catch(() => null)
      : await response.text().catch(() => "");

    if (!response.ok) {
      if (
        response.status === 403 &&
        method !== "GET" &&
        method !== "HEAD"
      ) {
        resetSessionToken();
      }
      if (
        options.notifyAuthFailure !== false &&
        (response.status === 401 ||
          response.status === 423 ||
          response.status === 428)
      ) {
        resetSessionToken();
        notifyAuthRequired(response.status);
      }
      const detail =
        body && typeof body === "object"
          ? String(
              (body as Record<string, unknown>).error ??
                (body as Record<string, unknown>).message ??
                (body as Record<string, unknown>).detail ??
                (body as Record<string, unknown>).title ??
                "",
            )
          : String(body ?? "");
      throw new ApiError(
        detail || `请求失败（HTTP ${response.status}）`,
        response.status,
      );
    }

    return body as T;
  } catch (error) {
    if (error instanceof ApiError) throw error;
    if (error instanceof DOMException && error.name === "AbortError") {
      throw new ApiError("本机代理响应超时");
    }
    throw new ApiError("无法连接本机 DeskMesh Agent");
  } finally {
    window.clearTimeout(timer);
  }
}

export function jsonRequest(method: "POST" | "PUT" | "PATCH", body?: unknown) {
  return {
    method,
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  } satisfies RequestInit;
}

export function unwrapList<T>(value: unknown, keys: string[]): T[] {
  if (Array.isArray(value)) return value as T[];
  if (!value || typeof value !== "object") return [];
  const record = value as Record<string, unknown>;
  for (const key of keys) {
    if (Array.isArray(record[key])) return record[key] as T[];
  }
  return [];
}

export function resultAccepted(result: WriteResult | null | undefined) {
  return result?.accepted !== false && !result?.error;
}
