import { ArrowArcLeftIcon } from "@phosphor-icons/react/dist/csr/ArrowArcLeft";
import { ArrowRightIcon } from "@phosphor-icons/react/dist/csr/ArrowRight";
import { CheckCircleIcon } from "@phosphor-icons/react/dist/csr/CheckCircle";
import { ClockIcon } from "@phosphor-icons/react/dist/csr/Clock";
import { DesktopTowerIcon } from "@phosphor-icons/react/dist/csr/DesktopTower";
import { MonitorPlayIcon } from "@phosphor-icons/react/dist/csr/MonitorPlay";
import { NetworkIcon } from "@phosphor-icons/react/dist/csr/Network";
import { ShieldCheckIcon } from "@phosphor-icons/react/dist/csr/ShieldCheck";
import { UserCircleIcon } from "@phosphor-icons/react/dist/csr/UserCircle";
import { WarningCircleIcon } from "@phosphor-icons/react/dist/csr/WarningCircle";
import type {
  ConnectionState,
  DiagnosticLog,
  PeerSummary,
  SwitchMode,
} from "../web/api";

type TargetOption = Pick<PeerSummary, "id" | "name" | "address" | "online">;

type QuietRelayOverviewProps = {
  connection: ConnectionState;
  localName: string;
  localRole: string;
  localNetwork: string;
  localUptime: string;
  adminName: string;
  target: PeerSummary | null;
  targetOptions: TargetOption[];
  targetActive: boolean;
  switchMode: SwitchMode;
  primaryBusy: boolean;
  primaryDisabled: boolean;
  remoteDesktopReady: boolean;
  eventsLive: boolean;
  events: DiagnosticLog[];
  onTargetChange: (targetId: string) => void;
  onPrimary: () => void;
  onRelease: () => void;
  onOpenRemote: () => void;
  onOpenDevices: () => void;
  onOpenDiagnostics: () => void;
};

export default function QuietRelayOverview({
  connection,
  localName,
  localRole,
  localNetwork,
  localUptime,
  adminName,
  target,
  targetOptions,
  targetActive,
  switchMode,
  primaryBusy,
  primaryDisabled,
  remoteDesktopReady,
  eventsLive,
  events,
  onTargetChange,
  onPrimary,
  onRelease,
  onOpenRemote,
  onOpenDevices,
  onOpenDiagnostics,
}: QuietRelayOverviewProps) {
  const localOnline = connection === "online";
  const primaryLabel = primaryBusy
    ? switchMode === "seamlessRemote"
      ? "正在打开画面…"
      : "正在连接…"
    : targetActive
      ? "当前控制目标"
      : switchMode === "seamlessRemote"
        ? "无缝打开远端"
        : "连接并切换";
  const recentEvents = events.slice(0, 4);

  return (
    <div className="quiet-overview">
      <div className="control-stage">
        <article className="relay-device-card is-local">
          <div className="relay-card-label">本机</div>
          <div className="relay-card-heading">
            <div>
              <h2>{localName}</h2>
              <span className={`relay-availability ${localOnline ? "is-online" : "is-offline"}`}>
                {localOnline ? "在线" : "等待 Agent"}
              </span>
            </div>
            <span className="relay-device-icon" aria-hidden="true">
              <DesktopTowerIcon size={24} weight="duotone" />
            </span>
          </div>

          <dl className="relay-facts">
            <div>
              <DesktopTowerIcon size={20} aria-hidden="true" />
              <dt>设备角色</dt>
              <dd>{localRole}</dd>
            </div>
            <div>
              <NetworkIcon size={20} aria-hidden="true" />
              <dt>网络</dt>
              <dd>{localNetwork}</dd>
            </div>
            <div>
              <ClockIcon size={20} aria-hidden="true" />
              <dt>运行时间</dt>
              <dd>{localUptime}</dd>
            </div>
            <div>
              <UserCircleIcon size={20} aria-hidden="true" />
              <dt>控制者</dt>
              <dd>{adminName || "本机管理员"}</dd>
            </div>
          </dl>
        </article>

        <div className="relay-actions" aria-label="控制切换操作">
          <div className="relay-path" aria-hidden="true">
            <span className="relay-orb">
              <ArrowRightIcon size={30} weight="bold" />
            </span>
          </div>

          <button
            className="relay-primary-action"
            type="button"
            disabled={primaryDisabled || primaryBusy || targetActive}
            onClick={onPrimary}
          >
            <strong>{primaryLabel}</strong>
            <small>
              {switchMode === "seamlessRemote"
                ? "不切显示器输入，通过局域网查看与控制"
                : "切换键鼠；已校准时同步切换显示器"}
            </small>
          </button>

          <button
            className="relay-secondary-action"
            type="button"
            disabled={!target || !remoteDesktopReady}
            onClick={onOpenRemote}
          >
            <MonitorPlayIcon size={22} aria-hidden="true" />
            <span>
              <strong>打开远程桌面</strong>
              <small>
                {remoteDesktopReady
                  ? "查看画面、音频与会话设置"
                  : "目标尚未提供远程桌面能力"}
              </small>
            </span>
          </button>

          <button
            className="relay-keep-local"
            type="button"
            disabled={!targetActive}
            onClick={onRelease}
          >
            <ArrowArcLeftIcon size={18} aria-hidden="true" />
            <span>{targetActive ? "返回本机" : "保持本机"}</span>
          </button>
        </div>

        <article className="relay-device-card is-target">
          {target ? (
            <>
              <div className="relay-target-topline">
                <span className="relay-card-label">目标设备</span>
                {targetOptions.length > 1 ? (
                  <label className="relay-target-picker">
                    <span className="visually-hidden">选择目标设备</span>
                    <select
                      value={target.id}
                      onChange={(event) => onTargetChange(event.target.value)}
                    >
                      {targetOptions.map((option) => (
                        <option key={option.id} value={option.id}>
                          {option.name || option.address || option.id}
                        </option>
                      ))}
                    </select>
                  </label>
                ) : null}
              </div>
              <div className="relay-card-heading">
                <div>
                  <h2>{target.name || "未命名设备"}</h2>
                  <span className={`relay-availability ${target.online ? "is-online" : "is-offline"}`}>
                    {target.online ? "在线" : "离线"}
                  </span>
                </div>
                <span className="relay-device-icon" aria-hidden="true">
                  <DesktopTowerIcon size={24} weight="duotone" />
                </span>
              </div>

              <dl className="relay-facts">
                <div>
                  <NetworkIcon size={20} aria-hidden="true" />
                  <dt>局域网地址</dt>
                  <dd>{target.address || "由 Agent 自动发现"}</dd>
                </div>
                <div>
                  <ClockIcon size={20} aria-hidden="true" />
                  <dt>网络延迟</dt>
                  <dd>{target.online && target.latencyMs != null ? `${target.latencyMs} ms` : "—"}</dd>
                </div>
                <div>
                  <ShieldCheckIcon size={20} aria-hidden="true" />
                  <dt>信任状态</dt>
                  <dd>{target.paired === false ? "尚未配对" : "已配对并固定证书"}</dd>
                </div>
                <div>
                  <UserCircleIcon size={20} aria-hidden="true" />
                  <dt>最后出现</dt>
                  <dd>{formatRelativeTime(target.lastSeen)}</dd>
                </div>
              </dl>
            </>
          ) : (
            <div className="relay-empty-target">
              <span className="relay-device-icon" aria-hidden="true">
                <DesktopTowerIcon size={28} weight="duotone" />
              </span>
              <div>
                <span className="relay-card-label">目标设备</span>
                <h2>尚未选择远端设备</h2>
                <p>先配对同一局域网中的另一台电脑，再从这里快速切换。</p>
              </div>
              <button className="secondary-button" type="button" onClick={onOpenDevices}>
                查看设备
              </button>
            </div>
          )}
        </article>
      </div>

      <section className="recent-events" aria-labelledby="recent-events-title">
        <div className="recent-events-heading">
          <h2 id="recent-events-title">最近事件</h2>
          <div>
            <span className={`event-channel-state ${eventsLive ? "is-live" : "is-polling"}`}>
              {eventsLive ? "实时" : "轮询"}
            </span>
            <button className="text-button" type="button" onClick={onOpenDiagnostics}>
              查看全部
            </button>
          </div>
        </div>
        <div className="recent-events-grid">
          {recentEvents.length > 0 ? (
            recentEvents.map((entry) => {
              const EventIcon = entry.level === "success"
                ? CheckCircleIcon
                : entry.level === "warning" || entry.level === "error"
                  ? WarningCircleIcon
                  : ArrowRightIcon;
              return (
                <article className={`recent-event is-${entry.level}`} key={entry.id}>
                  <span className="recent-event-icon" aria-hidden="true">
                    <EventIcon size={21} weight={entry.level === "success" ? "fill" : "regular"} />
                  </span>
                  <div>
                    <strong>{entry.source || "DeskMesh"}</strong>
                    <p>{entry.message}</p>
                    <time dateTime={entry.at}>{formatEventTime(entry.at)}</time>
                  </div>
                </article>
              );
            })
          ) : (
            <div className="recent-events-empty">
              <CheckCircleIcon size={22} aria-hidden="true" />
              <span>连接 Agent 后，最近的切换、输入和网络事件会显示在这里。</span>
            </div>
          )}
        </div>
      </section>
    </div>
  );
}

function formatEventTime(value?: string) {
  if (!value) return "刚刚";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return new Intl.DateTimeFormat("zh-CN", {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
  }).format(date);
}

function formatRelativeTime(value?: string) {
  if (!value) return "尚无记录";
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  const elapsedSeconds = Math.max(0, Math.floor((Date.now() - date.getTime()) / 1000));
  if (elapsedSeconds < 60) return "刚刚";
  if (elapsedSeconds < 3600) return `${Math.floor(elapsedSeconds / 60)} 分钟前`;
  if (elapsedSeconds < 86_400) return `${Math.floor(elapsedSeconds / 3600)} 小时前`;
  return new Intl.DateTimeFormat("zh-CN", { month: "numeric", day: "numeric" }).format(date);
}
