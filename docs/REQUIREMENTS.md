# CalDesk 需求规格说明

> CalDesk —— 一个常驻 Windows 桌面的 CalDAV 日历客户端。
> 本文档基于初始技术方案整理而成，作为产品定义与工程实施的基线。

---

## 0. 文档说明

- **项目命名**：统一使用 **CalDesk**（`Cal`endar + `Desk`）。命名空间 / 程序集 / 文件夹 / EXE 均沿用：`CalDesk.App`、`CalDesk.Core`、`CalDesk.CalDav`、`CalDesk.Storage`、`CalDesk.Tests`、`CalDesk.exe`。
- **目标运行环境**：Windows 10/11 桌面，.NET 10。
- **首发配套服务**：自建 CalDAV（Radicale），日历地址 `https://your-caldav-server.example/path/`，账号 `<your-caldav-user>`。
- **文档关系**：本文是「做什么 / 为什么 / 约束是什么」；逐步「怎么做 / 何时做」见 [ROADMAP.md](./ROADMAP.md)。

---

## 1. 项目概述

CalDesk 是一个 **本地优先（local-first）** 的桌面日历小工具：

- 以无边框窗口（**不透明 `WindowChrome` + DWM 阴影**）常驻桌面，随时可见本周 / 未来七天事项。
- 通过 CalDAV（HTTP/WebDAV）与远端日历双向同步。
- SQLite 离线缓存，断网也能查看与新增；恢复网络后自动补传。
- 轻量、单文件 EXE、开机启动、托盘常驻。

与 Outlook / 系统日历的差异定位：**不追求功能全覆盖**，而追求「贴在桌面上、一眼可见、随手可加」的桌面小工具体验。

---

## 2. 目标用户与使用场景

- **主用户**：单人 / 小团队，已自建 CalDAV（Radicale），需要桌面常驻视图。
- **核心场景**：
  1. 工作时余光扫到本周待办，无需打开浏览器或日历 App。
  2. 临时想起一件事 → 点 `＋` 或托盘 → 30 秒内录入并同步。
  3. 多设备（手机 / 浏览器）修改后，桌面视图几分钟内自动更新。
  4. 断网 / 服务器维护期间仍可查看历史、新增事项，联网后自动补传。

---

## 3. 技术栈

| 层 | 选型 | 说明 |
|---|---|---|
| 语言 / 运行时 | C# / .NET 10 | 单文件 EXE 发布 |
| UI 框架 | **WPF** | 无边框窗口（不透明 `WindowChrome` + DWM 阴影）、三模式（普通/桌面/置顶）、托盘、多显示器、Win32 互操作更成熟；不为简单日历引入 WebView |
| 架构模式 | MVVM | UI 与业务解耦，便于测试与替换 |
| 本地缓存 | SQLite | 本地优先存储，离线可用 |
| iCalendar | Ical.Net | 解析、生成、重复规则展开（RFC 5545）。**注意：它不等于 CalDAV 客户端** |
| CalDAV / WebDAV | `HttpClient` 自行实现 | 不引入重型 WebDAV 库，按需实现 RFC 4791 / RFC 4918 子集 |
| 凭据存储 | Windows Credential Locker | 不落明文 |
| 重复提醒 | 系统通知 / 计划任务 | 见 §13 |

**为何 WPF 而非 WinUI 3**：对「贴在桌面的小工具」场景，WPF 的无边框窗口（`WindowChrome` + DWM 阴影）、窗口 Z-order、托盘生态、Win32 窗口样式操控更直接稳定；WinUI 3 最终也常需操作 HWND。

---

## 4. 产品形态（两个窗口）

### 4.1 桌面周日程窗口（常驻）

常驻桌面右侧（可配置显示器与位置）：

```text
7月13日—7月19日                         ＋

周一 13
  09:30  项目周会
  14:00  客户演示

周二 14
  全天   提交报告
  16:30  牙医

周三 15
  暂无事项
```

支持能力：

- 显示本周或未来七天（可切换）
- 自动隐藏已过去的事项
- 全天事项与定时事项分组（全天置顶）
- 点击事项 → 编辑
- 双击空白区域 → 新增
- 鼠标悬停 → 显示地点、说明、日历名称（Tooltip）
- 窗口不透明（早期透明度滑块已移除）
- 锁定位置（防误拖）
- 多显示器选择、DPI 缩放自适应
- 开机启动
- 托盘图标常驻

### 4.2 快速新增窗口

点右上角 `＋` 或托盘图标弹出：

```text
标题       项目讨论
日期       2026-07-16（默认当日）
时间       14:00 — 15:00（默认下一个整 30 分钟，默认时长 1h 且可在设置中配置）
日历       工作（默认可配置）
地点
提醒       提前 15 分钟（默认可配置）
重复       不重复

[更多选项]                 [保存]
```

### 4.3 设置窗口（次要）

账号连接、默认日历、默认时长 / 提醒、窗口模式、主题（浅色 / 跟随系统）、字号（S/M/L）、开机启动、同步频率、显示器选择、全量重同步等。

---

## 5. 功能需求（Functional Requirements）

> 带 ✅ = MVP 必做；⏸ = 暂缓；❌ = 放弃。需求 ID 形如 `FR-xxx`，供路线图引用。

### 5.1 连接与发现
- `FR-101` ✅ 添加单个 CalDAV 账户（服务器、用户名、密码）。
- `FR-102` ✅ 自动发现：principal URL → calendar-home-set → 枚举日历集合。
- `FR-103` ✅ 兼容用户直接输入完整日历 URL（如 `…/<your-caldav-user>/work/`）的兜底路径。
- `FR-104` ✅ 读取并展示日历名称、颜色、权限。
- `FR-105` ✅ 凭据保存于 Windows Credential Locker，SQLite 仅存引用。
- `FR-106` ⏸ 多账户合并视图。

### 5.2 读取与展示
- `FR-201` ✅ 周视图：本周或未来七天。
- `FR-202` ✅ 全天事项与定时事项分组，全天置顶。
- `FR-203` ✅ 自动隐藏已过去事项。
- `FR-204` ✅ 鼠标悬停显示地点 / 说明 / 日历名称。
- `FR-205` ✅ 月份迷你日历（导航 + 选中日所在周）。
- `FR-206` ✅ 日历颜色按远端定义着色。
- `FR-207` ✅ 离线展示（来自 SQLite 缓存）。

### 5.3 新增 / 编辑 / 删除
- `FR-301` ✅ 快速新增普通事件（标题、日期、时间、日历、地点、提醒、重复）。
- `FR-302` ✅ 全天事件。
- `FR-303` ✅ 编辑事件并回传（带 `If-Match` ETag 防覆盖）。
- `FR-304` ✅ 删除事件（远端 `DELETE` + 本地标记）。
- `FR-305` ✅ 默认值可配置：默认日历、默认时长、默认提醒。
- `FR-306` ⏸ 「修改本次及以后」。
- `FR-307` ❌ 自然语言解析。

### 5.4 重复事件
- `FR-401` ✅ 正确显示已有的重复事项（解析 `RRULE`/`RDATE`/`EXDATE`）。
- `FR-402` ✅ 创建简单的每日 / 每周 / 每月重复。
- `FR-403` ✅ 删除整个系列。
- `FR-404` ✅ 单次例外基础支持（`EXDATE`）。
- `FR-405` ⏸ 复杂重复规则编辑器。
- `FR-406` ⏸ 修改单次实例 / `RECURRENCE-ID`（This-and-future）。
- `FR-407` ⏸ 跨夏令时 / 浮动时间的高级处理。

### 5.5 同步
- `FR-501` ✅ 本地优先：新增先入 SQLite + 立即显示，后台上传。
- `FR-502` ✅ 定时同步（默认每 5 分钟）。
- `FR-503` ✅ 手动刷新立即同步。
- `FR-504` ✅ 基于 `href + ETag` 的变更比较。
- `FR-505` ✅ 冲突提示（本地与远端同时变化时，创建冲突副本，要求用户选择；**不默认 LWW**）。
- `FR-506` ✅ 同步失败显示警告。
- `FR-507` ✅ WebDAV Sync Collection（`sync-token` 增量同步）——**已实现并作为主拉取机制**（`sync-collection` 增量 + 空 token 初拉；不支持 `sync-token` 的服务器回退 `calendar-query`）。

### 5.6 桌面窗口与系统集成
- `FR-601` ✅ 无边框窗口（`WindowStyle=None` + `AllowsTransparency=False` + 不透明 `WindowChrome` + DWM 阴影 / 圆角）。**设计变更**：早期「透明窗口」方案已改为不透明 `WindowChrome`（更稳，支持原生阴影与圆角）。
- `FR-602` ✅ 三种窗口模式：普通窗口 / 桌面模式 / 置顶模式。
- `FR-603` ✅ 位置可锁定。（早期「透明度可调」随透明窗口方案一并移除，窗口现为不透明。）
- `FR-604` ✅ 不出现在 Alt+Tab（`WS_EX_TOOLWINDOW`）、尽量不抢焦点（`WS_EX_NOACTIVATE`）。
- `FR-605` ✅ 多显示器选择、DPI 自适应、位置持久化。
- `FR-606` ✅ 系统托盘图标（显示 / 隐藏、新增、同步、退出）。
- `FR-607` ✅ 开机启动。
- `FR-608` ⏸ 鼠标穿透（`WS_EX_TRANSPARENT`）。
- `FR-609` ⏸ WorkerW 严格嵌入桌面（实验功能，见 §11 风险）。

### 5.7 提醒
- `FR-701` ✅ 事件到期前 N 分钟系统通知（默认可配）。
- `FR-702` ✅ 应用未在前台也能触发（托盘常驻）。

---

## 6. 非功能需求（Non-Functional）

- `NFR-01` **单文件 EXE（framework-dependent）**：发布产物为一个可执行文件，**不打包运行时**（目标机已预装 .NET 10 桌面运行时），体积小、便于分发。
- `NFR-02` **启动快**：冷启动到桌面窗口可见 ≤ 2s（目标）。
- `NFR-03` **低占用**：常驻内存与 CPU 低；同步为后台轻量任务，不阻塞 UI 线程。
- `NFR-04` **数据安全**：密码绝不明文落盘（见 §10）。
- `NFR-05` **数据完整**：保留完整 `icalendar_data`，编辑回传时尽量保留未知 `X-` 属性，不因解析后丢失扩展字段。
- `NFR-06` **可观测**：同步日志 / 错误可查，便于排查与服务器兼容问题。
- `NFR-07` **可测试**：CalDAV / Storage / Recurrence 层可脱离 UI 单测。
- `NFR-08` **稳健性**：网络异常、4xx/5xx、超时不应崩溃，且不破坏本地数据。

---

## 7. 系统架构

```text
┌──────────────────────────┐
│ WPF UI                   │
│ 周视图 / 快速新增 / 设置   │
└────────────┬─────────────┘
             │ MVVM
┌────────────▼─────────────┐
│ Application Services     │
│ EventService             │
│ CalendarService          │
│ ReminderService          │
└────────────┬─────────────┘
             │
┌────────────▼─────────────┐
│ Sync Engine              │
│ 拉取 / 上传 / 冲突处理     │
└──────┬──────────────┬────┘
       │              │
┌──────▼──────┐ ┌─────▼──────────┐
│ SQLite      │ │ CalDAV Client  │
│ 本地缓存     │ │ HTTP/WebDAV    │
└─────────────┘ └────────────────┘
```

**关键约束**：UI 不直接访问服务器。数据流一律 `UI → SQLite → 同步队列 → CalDAV`。

---

## 8. 项目结构

```text
CalDesk/
├─ CalDesk.App              # WPF 主程序（入口、窗口、托盘、开机启动）
│  ├─ Views
│  ├─ ViewModels
│  ├─ Controls
│  └─ Windowing             # Win32 互操作（窗口样式 / Z-order / 多显示器）
├─ CalDesk.Core             # 领域模型与纯逻辑（无 UI、无 IO 依赖）
│  ├─ Models
│  ├─ Services              # 应用服务接口与实现
│  └─ Recurrence            # 重复事件展开与例外
├─ CalDesk.CalDav           # CalDAV / WebDAV 客户端
│  ├─ Discovery             # principal / home-set / 日历枚举
│  ├─ Requests              # PROPFIND / REPORT / PUT / DELETE
│  ├─ Parsing               # XML 体与 iCalendar 互转（基于 Ical.Net）
│  └─ Synchronization       # 同步引擎、ETag 比较、冲突
├─ CalDesk.Storage          # 持久化
│  ├─ SQLite
│  └─ Repositories
└─ CalDesk.Tests            # 单测与集成测试
```

分层依赖方向：`App → Core / CalDav / Storage`；`CalDav / Storage → Core`；`Core` 不依赖三者。

---

## 9. CalDAV 实现规范

> CalDAV = WebDAV 之上的日历协议（RFC 4791）；数据格式为 iCalendar（RFC 5545）。不是简单的 REST JSON API。

### 9.1 首次连接流程

用户只输入「服务器 / 用户名 / 密码」，客户端依次：

1. `PROPFIND /.well-known/caldav` (`Depth: 0`) → 取 `current-user-principal`
2. `PROPFIND` principal → 取 `calendar-home-set`
3. `PROPFIND` home (`Depth: 1`) → 枚举日历集合
4. 读取日历名称、颜色、权限
5. `REPORT calendar-query` 查询本周事件
6. 保存资源 URL 与 ETag

**兜底**：处理服务器重定向、Apple 风格路径、Nextcloud 路径，以及用户直接输入完整日历 URL（首发场景，见 §14）。

### 9.2 查询本周事项（REPORT calendar-query）

```xml
<?xml version="1.0" encoding="utf-8" ?>
<c:calendar-query
    xmlns:d="DAV:"
    xmlns:c="urn:ietf:params:xml:ns:caldav">
  <d:prop>
    <d:getetag />
    <c:calendar-data />
  </d:prop>
  <c:filter>
    <c:comp-filter name="VCALENDAR">
      <c:comp-filter name="VEVENT">
        <c:time-range
          start="20260712T160000Z"
          end="20260719T160000Z" />
      </c:comp-filter>
    </c:comp-filter>
  </c:filter>
</c:calendar-query>
```

**时区注意**：不要直接用本地日期拼 UTC。须先确定：用户系统时区、周起始日、夏令时变化、全天事项的日期边界。

### 9.3 新增事项（PUT）

生成带稳定 UID 的 iCalendar 对象：

```ical
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//CalDesk//Desktop Calendar//ZH-CN
BEGIN:VEVENT
UID:0f52ef1d-41d4-4584-a5c9-f2432785bd84@caldesk
DTSTAMP:20260715T080000Z
DTSTART:20260716T060000Z
DTEND:20260716T070000Z
SUMMARY:项目讨论
END:VEVENT
END:VCALENDAR
```

```http
PUT /calendars/<your-caldav-user>/work/0f52ef1d.ics
Content-Type: text/calendar; charset=utf-8
If-None-Match: *
```

### 9.4 更新 / 删除（乐观并发）

- 更新：`If-Match: "<服务器返回的 ETag>"`，防止覆盖其他设备的修改。
- 删除：`DELETE` 同样带 `If-Match`。

### 9.5 资源 URL 命名

- 资源路径以 UID 派生（如 `<uid>.ics`），保证幂等、可重试。

---

## 10. 凭据安全

**禁止**将 CalDAV 密码写入：`appsettings.json`、SQLite、注册表明文、自定义可逆加密配置文件。

- 使用 **Windows Credential Locker**（Credential Manager）保存。
- SQLite 仅保存引用：`credential_target = "CalDesk:account:42"`。
- 连接时按引用读取，运行期驻留内存，退出即释放。

---

## 11. 同步模型

### 11.1 写入流程

```text
UI → SQLite → 同步队列 → CalDAV
```

新增事件：
1. 立即写入 SQLite
2. UI 立即显示
3. 操作写入 `sync_operations`
4. 后台上传
5. 成功 → 保存资源 URL 与 ETag
6. 失败 → UI 显示同步警告（重试 / 放弃）

### 11.2 数据模型（SQLite）

```sql
calendars
---------
id
account_id
remote_url
display_name
color
ctag
sync_token

events
------
id
calendar_id
uid
remote_href
etag
icalendar_data
start_utc
end_utc
is_all_day
summary
sync_status
deleted_at

sync_operations
---------------
id
event_id
operation
attempt_count
last_error
created_at
```

> **保留完整 `icalendar_data`**：未必支持所有扩展字段。编辑回传时尽量保留未知 `X-` 属性，而非解析后丢弃。

### 11.3 增量策略（已实现）

- **主机制 — WebDAV Sync Collection（`sync-token` 增量）**：首发服务器（Radicale）已确认支持 `sync-collection`（§14.1）。对已存 `sync-token` 的日历走增量；首次用空 token 的 `sync-collection` 全量拉取。仅获取新增 / 修改 / 删除资源，降低大型日历开销。
- **兜底 — `calendar-query`**：当服务器不公告 `sync-token` 时，回退到「过去 30 天 ~ 未来 180 天」范围的 `REPORT calendar-query`（无法探测删除，靠本地 `deleted_at` 兜底）。
- **节奏**：默认每 5 分钟同步；手动刷新立即同步；设置页提供「全量重同步」入口（丢弃 `sync-token` 重新初拉，用于修复不一致）。

### 11.4 冲突处理

```text
本地未修改 + 远端变化   → 接受远端版本
本地已修改 + 远端未变化 → 上传本地版本
本地已修改 + 远端也变化 → 创建冲突副本，要求用户选择
```

**禁止**默认「最后写入者获胜（LWW）」——多设备场景会静默丢数据。

---

## 12. 重复事件（最大难点）

真正耗时的是重复日程而非 CalDAV HTTP。涉及：`RRULE` / `RDATE` / `EXDATE` / `RECURRENCE-ID`、修改单次实例、本次及以后、跨夏令时、浮动时间、全天事件。

**第一版（MVP）范围**：
- 正确显示已有重复事项
- 创建简单的每日 / 每周 / 每月重复
- 删除整个系列
- 单次例外基础支持（`EXDATE`）

**暂缓**：复杂重复编辑、修改本次及以后（`RECURRENCE-ID`）、跨夏令时 / 浮动时间高级处理。

**内部模型须区分**：

```csharp
CalendarEventSeries
EventOccurrence
EventException
```

> ⚠️ 不要把展开后的每次 `occurrence` 当作独立远端事件上传——一个系列在远端是一个带 `RRULE` 的 VEVENT。

---

## 13. 提醒

- 基础提醒：事件开始前 N 分钟系统通知（N 默认 15，可配）。
- 实现：托盘进程内定时器 / 计划任务；应用常驻托盘以保证可触发。
- MVP 不做「跨设备提醒状态同步」。

---

## 14. 针对 Radicale 自建服务的适配

首发配套服务：`https://your-caldav-server.example/path/`（账号 `<your-caldav-user>`，日历 `work`，服务端为 **Radicale**）。

### 14.1 已确认能力（连通性自检，2026-07-15）

| 能力 | 状态 | 证据 |
|---|---|---|
| `.well-known/caldav` 发现 | ✅ | `PROPFIND` 返回 301 重定向 |
| 日历集合识别 | ✅ | `resourcetype` 含 `<C:calendar/>` |
| displayname | ✅ | `工作` |
| 权限 | ✅ | read / write / all（可 PUT/DELETE） |
| CalendarServer `getctag` | ✅ | 返回 ctag（廉价变更探测可用） |
| **WebDAV Sync Collection（`sync-token`）** | ✅ **已支持** | 返回 `http://radicale.org/ns/sync/…` → `FR-507` 可行 |
| 日历颜色 | ✅ | `#ff5f42ff` |
| `REPORT calendar-query` + time-range | ✅ | 正确返回本周事件（带 ETag 与完整 iCalendar） |
| PUT / DELETE / `If-Match`(ETag) | ⏳ 待写测试 | 权限允许；M3 写测试 harness 再验 |
| TLS | ✅ | 正规 CA 签发证书（如 Let's Encrypt），全链下发，`openssl` 验证 `0 (ok)` |

### 14.2 适配要点

1. **直接日历 URL 入口**：首发优先用完整日历 URL（`FR-103`），`well-known` / principal / home-set 链路作为兼容路径。
2. **TLS**：正规 Let's Encrypt 证书（ECDSA，经 ISRG Root X2 交叉签名），**非自签**；链完整、本机根证书齐全，无需「信任自签」开关。若个别客户端报链错误，多半是其根证书库缺 ISRG Root X1/X2（见 §16 排查）。
3. **Basic Auth**：`Authorization: Basic` 头随每次请求发送。
4. **路径形态**：`/<user>/<calendar>/`；资源 `/<user>/<calendar>/<uid>.ics`。`PUT`/`DELETE` 路径据此拼装。
5. **增量同步**：服务端**支持 `sync-token`**，可在 M4 直接采用 WebDAV Sync Collection（`FR-507`）做增量；以 `getctag` 作为廉价变更探测。
6. **时区注意（真实数据）**：该日历同时存在**浮动时间**事件（`DTSTART` 既无 `Z` 也无 `TZID`，如 `20260715T093000`）与 `TZID=Asia/Shanghai` 事件。解析与时间范围换算必须正确处理浮动时间（按本地时间解释），呼应 §12。
7. **首发只对接该单一服务**：稳定后再扩 Nextcloud / 群晖 / Apple 等兼容性（呼应最终建议）。

---

## 15. MVP 范围总览

**纳入 MVP（✅）**：
- 单个 CalDAV 账户（含完整日历 URL 兜底）
- 多个远端日历
- 周列表 + 月份迷你日历
- 日历颜色定义
- 新增 / 编辑 / 删除普通事件
- 全天事件
- 基础重复事件显示
- SQLite 离线缓存
- 5 分钟自动同步
- 托盘图标 + 开机启动
- 无边框桌面窗口（不透明 `WindowChrome`，三模式）
- 基本提醒

**暂缓（⏸）**：CalDAV Scheduling、复杂重复规则编辑、「本次及以后」、VTODO 全兼容、多账户合并、Windows Widgets Provider、鼠标穿透、WorkerW 嵌入。（WebDAV Sync Collection 已实现，不再暂缓。）

**放弃（❌）**：自然语言解析、共享权限管理、会议邀请与参会状态。

---

## 16. 风险与注意

| 风险 | 说明 | 应对 |
|---|---|---|
| WorkerW 嵌入不稳定 | 严格嵌入 `WorkerW` 属非公开 Explorer 技巧，系统更新后可能失效 | 作为实验功能（`FR-609`），产品化优先「普通无边框 + 锁定位置」 |
| LWW 丢数据 | 多设备并发修改 | 默认冲突提示，绝不静默 LWW（`FR-505`） |
| 重复事件复杂度 | RRULE/EXDATE/RECURRENCE-ID 组合爆炸 | MVP 收窄范围，内部模型区分 Series/Occurrence/Exception |
| 时区 / 夏令时 | 直接拼 UTC 会出错 | 以系统时区为基准做范围换算（§9.2） |
| 扩展字段丢失 | 解析—回写循环可能丢 `X-` 属性 | 保留完整 `icalendar_data`，编辑时尽量原地修改（§11.2） |
| Radicale 兼容性 | 自建服务特性差异 | 首发只对接该服务；M1 做连通性自检（§14） |

---

## 17. 最终落地路径（基线策略）

> **C# + WPF 做桌面外壳，SQLite 做本地优先缓存，Ical.Net 处理 iCalendar，自行实现精简 CalDAV 客户端。**

不从完整 RFC 覆盖开始。先针对实际使用的服务器（Radicale）完成 discovery、查询、PUT、DELETE 和 ETag 冲突控制；稳定之后再扩大兼容范围。具体里程碑与验收见 [ROADMAP.md](./ROADMAP.md)。

---

## 18. 实现状态与基线偏差（2026-07-23 快照）

> 本文是产品基线（做什么 / 为什么）。以下为实现期相对基线的**已落地偏差 / 补充**，以代码为准；FR 编号变更见上文对应条目。

- **窗口外观**：从「无边框透明窗口」改为**不透明 `WindowChrome` + DWM 阴影 + 圆角**（`AllowsTransparency=False`）。更稳定、支持原生阴影与圆角，规避透明窗口的渲染 / 焦点问题；窗口级透明度滑块随之移除（`FR-601 / 603`）。
- **同步机制**：WebDAV **`sync-collection` 为主**（`sync-token` 增量 + 空 token 初拉），不支持 `sync-token` 的服务器回退 `calendar-query`。`FR-507` 由「暂缓」转为「已实现」（§11.3）。
- **主题系统**：支持**浅色 / 跟随系统**两套主题（`Palette.Light / Dark.xaml` + `SystemThemeWatcher`），随系统明暗自动切换。
- **字号**：设置中可选 **S / M / L** 三档字号。
- **日历调色板**：设置页可逐日历配色（声明式 12 色板）。
- **DST 安全日期运算**：周 / 月范围按本地零点 ↔ UTC 换算，含夏令时 `IsInvalidTime` 间隙保护（避免重复事件跨夏令时错位）。
- **存储优化**：周 / 月视图用轻量范围查询 + 按需回填重复展开数据，降低大日历开销。
- **全量重同步**：设置页提供「全量重同步」入口（丢弃 `sync-token` 重新初拉，用于修复不一致）。
- **诊断**：`StartupLog` 文件日志（崩溃安全）+ 全局异常处理，便于排障。

里程碑实现进度见 [ROADMAP.md](./ROADMAP.md)。
