# CalDesk 实现路线图

> 由 [REQUIREMENTS.md](./REQUIREMENTS.md) 派生。需求 ID（`FR-xxx` / `NFR-xx`）可在该文档追溯。
> 基线策略：先针对首发服务（radical `https://radi.vectory.cn:18443/mar/work/`）打通 discovery / 查询 / PUT / DELETE / ETag 冲突，稳定后再扩兼容。

---

## 里程碑总览

| 里程碑 | 主题 | 关键产出 | 主要需求 |
|---|---|---|---|
| **M0** | 工程地基 | 解决方案骨架、构建、测试、日志、配置、CI 本地化 | NFR-01/06/07/08 |
| **M1** | CalDAV 连通 | discovery + 完整日历 URL 兜底 + `calendar-query` 拉取 + ETag 落库 | FR-101~106, FR-504 |
| **M2** | 本地缓存 + 周视图 | SQLite schema、Repository、只读周视图（离线可用） | FR-201~207, NFR-05 |
| **M3** | 增删改 + 乐观并发 | PUT/DELETE、`If-Match`、本地优先写、快速新增窗口 | FR-301~305 |
| **M4** | 同步引擎 | 定时 / 手动同步、`sync_operations` 队列、冲突提示、失败警告 | FR-501~506 |
| **M5** | 桌面窗口与系统集成 | 无边框透明、三模式、托盘、开机启动、多显示器、DPI | FR-601~607 |
| **M6** | 重复事件（基础） + 提醒 | 显示 / 创建简单重复 / 删除系列 / EXDATE；到期通知 | FR-401~404, FR-701~702 |
| **M7** | 打磨与发布 | 单文件 EXE、性能、错误处理、首发上线 | NFR-01~08 |

> M1–M4 是「能正确同步数据」的核心；M5 是「桌面小工具体验」核心；M6 为增值。可并行探讨但实施按序以降低返工。

---

## M0 — 工程地基

**目标**：搭好可构建、可测试、可观测的骨架（仅占位实现，打通管线）。

**任务**
- [ ] 建立 `CalDesk.sln` 与五个项目（`App / Core / CalDav / Storage / Tests`），目标框架 .NET 10。
- [ ] 确立分层依赖方向（`App → Core/CalDav/Storage`；`CalDav/Storage → Core`；`Core` 无依赖）。
- [ ] 接入 `Ical.Net`、`Microsoft.Data.Sqlite`、MVVM（`CommunityToolkit.Mvvm` 候选）、日志组件。
- [ ] 统一日志接口（文件 + 调试输出），供同步错误排查（NFR-06）。
- [ ] 配置体系雏形（窗口 / 同步 / 默认值），不含凭据。
- [ ] `.gitignore`、`README` 占位、测试项目骨架与一个贯通的 smoke 测试。

**验收**
- `dotnet build` 全绿；`dotnet test` 可运行（哪怕只有一个 dummy 测试）。
- 五个项目分层清晰，`Core` 不引用任何 IO/UI 包。

---

## M1 — CalDAV 连通（首发核心）

**目标**：能与 radical 拉通一次，把本周事件拉下来并落库（带 ETag）。

**任务**
- [ ] `HttpClient` 封装：BaseAddress、Basic Auth、超时、TLS 信任开关（自建证书，见 §14）、统一错误包装。
- [ ] **Discovery**：`PROPFIND /.well-known/caldav` → principal → calendar-home-set → 枚举日历集合（`FR-102`）。
- [ ] **完整日历 URL 兜底**：直接对用户给定 URL `PROPFIND Depth:1` 校验日历集合（`FR-103`，首发主路径）。
- [ ] 读取日历名称 / 颜色 / 权限（`FR-104`）。
- [ ] `REPORT calendar-query` + time-range，**以系统时区换算 UTC**（`FR-504`，§9.2）。
- [ ] 解析响应：`getetag` + `calendar-data`，落 `calendars` / `events`（保留完整 `icalendar_data`）。
- [ ] 连通性自检脚本 / 集成测试：对 `radi.vectory.cn:18443/mar/work/` 跑通。

**验收**
- 连一次 radical，能枚举出 `work` 日历并取回本周事件。
- ETag、`remote_href`、`start/end_utc`、`is_all_day` 正确入库。
- 时区边界（含全天事项日期边界）无错位。

**风险**：自签名证书 → TLS 信任开关；`sync-collection` 不支持 → 本里程碑不依赖它，仅记录结论供 M4。

---

## M2 — 本地缓存 + 只读周视图

**目标**：脱离服务器也能看，UI 先以只读形态跑起来。

**任务**
- [ ] 固化 SQLite schema（`calendars / events / sync_operations`，§11.2）与迁移机制。
- [ ] Repository 层：按周范围查询、按日历分组、全天/定时分组（`FR-202`）。
- [ ] 重复事件**展示**先用 Ical.Net 在窗口内展开（写入与编辑留到 M6）。
- [ ] 周视图 ViewModel + View：本周 / 未来七天切换、自动隐藏已过去事项（`FR-201/203`）。
- [ ] 月份迷你日历（导航 + 选中日定位周，`FR-205`）。
- [ ] Tooltip：地点 / 说明 / 日历名称（`FR-204`）；日历着色（`FR-206`）。
- [ ] 断网只读：UI 仅读 SQLite（`FR-207`）。

**验收**
- 断网启动仍能显示缓存中的本周事项（含重复展开）。
- 全天置顶、过期隐藏、颜色正确。

---

## M3 — 增删改 + 乐观并发

**目标**：能在桌面录入并安全回传，不覆盖他人修改。

**任务**
- [ ] 快速新增窗口：标题 / 日期 / 时间（默认下一整 30 分钟）/ 日历 / 地点 / 提醒 / 重复（重复逻辑 M6，此处先「不重复」）（`FR-301`）。
- [ ] 全天事件支持（`FR-302`）。
- [ ] 生成带稳定 UID 的 VEVENT（`PRODID -//CalDesk//…`），`PUT …/<uid>.ics` + `If-None-Match: *`（§9.3）。
- [ ] 编辑回传：`If-Match: "<ETag>"`（`FR-303`）。
- [ ] 删除：`DELETE` + 本地 `deleted_at` 标记（`FR-304`）。
- [ ] 本地优先写：先入 SQLite + UI 立即显示 + 写 `sync_operations`（`FR-501`）。
- [ ] 默认值配置：默认日历 / 时长 / 提醒（`FR-305`）。
- [ ] 回传时保留未知的 `X-` 属性（在原 `icalendar_data` 上修改，NFR-05）。

**验收**
- 新增 → 几乎立即可见 → 后台 PUT 成功 → ETag 回写。
- 编辑后服务器侧 UID 不变、未丢字段；并发覆盖被 `If-Match` 拒绝（412）。
- 删除在远端确认后才最终清理本地。

---

## M4 — 同步引擎

**目标**：自动化、可重试、冲突可见。

**任务**
- [ ] 定时同步（默认 5 分钟）+ 手动刷新（`FR-502/503`）。
- [ ] `sync_operations` 队列处理：重试、退避、`attempt_count`、`last_error`。
- [ ] 远端 → 本地：`href + ETag` 比较，按变化更新（`FR-504`）。
- [ ] 冲突处理：本地已改 + 远端也变 → 创建冲突副本并提示用户选择（**不 LWW**，`FR-505`，§11.4）。
- [ ] 失败 UI 警告（同步状态角标 / 托盘提示，`FR-506`）。
- [ ] 决策并记录 `sync-token` 策略：若 radical 支持 → 第二版增量；否则长期 ETag 轮询（`FR-507` 评估）。

**验收**
- 手机端改一个事件，5 分钟内桌面视图更新。
- 桌面与手机同时改同一事件 → 出现冲突提示而非静默覆盖。
- 断网期间新增，恢复后自动补传成功。

---

## M5 — 桌面窗口与系统集成

**目标**：真正「贴在桌面上」的常驻体验。

**任务**
- [ ] 无边框透明窗口（`WindowStyle=None` + `AllowsTransparency=True` + `ShowInTaskbar=False`，§4.1）。
- [ ] Win32 样式：`WS_EX_TOOLWINDOW`（不进 Alt+Tab）、`WS_EX_NOACTIVATE`（不抢焦点）（`FR-604`）。
- [ ] 三模式：普通窗口 / 桌面模式 / 置顶模式（`SetWindowPos` 控制 Z-order）（`FR-602`）。
- [ ] 透明度可调、位置锁定（`FR-603`）。
- [ ] 多显示器选择 + DPI 自适应 + 位置持久化（按显示器 ID）（`FR-605`）。
- [ ] 系统托盘：显示/隐藏、新增、立即同步、退出（`FR-606`）。
- [ ] 开机启动（注册表 / 启动文件夹，单文件 EXE 友好）（`FR-607`）。
- [ ] 设置窗口：账号、默认值、窗口模式、透明度、同步频率、显示器。

**验收**
- 重启系统后窗口自动出现在上次显示器 / 位置，透明度与模式保留。
- 点击事项不抢焦点过度；拖动 / 锁定符合预期。
- 托盘菜单覆盖核心动作。

**风险**：WorkerW 嵌入留作实验功能（`FR-609`，§16），MVP 用「普通无边框 + 锁定」。

---

## M6 — 重复事件（基础）+ 提醒

**目标**：处理最常见的重复，避免把 occurrence 当独立事件上传。

**任务**
- [ ] 内部模型：`CalendarEventSeries` / `EventOccurrence` / `EventException`（§12）。
- [ ] 显示已有重复事项（`RRULE` / `RDATE` / `EXDATE`）（`FR-401`）。
- [ ] 创建简单重复：每日 / 每周 / 每月（`FR-402`）。
- [ ] 删除整个系列（`FR-403`）。
- [ ] 单次例外基础支持（`EXDATE`，`FR-404`）。
- [ ] 提醒：到期前 N 分钟系统通知（托盘常驻触发，`FR-701/702`）。

**验收**
- 在快速新增里建一个「每周」事件，服务器侧是**一个**带 `RRULE` 的 VEVENT（不是 52 个）。
- 桌面视图正确展开本周 occurrence。
- 单次例外生效；到期通知按时触发。

**暂缓**：复杂重复编辑、本次及以后（`RECURRENCE-ID`）、跨夏令时/浮动时间高级处理（`FR-405~407`）。

---

## M7 — 打磨与发布

**目标**：单文件 EXE，可日常使用。

**任务**
- [ ] 单文件发布（**framework-dependent**：`PublishSingleFile=true` + `SelfContained=false`，不打包运行时，NFR-01）。命令：`dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`。
- [ ] 启动性能（冷启动 ≤ 2s 目标，NFR-02）；后台同步不阻塞 UI（NFR-03）。
- [ ] 全链路错误处理：网络 / 4xx / 5xx / 超时不崩溃、不损数据（NFR-08）。
- [ ] 日志与诊断面板（NFR-06）。
- [ ] 安装 / 升级体验（便携优先），卸载清理凭据引用。

**验收**
- 单 EXE 在干净 Windows 10/11 上运行；连接 radical、查看、新增、编辑、删除、提醒、托盘、开机启动全通。
- 断网/恢复、并发冲突、重复事件三类场景手测通过。

---

## 跨里程碑的验证原则

- **优先窄验证**：每个里程碑只验证该里程碑的可观察行为（M1=拉取正确、M3=回写不丢字段、M4=冲突不丢数据）。
- **首发服务器为准**：兼容性以 radical 通过为门槛；其它服务器兼容留到稳定后。
- **回归点**：每次改动 SQLite schema / CalDAV 请求 / 重复逻辑后，重跑对应里程碑的验收脚本。
- **不做**：未到 M7 不做全量性能压测；未确认 `sync-collection` 支持前不依赖 `sync-token`。

---

## 范围边界速查

- **MVP 内**：M0–M7 的 ✅ 项。
- **暂缓**：多账户合并、复杂重复、本次及以后、VTODO、WebDAV Sync Collection、鼠标穿透、WorkerW 嵌入、Windows Widgets、CalDAV Scheduling。
- **放弃**：自然语言解析、共享权限、会议邀请与参会状态。
