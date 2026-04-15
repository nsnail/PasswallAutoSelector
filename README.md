# PasswallAutoSelector

自动为 iStoreOS / OpenWrt 的 Passwall 选择节点：

- 定时检测所有节点的 `Ping` / `TCPing` / `URLTest`
- 仅在三项全通时参与排序
- 自动选择综合延迟最低的节点
- 调用 Passwall `set_node?protocol=tcp&section=<nodeId>` 进行 TCP 节点切换
- 切换后回读 `settings` 页面中的 `TCP Node` 做校验

## 1. 环境要求

- Windows + PowerShell
- .NET 8 SDK 或更高（项目目标框架 `net8.0`）
- 可访问路由器 LuCI 地址（例如 `http://10.4.147.182`）

## 2. 安装与启动

在项目目录执行：

```powershell
dotnet restore
dotnet build
```

首次需要安装 Playwright Chromium 内核：

```powershell
pwsh .\bin\Debug\net8.0\playwright.ps1 install chromium
```

运行：

```powershell
dotnet run
```

## 3. 配置

编辑 `appsettings.json`：

- `NodeListUrl`: 节点页地址（示例：`/cgi-bin/luci/admin/services/passwall/node_list`）
- `Username` / `Password`: LuCI 账号密码
- `IntervalSeconds`: 每轮检测周期（秒）
- `Headless`: `true` 为无头运行，`false` 可看浏览器界面

### 切换确认相关配置

- `VerifyTcpNodeAfterSwitch`: 是否在切换后校验 settings 页面 TCP 节点
- `VerifyTimeoutSeconds`: 校验超时时间
- `VerifyPollIntervalSeconds`: 校验轮询间隔

### 逐行测速相关配置

- `UseRowLevelTest`: 是否逐行触发测速
- `RowPingTriggerKeywords` / `RowTcpTriggerKeywords` / `RowUrlTriggerKeywords`: 行内测速按钮关键字
- `RowTestInterClickDelayMs` / `RowTestInterNodeDelayMs`: 逐行点击节流

## 4. 工作流程

每一轮执行：

1. 打开 `NodeListUrl`，若需要则自动登录。
2. 逐行触发 `Ping/TCPing/URLTest`。
3. 读取每行测速结果，筛选三项全通节点。
4. 以 `Ping + TCP + URL` 作为评分，选出最小值。
5. 优先调用：
   - `/cgi-bin/luci/admin/services/passwall/set_node?protocol=tcp&section=<nodeId>`
6. 回读 `/cgi-bin/luci/admin/services/passwall/settings`，校验 `TCP Node` 是否已切换到目标节点。
7. 进入下一轮等待。

## 5. 日志说明

典型日志：

- `开始逐行测速，节点数: ...`
- `已触发节点测速: ... (x/3)`
- `候选最优: ...`
- `已通过 set_node 接口切换 TCP 节点: ...`
- `切换校验通过，settings TCP 节点=...`

如果出现：

- `切换后校验失败`：说明 set_node 后 settings 页未反映目标节点，需要检查路由器插件状态或页面结构变化。

## 6. 安全建议

- 不要把真实密码提交到 Git。
- 推荐仅在局域网环境运行。
- 可将账号密码通过环境变量注入（前缀 `PASSWALL_`），避免明文写入配置文件。

## 7. 免责声明

本工具通过 Web 自动化操作 LuCI 页面与接口。Passwall 版本、主题或页面结构变化可能导致识别规则失效，需要按实际页面调整关键字或解析逻辑。
