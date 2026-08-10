import { type FormEvent, useState } from "react";
import {
  ApiError,
  apiRequest,
  type AuthStatus,
  type ConnectionState,
  jsonRequest,
  resetSessionToken,
  type SessionRevokeResult,
} from "../web/api";
import { useAdminAuth } from "./AuthGate";

type AdminNotice = {
  tone: "success" | "warning" | "danger";
  message: string;
};

export default function AdminPage({
  active,
  connection,
  onNotice,
  onBeforeLock,
}: {
  active: boolean;
  connection: ConnectionState;
  onNotice: (notice: AdminNotice | null) => void;
  onBeforeLock?: () => void | Promise<void>;
}) {
  const { auth, endingSession, lock, updateAuth } = useAdminAuth();
  const [currentPassword, setCurrentPassword] = useState("");
  const [newPassword, setNewPassword] = useState("");
  const [passwordConfirmation, setPasswordConfirmation] = useState("");
  const [showPasswords, setShowPasswords] = useState(false);
  const [passwordBusy, setPasswordBusy] = useState(false);
  const [passwordError, setPasswordError] = useState<string | null>(null);
  const [passwordSuccess, setPasswordSuccess] = useState<string | null>(null);
  const [sessionsBusy, setSessionsBusy] = useState(false);

  const activeSessionCount = Math.max(1, auth.activeSessionCount ?? 1);

  async function changePassword(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setPasswordError(null);
    setPasswordSuccess(null);
    if (newPassword.length < 12) {
      setPasswordError("新密码至少需要 12 个字符。");
      return;
    }
    if (newPassword !== passwordConfirmation) {
      setPasswordError("两次输入的新密码不一致。");
      return;
    }
    if (newPassword === currentPassword) {
      setPasswordError("新密码不能与当前密码相同。");
      return;
    }

    setPasswordBusy(true);
    try {
      const next = await apiRequest<AuthStatus>(
        "/api/v1/auth/password",
        jsonRequest("POST", { currentPassword, newPassword }),
        15_000,
      );
      resetSessionToken();
      updateAuth(next);
      setCurrentPassword("");
      setNewPassword("");
      setPasswordConfirmation("");
      setPasswordSuccess("管理员密码已更新，其他登录会话已失效。");
      onNotice({
        tone: "success",
        message: "管理员密码已更新；本机当前会话已安全轮换。",
      });
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        setPasswordError("当前密码不正确，请重新输入。");
      } else {
        setPasswordError(adminErrorMessage(error, "无法修改管理员密码。"));
      }
    } finally {
      setPasswordBusy(false);
    }
  }

  async function revokeOtherSessions() {
    setSessionsBusy(true);
    onNotice(null);
    try {
      const result = await apiRequest<SessionRevokeResult>(
        "/api/v1/auth/sessions/revoke",
        jsonRequest("POST", { includeCurrent: false }),
      );
      updateAuth(result.auth);
      onNotice({
        tone: "success",
        message:
          (result.revoked ?? 0) > 0
            ? `已撤销 ${result.revoked} 个其他管理员会话。`
            : "没有需要撤销的其他管理员会话。",
      });
    } catch (error) {
      onNotice({
        tone: "danger",
        message: adminErrorMessage(error, "无法撤销其他管理员会话。"),
      });
    } finally {
      setSessionsBusy(false);
    }
  }

  async function lockConsole() {
    onNotice(null);
    try {
      await onBeforeLock?.();
      await lock();
    } catch (error) {
      onNotice({
        tone: "danger",
        message: adminErrorMessage(error, "无法锁定本机控制台。"),
      });
    }
  }

  return (
    <section
      id="admin"
      className="content-section page-view"
      aria-labelledby="admin-title"
      hidden={!active}
    >
      <div className="section-heading">
        <div>
          <p className="eyebrow">管理员设置</p>
          <h2 id="admin-title">只管理这台电脑</h2>
        </div>
        <p>
          登录保护本机网页控制台；它不会改变 Windows 权限，也不会把控制端口开放到局域网。
        </p>
      </div>

      <div className="admin-grid">
        <article className="admin-card admin-session-card">
          <header className="admin-card-heading">
            <div>
              <span>账户与会话</span>
              <h3>{auth.username || "本机管理员"}</h3>
            </div>
            <span className="admin-role">{adminRoleLabel(auth.role)}</span>
          </header>
          <dl className="admin-facts">
            <div>
              <dt>当前状态</dt>
              <dd>{connection === "online" ? "已登录并连接 Agent" : "Agent 连接异常"}</dd>
            </div>
            <div>
              <dt>活动会话</dt>
              <dd>{activeSessionCount} 个</dd>
            </div>
            <div>
              <dt>空闲到期</dt>
              <dd>{formatAdminTime(auth.idleExpiresAt)}</dd>
            </div>
            <div>
              <dt>最晚到期</dt>
              <dd>{formatAdminTime(auth.sessionExpiresAt)}</dd>
            </div>
            <div>
              <dt>上次登录</dt>
              <dd>{formatAdminTime(auth.lastLoginAt)}</dd>
            </div>
          </dl>
          <div className="admin-session-actions">
            <button
              type="button"
              className="secondary-button"
              disabled={sessionsBusy || activeSessionCount <= 1}
              onClick={() => void revokeOtherSessions()}
            >
              {sessionsBusy ? "正在撤销…" : "撤销其他会话"}
            </button>
            <button
              type="button"
              className="danger-button"
              disabled={endingSession}
              onClick={() => void lockConsole()}
            >
              {endingSession ? "正在锁定…" : "退出并锁定"}
            </button>
          </div>
          <p className="admin-card-note">
            锁定只结束当前网页登录；Agent、自启动设置和已配对设备不会被删除。
          </p>
        </article>

        <form className="admin-card admin-password-card" onSubmit={(event) => void changePassword(event)}>
          <header className="admin-card-heading">
            <div>
              <span>凭据安全</span>
              <h3>修改管理员密码</h3>
            </div>
          </header>
          <p className="admin-card-copy">
            修改成功后将轮换当前会话，并立即撤销其他浏览器中的登录。
          </p>
          {passwordError ? <p className="auth-error" role="alert">{passwordError}</p> : null}
          {passwordSuccess ? <p className="admin-success" role="status">{passwordSuccess}</p> : null}
          <AdminPasswordField
            label="当前密码"
            value={currentPassword}
            show={showPasswords}
            autoComplete="current-password"
            disabled={passwordBusy}
            onChange={setCurrentPassword}
            onToggle={() => setShowPasswords((value) => !value)}
          />
          <AdminPasswordField
            label="新密码"
            value={newPassword}
            show={showPasswords}
            autoComplete="new-password"
            disabled={passwordBusy}
            hint="至少 12 个字符。"
            onChange={setNewPassword}
            onToggle={() => setShowPasswords((value) => !value)}
          />
          <AdminPasswordField
            label="确认新密码"
            value={passwordConfirmation}
            show={showPasswords}
            autoComplete="new-password"
            disabled={passwordBusy}
            onChange={setPasswordConfirmation}
            onToggle={() => setShowPasswords((value) => !value)}
          />
          <button
            type="submit"
            className="primary-button"
            disabled={
              passwordBusy ||
              !currentPassword ||
              !newPassword ||
              !passwordConfirmation
            }
          >
            {passwordBusy ? "正在更新…" : "更新管理员密码"}
          </button>
        </form>

        <article className="admin-card admin-boundary-card">
          <header className="admin-card-heading">
            <div>
              <span>保护范围</span>
              <h3>本机登录与设备信任相互独立</h3>
            </div>
          </header>
          <ul>
            <li>管理员密码只保护 127.0.0.1:5616 上的本机控制台。</li>
            <li>设备之间仍使用一次性配对、mTLS 与证书指纹固定。</li>
            <li>登录不会获得 Windows 管理员权限，也不能越过 UAC、锁屏或登录界面。</li>
            <li>任何密码、会话令牌和一次性设置令牌都不会同步到另一台电脑。</li>
          </ul>
          <p>
            忘记密码或凭据损坏时，请从 DeskMesh 托盘菜单重置控制台登录；网页不会提供匿名重置入口。
          </p>
        </article>
      </div>
    </section>
  );
}

function AdminPasswordField({
  label,
  value,
  show,
  autoComplete,
  disabled,
  hint,
  onChange,
  onToggle,
}: {
  label: string;
  value: string;
  show: boolean;
  autoComplete: "current-password" | "new-password";
  disabled: boolean;
  hint?: string;
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
          minLength={autoComplete === "new-password" ? 12 : undefined}
          maxLength={256}
          autoComplete={autoComplete}
          disabled={disabled}
          required
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

function adminRoleLabel(role?: string | null) {
  return role?.toLowerCase() === "administrator" ? "本机管理员" : role || "本机管理员";
}

function formatAdminTime(value?: string | null) {
  if (!value) return "未报告";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "未报告";
  return new Intl.DateTimeFormat("zh-CN", {
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
    hour12: false,
  }).format(date);
}

function adminErrorMessage(error: unknown, fallback: string) {
  return error instanceof Error && error.message ? error.message : fallback;
}
