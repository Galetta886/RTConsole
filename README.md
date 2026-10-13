# RTConsole

RTConsole 是一个面向 Windows 的非官方 RimWorld Together 专用服务器本地管理面板。它负责启动 `RTServer.exe`、接管标准输入输出，并在浏览器中提供日常管理、备份和世界档案切换功能。

> 本项目与 Ludeon Studios 或 RimWorld Together 项目方没有隶属或背书关系。仓库不包含 RimWorld、RimWorld Together 或任何第三方模组及服务器文件。

## 功能

- 启动、停止并监控 RimWorld Together 专用服务器
- 查看在线玩家和服务器日志
- 存档、踢出、封禁、授予或取消管理员权限
- 发送聊天、广播和游戏事件
- 执行快捷命令或自由控制台命令
- 创建、列出和恢复服务器备份
- 将 `Assets` 与 `Configs` 保存为独立世界档案并整体切换
- 重开世界，同时选择是否保留玩家账号
- 在恢复、切换和重开前自动创建安全快照
- 对高风险操作使用 `RESET`、`RESTORE`、`SWITCH`、`DELETE` 和 `NEWWORLD` 确认词

## 安全边界

- 管理面板只监听 `localhost`，不要通过反向代理向局域网或互联网开放。
- 首次运行生成 128 位随机管理令牌；令牌放在 URL 片段和请求头中，不写入日志。
- 同一个安装目录只允许一个 RTConsole 实例，避免打开不能发送命令的“空面板”。
- 管理类 POST 操作按单通道串行执行，避免重复点击让恢复、切换或停服流程相互穿插。
- ZIP 快照会先解压到隔离目录，并验证路径、文件数量、展开体积和必要配置；验证通过后才替换当前世界。
- 每次恢复或切换前都会保存 `PreRestore_*.zip`，失败时尝试自动回滚。
- `rtconsole.json`、日志、档案、备份和世界数据均被 Git 忽略。

更完整的安全说明见 [SECURITY.md](SECURITY.md)。

## 环境要求

- Windows 10/11 或 Windows Server
- .NET Framework 4.x
- RimWorld Together 专用服务器，由使用者自行安装
- 构建时需要系统自带的 64 位 .NET Framework C# 编译器

## 构建

在 PowerShell 中执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

输出位于：

```text
artifacts/app/RTConsole.exe
artifacts/app/panel.html
artifacts/app/rtconsole.example.json
```

## 安装与运行

1. 将 `artifacts/app` 复制到准备好的安装目录。
2. 把 RimWorld Together 服务器目录放在安装目录下并命名为 `Server-win-x64`；也可以首次运行后修改 `rtconsole.json` 中的 `ServerDir`。
3. 双击 `RTConsole.exe`。
4. 程序会创建本地配置、打开浏览器并按配置自动启动服务器。

`ServerDir` 支持相对于 RTConsole 安装目录的路径，例如：

```json
{
  "ServerDir": "..\\RimWorldTogetherServer",
  "Port": 2678,
  "Token": "",
  "AutoStart": true,
  "OpenBrowser": true
}
```

令牌留空时，程序会生成新令牌并写回本地 `rtconsole.json`。

## 测试

先构建，再运行完整测试：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\test\run-all.ps1
```

每个测试都会使用独立临时目录、随机面板端口和随机游戏端口。测试只会停止自己启动的进程，不读取或修改正式服务器配置、档案和世界数据。

测试范围包括：

- 令牌鉴权和命令通道
- 中文玩家名与事件列表解析
- 备份与恢复
- 世界档案保存、切换和删除
- 重开世界与账号保留
- ZIP 路径穿越和不完整快照拒绝
- 相对服务器路径

## 项目结构

```text
src/RTConsole.cs          C# 后端与服务器进程托管
panel.html                本地 Web 管理界面
test/                     隔离烟雾测试和安全回归测试
build.ps1                 无额外包管理器的构建脚本
rtconsole.example.json    示例配置
```

## 许可证

[MIT](LICENSE)

---

RTConsole is an unofficial, local-only Windows Web console for RimWorld Together dedicated servers. It does not distribute the game, the multiplayer server, mods, or user data.
