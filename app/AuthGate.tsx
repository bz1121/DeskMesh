import {
  createContext,
  type FormEvent,
  type PropsWithChildren,
  type ReactNode,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  ApiError,
  apiRequest,
  AUTH_REQUIRED_EVENT,
  MINIMUM_ADMIN_PASSWORD_LENGTH,
  type AuthStatus,
  jsonRequest,
  resetSessionToken,
} from "../web/api";

type AdminAuthContextValue = {
  auth: AuthStatus;
  endingSession: boolean;
  lock: () => Promise<void>;
  refreshAuth: () => Promise<AuthStatus | null>;
  updateAuth: (next: AuthStatus) => void;
};

const AdminAuthContext = createContext<AdminAuthContextValue | null>(null);

const consoleRoutes = new Set([
  "overview",
  "devices",
  "remote-desktop",
  "clipboard",
  "files",
  "display",
  "security",
  "admin",
  "diagnostics",
]);

let initialBootstrapToken = consumeBootstrapTokenFromUrl();

export function useAdminAuth() {
  const value = useContext(AdminAuthContext);
  if (!value) {
    throw new Error("管理员认证上下文只能在 AuthGate 内使用");
  }
  return value;
}

export default function AuthGate({ children }: PropsWithChildren) {
  const [auth, setAuth] = useState<AuthStatus | null>(null);
  const [offline, setOffline] = useState(false);
  const [checking, setChecking] = useState(true);
  const [endingSession, setEndingSession] = useState(false);
  const [bootstrapToken, setBootstrapToken] = useState<string | null>(
    initialBootstrapToken,
  );

  const refreshAuth = useCallback(async () => {
    try {
      const next = await apiRequest<AuthStatus>(
        "/api/v1/auth/status",
        {},
        6500,
        { notifyAuthFailure: false },
      );
      setAuth(next);
      setOffline(false);
      return next;
    } catch {
      setOffline(true);
      return null;
    } finally {
      setChecking(false);
    }
  }, []);

  useEffect(() => {
    const timer = window.setTimeout(() => void refreshAuth(), 0);
    return () => window.clearTimeout(timer);
  }, [refreshAuth]);

  useEffect(() => {
    initialBootstrapToken = null;
  }, []);

  useEffect(() => {
    const handleAuthRequired = () => {
      resetSessionToken();
      void refreshAuth();
    };
    window.addEventListener(AUTH_REQUIRED_EVENT, handleAuthRequired);
    return () =>
      window.removeEventListener(AUTH_REQUIRED_EVENT, handleAuthRequired);
  }, [refreshAuth]);

  useEffect(() => {
    if (!offline) return;
    const timer = window.setInterval(() => void refreshAuth(), 5000);
    return () => window.clearInterval(timer);
  }, [offline, refreshAuth]);

  const updateAuth = useCallback((next: AuthStatus) => {
    setAuth(next);
    setOffline(false);
  }, []);

  useEffect(() => {
    if (!auth?.authenticated) return;
    let lastTouchAt = Date.now();
    let touchInFlight = false;

    const touchSession = (event: Event) => {
      if (!event.isTrusted || document.visibilityState !== "visible") return;
      const now = Date.now();
      if (touchInFlight || now - lastTouchAt < 120_000) return;
      lastTouchAt = now;
      touchInFlight = true;
      void apiRequest<AuthStatus>(
        "/api/v1/auth/touch",
        jsonRequest("POST"),
      )
        .then(updateAuth)
        .catch(() => {
          // apiRequest announces expired/recovery sessions to this gate.
        })
        .finally(() => {
          touchInFlight = false;
        });
    };

    window.addEventListener("pointerdown", touchSession, { passive: true });
    window.addEventListener("keydown", touchSession);
    window.addEventListener("touchstart", touchSession, { passive: true });
    document.addEventListener("visibilitychange", touchSession);
    return () => {
      window.removeEventListener("pointerdown", touchSession);
      window.removeEventListener("keydown", touchSession);
      window.removeEventListener("touchstart", touchSession);
      document.removeEventListener("visibilitychange", touchSession);
    };
  }, [auth?.authenticated, updateAuth]);

  const lock = useCallback(async () => {
    setEndingSession(true);
    try {
      const next = await apiRequest<AuthStatus>(
        "/api/v1/auth/logout",
        jsonRequest("POST"),
        6500,
      );
      resetSessionToken();
      setAuth({ ...next, authenticated: false, state: "login-required" });
    } finally {
      setEndingSession(false);
    }
  }, []);

  const contextValue = useMemo<AdminAuthContextValue | null>(
    () =>
      auth?.authenticated
        ? { auth, endingSession, lock, refreshAuth, updateAuth }
        : null,
    [auth, endingSession, lock, refreshAuth, updateAuth],
  );

  if (checking && !auth) {
    return <AuthMessage title="正在检查本机控制台" message="正在与 DeskMesh Agent 建立安全连接…" busy />;
  }

  if (offline || !auth) {
    return (
      <AuthMessage
        title="本机 Agent 未运行"
        message="登录暂不可用。请先启动 DeskMesh，再重新检查本机控制台。"
        actionLabel={checking ? "正在检查…" : "重新检查"}
        actionDisabled={checking}
        onAction={() => {
          setChecking(true);
          void refreshAuth();
        }}
      />
    );
  }

  if (auth.recoveryRequired || auth.state === "recovery-required") {
    return (
      <AuthMessage
        tone="danger"
        title="管理员凭据需要恢复"
        message="本机管理员凭据已损坏或无法由当前 Windows 用户读取。请从 DeskMesh 托盘菜单重置控制台登录，然后重新打开控制台。已配对设备不会因此自动删除。"
        actionLabel="重新检查"
        onAction={() => void refreshAuth()}
      />
    );
  }

  if (!auth.configured || auth.state === "setup-required") {
    if (!bootstrapToken) {
      return (
        <AuthMessage
          title="请从托盘完成首次设置"
          message="为防止其他本机网页抢先注册管理员，首次设置必须从 DeskMesh 托盘菜单打开控制台。一次性设置链接不会通过局域网发送。"
          actionLabel="我已从托盘重新打开"
          onAction={() => void refreshAuth()}
        />
      );
    }
    return (
      <SetupView
        bootstrapToken={bootstrapToken}
        onBootstrapRejected={() => setBootstrapToken(null)}
        onAuthenticated={(next) => {
          setBootstrapToken(null);
          resetSessionToken();
          updateAuth(next);
        }}
        onRefresh={refreshAuth}
      />
    );
  }

  if (!auth.authenticated || auth.state === "login-required") {
    return (
      <LoginView
        auth={auth}
        onAuthenticated={(next) => {
          resetSessionToken();
          updateAuth(next);
        }}
        onRefresh={refreshAuth}
      />
    );
  }

  return (
    <AdminAuthContext.Provider value={contextValue}>
      {children}
    </AdminAuthContext.Provider>
  );
}

function SetupView({
  bootstrapToken,
  onBootstrapRejected,
  onAuthenticated,
  onRefresh,
}: {
  bootstrapToken: string;
  onBootstrapRejected: () => void;
  onAuthenticated: (next: AuthStatus) => void;
  onRefresh: () => Promise<AuthStatus | null>;
}) {
  const [username, setUsername] = useState("admin");
  const [password, setPassword] = useState("");
  const [confirmation, setConfirmation] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const usernameInput = useRef<HTMLInputElement>(null);

  useAuthDocumentTitle("首次设置");
  useEffect(() => usernameInput.current?.focus(), []);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const normalizedUsername = username.trim();
    if (!normalizedUsername) {
      setError("请输入管理员名称。");
      return;
    }
    if (password.length < MINIMUM_ADMIN_PASSWORD_LENGTH) {
      setError(`管理员密码至少需要 ${MINIMUM_ADMIN_PASSWORD_LENGTH} 个字符。`);
      return;
    }
    if (password !== confirmation) {
      setError("两次输入的密码不一致。");
      return;
    }

    setBusy(true);
    setError(null);
    try {
      const next = await apiRequest<AuthStatus>(
        "/api/v1/auth/setup",
        jsonRequest("POST", {
          username: normalizedUsername,
          password,
          bootstrapToken,
        }),
        15_000,
        { notifyAuthFailure: false, skipSessionToken: true },
      );
      setPassword("");
      setConfirmation("");
      onAuthenticated(next);
    } catch (caught) {
      const status = caught instanceof ApiError ? caught.status : 0;
      if (status === 403) {
        onBootstrapRejected();
        setError("一次性设置链接无效或已过期，请从 DeskMesh 托盘重新打开控制台。");
      } else if (status === 409) {
        setError("管理员已经创建，正在切换到登录界面。");
        await onRefresh();
      } else if (status === 423) {
        setError("管理员凭据需要从 DeskMesh 托盘恢复，网页不能直接覆盖。");
        await onRefresh();
      } else {
        setError(authErrorMessage(caught, "无法创建本机管理员，请稍后重试。"));
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthFrame
      eyebrow="首次设置"
      title="创建本机管理员"
      description="此登录只保护这台电脑上的 DeskMesh 控制台，不会上传或同步。"
      footer="一次性设置链接使用后立即失效。忘记密码时，请从 DeskMesh 托盘菜单重置控制台登录。"
    >
      <form className="auth-form" onSubmit={(event) => void submit(event)}>
        {error ? <p className="auth-error" role="alert">{error}</p> : null}
        <label className="field-label">
          管理员名称
          <input
            ref={usernameInput}
            type="text"
            value={username}
            maxLength={32}
            autoComplete="username"
            required
            onChange={(event) => setUsername(event.target.value)}
          />
        </label>
        <PasswordField
          label="新密码"
          value={password}
          show={showPassword}
          autoComplete="new-password"
          hint={`至少 ${MINIMUM_ADMIN_PASSWORD_LENGTH} 个字符；建议使用更长密码，只在本机验证。`}
          onChange={setPassword}
          onToggle={() => setShowPassword((value) => !value)}
        />
        <PasswordField
          label="确认密码"
          value={confirmation}
          show={showPassword}
          autoComplete="new-password"
          onChange={setConfirmation}
          onToggle={() => setShowPassword((value) => !value)}
        />
        <button className="primary-button full-width" type="submit" disabled={busy}>
          {busy ? "正在创建管理员…" : "创建管理员并进入控制台"}
        </button>
      </form>
    </AuthFrame>
  );
}

function LoginView({
  auth,
  onAuthenticated,
  onRefresh,
}: {
  auth: AuthStatus;
  onAuthenticated: (next: AuthStatus) => void;
  onRefresh: () => Promise<AuthStatus | null>;
}) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [showPassword, setShowPassword] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const usernameInput = useRef<HTMLInputElement>(null);
  const secondsUntilUnlock = useSecondsUntil(auth.lockedUntil);
  const locked = secondsUntilUnlock > 0;

  useAuthDocumentTitle("管理员登录");
  useEffect(() => usernameInput.current?.focus(), []);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (locked) return;
    setBusy(true);
    setError(null);
    try {
      const next = await apiRequest<AuthStatus>(
        "/api/v1/auth/login",
        jsonRequest("POST", { username: username.trim(), password }),
        15_000,
        { notifyAuthFailure: false, skipSessionToken: true },
      );
      setPassword("");
      onAuthenticated(next);
    } catch (caught) {
      const status = caught instanceof ApiError ? caught.status : 0;
      if (status === 401) {
        setError("管理员名称或密码不正确，请重试。");
        await onRefresh();
      } else if (status === 429) {
        setError("尝试次数过多，控制台已暂时锁定。");
        await onRefresh();
      } else if (status === 428 || status === 423) {
        await onRefresh();
      } else {
        setError(authErrorMessage(caught, "登录失败，请确认本机 Agent 正在运行。"));
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <AuthFrame
      eyebrow="仅限本机"
      title="解锁本机控制台"
      description="登录后才能查看设备状态或发出键鼠、剪贴板、文件与显示器操作。"
      footer="控制台始终只监听 127.0.0.1:5616。登录不会开放局域网网页访问，也不会获得 Windows 管理员权限。"
    >
      <form className="auth-form" onSubmit={(event) => void submit(event)}>
        {error ? <p className="auth-error" role="alert">{error}</p> : null}
        {locked ? (
          <p className="auth-lockout">
            尝试次数过多，请在 {secondsUntilUnlock} 秒后重试。
          </p>
        ) : null}
        <label className="field-label">
          管理员名称
          <input
            ref={usernameInput}
            type="text"
            value={username}
            maxLength={32}
            autoComplete="username"
            required
            disabled={busy || locked}
            onChange={(event) => setUsername(event.target.value)}
          />
        </label>
        <PasswordField
          label="管理员密码"
          value={password}
          show={showPassword}
          autoComplete="current-password"
          disabled={busy || locked}
          onChange={setPassword}
          onToggle={() => setShowPassword((value) => !value)}
        />
        {auth.failedAttempts ? (
          <p className="auth-attempt-note">
            最近失败 {auth.failedAttempts} 次；连续失败 5 次将暂时锁定。
          </p>
        ) : null}
        <button
          className="primary-button full-width"
          type="submit"
          disabled={busy || locked || !username.trim() || !password}
        >
          {busy ? "正在验证…" : locked ? "暂时锁定" : "登录"}
        </button>
      </form>
    </AuthFrame>
  );
}

function AuthFrame({
  eyebrow,
  title,
  description,
  footer,
  children,
}: {
  eyebrow: string;
  title: string;
  description: string;
  footer: string;
  children: ReactNode;
}) {
  return (
    <div className="auth-shell">
      <main className="auth-card" aria-labelledby="auth-title">
        <header className="auth-brand">
          <img src="/lanswitch-icon.png" alt="" width="52" height="52" />
          <div>
            <strong>DeskMesh</strong>
            <span>本地控制台</span>
          </div>
        </header>
        <div className="auth-copy">
          <p className="eyebrow">{eyebrow}</p>
          <h1 id="auth-title" tabIndex={-1}>{title}</h1>
          <p>{description}</p>
        </div>
        {children}
        <p className="auth-boundary">{footer}</p>
      </main>
    </div>
  );
}

function PasswordField({
  label,
  value,
  show,
  autoComplete,
  hint,
  disabled = false,
  onChange,
  onToggle,
}: {
  label: string;
  value: string;
  show: boolean;
  autoComplete: "new-password" | "current-password";
  hint?: string;
  disabled?: boolean;
  onChange: (value: string) => void;
  onToggle: () => void;
}) {
  return (
    <label className="field-label">
      {label}
      <span className="password-control">
        <input
          type={show ? "text" : "password"}
          value={value}
          minLength={autoComplete === "new-password" ? MINIMUM_ADMIN_PASSWORD_LENGTH : undefined}
          maxLength={256}
          autoComplete={autoComplete}
          required
          disabled={disabled}
          onChange={(event) => onChange(event.target.value)}
        />
        <button
          type="button"
          disabled={disabled}
          aria-label={show ? `隐藏${label}` : `显示${label}`}
          aria-pressed={show}
          onClick={onToggle}
        >
          {show ? "隐藏" : "显示"}
        </button>
      </span>
      {hint ? <small>{hint}</small> : null}
    </label>
  );
}

function AuthMessage({
  title,
  message,
  tone = "neutral",
  busy = false,
  actionLabel,
  actionDisabled = false,
  onAction,
}: {
  title: string;
  message: string;
  tone?: "neutral" | "danger";
  busy?: boolean;
  actionLabel?: string;
  actionDisabled?: boolean;
  onAction?: () => void;
}) {
  useAuthDocumentTitle(title);
  return (
    <AuthFrame
      eyebrow={tone === "danger" ? "需要本机处理" : "本机访问"}
      title={title}
      description={message}
      footer="DeskMesh 不会通过云服务验证此登录，也不会把管理员凭据发送到已配对设备。"
    >
      <div className={`auth-message ${tone}`} role={tone === "danger" ? "alert" : "status"}>
        {busy ? <span className="auth-spinner" aria-hidden="true" /> : null}
        <span>{busy ? "请稍候，正在读取本机安全状态。" : "控制台尚未解锁，不会启动设备轮询或远程控制。"}</span>
      </div>
      {actionLabel && onAction ? (
        <button
          className="primary-button full-width"
          type="button"
          disabled={actionDisabled}
          onClick={onAction}
        >
          {actionLabel}
        </button>
      ) : null}
    </AuthFrame>
  );
}

function useAuthDocumentTitle(page: string) {
  useEffect(() => {
    document.title = `${page} · DeskMesh`;
  }, [page]);
}

function useSecondsUntil(value?: string | null) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!value || Date.parse(value) <= Date.now()) return;
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, [value]);
  if (!value) return 0;
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) return 0;
  return Math.max(0, Math.ceil((timestamp - now) / 1000));
}

function authErrorMessage(error: unknown, fallback: string) {
  if (error instanceof ApiError && error.status === 0) return fallback;
  return error instanceof Error && error.message ? error.message : fallback;
}

function consumeBootstrapTokenFromUrl() {
  if (typeof window === "undefined") return null;
  const url = new URL(window.location.href);
  let token = url.searchParams.get("bootstrap");
  let returnRoute = url.searchParams.get("return");
  url.searchParams.delete("bootstrap");
  url.searchParams.delete("return");

  const rawHash = url.hash.replace(/^#\/?/, "");
  const queryIndex = rawHash.indexOf("?");
  const route = (queryIndex >= 0 ? rawHash.slice(0, queryIndex) : rawHash).trim();
  if (route === "setup") {
    const hashQuery = new URLSearchParams(
      queryIndex >= 0 ? rawHash.slice(queryIndex + 1) : "",
    );
    token ||= hashQuery.get("bootstrap");
    returnRoute ||= hashQuery.get("return");
    url.hash = consoleRoutes.has(returnRoute || "")
      ? `#/${returnRoute}`
      : "#/overview";
  }

  if (token) {
    window.history.replaceState(
      null,
      "",
      `${url.pathname}${url.search}${url.hash}`,
    );
  }
  return token;
}
