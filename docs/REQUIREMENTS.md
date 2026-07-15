# CalDesk 需求规格说明

> CalDesk —— 一个常驻 Windows 桌面的 CalDAV 日历客户端。
> 本文档基于初始技术方案整理而成，作为产品定义与工程实施的基线。

---

## 0. 文档说明

- **项目命名**：统一使用 **CalDesk**（`Cal`endar + `Desk`）。命名空间 / 程序集 / 文件夹 / EXE 均沿用：`CalDesk.App`、`CalDesk.Core`、`CalDesk.CalDav`、`CalDesk.Storage`、`CalDesk.Tests`、`CalDesk.exe`。
- **目标运行环境**：Windows 10/11 桌面，.NET 10。
- **首发配套服务**：自建 CalDAV（radical），日历地址 `https://radi.vectory.cn:18443/mar/work/`，账号 `mar`。
- **文档关系**：本文是「做什么 / 为什么 / 约束是什么」；逐步「怎么做 / 何时做」见 [ROADMAP.md](./ROADMAP.md)。

---

## 1. 项目概述

CalDesk 是一个 **本地优先（local-first）** 的桌面日历小工具：

- 以无边框半透明窗口常驻桌面，随时可见本周 / 未来七天事项。
- 通过 CalDAV（HTTP/WebDAV）与远端日历双向同步。
- SQLite 离线缓存，断网也能查看与新增；恢复网络后自动补传。
- 轻量、单文件 EXE、开机启动、托盘常驻。

与 Outlook / 系统日历的差异定位：**不追求功能全覆盖**，而追求「贴在桌面上、一眼可见、随手可加」的桌面小工具体验。

---

## 2. 目标用户与使用场景

- **主用户**：单人 / 小团队，已自建 CalDAV（radical），需要桌面常驻视图。
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
| UI 框架 | **WPF** | 无边框透明窗口、置顶/置底/穿透、托盘、多显示器、Win32 互操作更成熟；不为简单日历引入 WebView |
| 架构模式 | MVVM | UI 与业务解耦，便于测试与替换 |
| 本地缓存 | SQLite | 本地优先存储，离线可用 |
| iCalendar | Ical.Net | 解析、生成、重复规则展开（RFC 5545）。**注意：它不等于 CalDAV 客户端** |
| CalDAV / WebDAV | `HttpClient` 自行实现 | 不引入重型 WebDAV 库，按需实现 RFC 4791 / RFC 4918 子集 |
| 凭据存储 | Windows Credential Locker | 不落明文 |
| 重复提醒 | 系统通知 / 计划任务 | 见 §13 |

**为何 WPF 而非 WinUI 3**：对「贴在桌面的透明小工具」场景，WPF 的无边框透明窗口、窗口 Z-order、托盘生态、Win32 窗口样式操控更直接稳定；WinUI 3 最终也常需操作 HWND。

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
- 可调透明度
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

账号连接、默认日历、默认时长 / 提醒、窗口模式、透明度、开机启动、同步频率、显示器选择等。

---

## 5. 功能需求（Functional Requirements）

> 带 ✅ = MVP 必做；⏸ = 暂缓；❌ = 放弃。需求 ID 形如 `FR-xxx`，供路线图引用。

### 5.1 连接与发现
- `FR-101` ✅ 添加单个 CalDAV 账户（服务器、用户名、密码）。
- `FR-102` ✅ 自动发现：principal URL → calendar-home-set → 枚举日历集合。
- `FR-103` ✅ 兼容用户直接输入完整日历 URL（如 `…/mar/work/`）的兜底路径。
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
- `FR-507` ⏸ WebDAV Sync Collection（`sync-token` 增量同步）。

### 5.6 桌面窗口与系统集成
- `FR-601` ✅ 无边框透明窗口（`WindowStyle=None` + `AllowsTransparency=True`）。
- `FR-602` ✅ 三种窗口模式：普通窗口 / 桌面模式 / 置顶模式。
- `FR-603` ✅ 透明度可调、位置可锁定。
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

- `NFR-01` **单文件 EXE**：发布产物为一个可执行文件（含依赖），便于分发。
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
PUT /calendars/mar/work/0f52ef1d.ics
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

### 11.3 增量策略

- **第一版（MVP）**：查询「过去 30 天 ~ 未来 180 天」范围；比较 `href + ETag`；每 5 分钟同步；手动刷新立即同步。
- **第二版**：WebDAV Sync Collection（`sync-token` 增量），仅获取新增 / 修改 / 删除资源，降低大型日历开销。
  > ⚠️ 须先确认首发服务器（radical）是否支持 `sync-collection`；不支持则长期沿用 ETag 轮询。

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

## 14. 针对 radical 自建服务的适配

首发配套服务：`https://radi.vectory.cn:18443/mar/work/`（账号 `mar`，日历 `work`）。适配要点：

1. **直接日历 URL 入口优先**：MVP discovery 可失败兜底为用户给定的完整日历 URL（`FR-103`）。即：直接对该 URL 发 `PROPFIND Depth:1` 校验为日历集合，跳过 principal / home-set 链路，降低首发风险。
2. **HTTPS + 自签名 / 自建证书**：客户端须处理 TLS 信任（可选「信任此证书」开关，凭证单独保存），避免证书校验失败阻断。
3. **Basic Auth**：radical 通常使用 HTTP Basic；`Authorization: Basic` 头随每次请求发送。
4. **路径形态**：`/<user>/<calendar>/`；资源 `/<user>/<calendar>/<uid>.ics`。`PUT`/`DELETE` 路径据此拼装。
5. **`sync-collection` 兼容性待确认**：若该服务不支持 WebDAV Sync Collection，长期采用「时间范围 + ETag 轮询」（`FR-504`），不依赖 `sync-token`。
6. **`calendar-query` / ETag 支持确认**：作为 M1 验收前的连通性自检项（见 ROADMAP M1）。
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
- 无边框透明桌面窗口（三模式）
- 基本提醒

**暂缓（⏸）**：CalDAV Scheduling、复杂重复规则编辑、「本次及以后」、VTODO 全兼容、多账户合并、WebDAV Sync Collection、Windows Widgets Provider、鼠标穿透、WorkerW 嵌入。

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
| radical 兼容性 | 自建服务特性差异 | 首发只对接该服务；M1 做连通性自检（§14） |

---

## 17. 最终落地路径（基线策略）

> **C# + WPF 做桌面外壳，SQLite 做本地优先缓存，Ical.Net 处理 iCalendar，自行实现精简 CalDAV 客户端。**

不从完整 RFC 覆盖开始。先针对实际使用的服务器（radical）完成 discovery、查询、PUT、DELETE 和 ETag 冲突控制；稳定之后再扩大兼容范围。具体里程碑与验收见 [ROADMAP.md](./ROADMAP.md)。
