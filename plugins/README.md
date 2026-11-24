# LimoClip 平台插件（V0.5-B）

本目录存放平台插件清单 `providers-manifest.json`。客户端启动时会扫描本地插件目录，按清单校验签名后热加载；
设置页「组件与插件更新」也可以检查并安装新版本。

## 文件

| 文件 | 说明 |
| --- | --- |
| `providers-manifest.json` | 插件清单（首次构建插件后由 `scripts/build-plugins.ps1` 生成/更新，与插件 DLL 一起提交） |
| `README.md` | 本文档 |

## 清单格式

```json
{
  "manifestVersion": 1,
  "updatedAt": "2026-11-23T13:00:00Z",
  "plugins": [
    {
      "pluginId": "youtube-dlp",
      "platform": 6,
      "version": "1.0.0",
      "minAppVersion": "0.5.0",
      "dllName": "plugin.dll",
      "downloadUrl": "https://github.com/heyiai02/LimoClip/releases/download/plugin-youtube-dlp-1.0.0/plugin.dll",
      "mirrorDownloadUrl": "https://gitee.com/HeYiai/limoclip-plugins/raw/master/plugins/plugin.dll",
      "sha256": "小写十六进制 SHA256（64 位）",
      "ed25519Signature": "对 DLL 原始字节的 Ed25519 签名（Base64，64 字节）",
      "extraScripts": []
    }
  ]
}
```

- `platform` 使用 `LiveMonitor.Core.Enums.PlatformType` 的数字值：Bilibili=1、Douyin=2、Douyu=3、Huya=4、Twitch=5、YouTube=6。
- `downloadUrl` 必须是 HTTPS 绝对地址；`dllName` 必须是单个 `.dll` 文件名。
- `mirrorDownloadUrl`（可选，方案 B）：国内镜像地址（Gitee Raw）。客户端下载 DLL 时先请求 `downloadUrl`，失败（网络异常/超时/非 2xx）才回退该地址；镜像内容必须与主地址完全一致，客户端仍用同一份 `sha256`/`ed25519Signature` 校验，不降低安全要求。留空表示该插件不配置镜像。
- `minAppVersion` 高于当前客户端版本时，客户端会跳过该插件（不降级应用）。
- 签名对象是 **DLL 原始字节**；校验时先比 SHA256，再用内置公钥做 Ed25519 验签，任一失败即拒绝加载。

## 大陆镜像（方案 B）

- 客户端拉取清单时按顺序尝试：GitHub Raw 主地址 → Gitee Raw 镜像（`PluginTrust.ManifestMirrorUrl`，默认 `https://gitee.com/HeYiai/limoclip-plugins/raw/master/providers-manifest.json`）。任一地址可用即可检查更新。
- 插件 DLL 下载同样支持回退：清单条目的 `mirrorDownloadUrl` 指向 Gitee 上的同字节 DLL；主地址失败时自动使用镜像。
- 发布方需要做的事：
  1. 在 Gitee 建公开仓库（默认 `HeYiai/limoclip-plugins`），只放清单与已签名的小体积 DLL（30KB~150KB），不存完整源码。清单放在仓库根 `providers-manifest.json`；DLL 建议放 `plugins/bin/{pluginId}-{version}-plugin.dll`。
  2. 每次发布插件后，把**同一份** `plugins/providers-manifest.json` 与 DLL 推送到 Gitee 仓库（Gitee 的 `/blob/` 是网页地址，清单与 DLL 都必须用 `/raw/` 直链访问，例如 `https://gitee.com/HeYiai/limoclip-plugins/raw/master/plugins/bin/douyu-1.0.1-plugin.dll`）。
  3. 清单条目加 `mirrorDownloadUrl`：可用 `build-plugins.ps1 -MirrorDownloadUrl <地址>` / `sign-plugin.ps1 -MirrorDownloadUrl <地址>` 自动写入，或手工编辑。`mirrorDownloadUrl` 与主 `downloadUrl` 指向**同一字节**的 DLL，因此 `sha256` / `ed25519Signature` 只需维护一份。
- **不要用 Gitee Release 附件做 DLL 下载地址**：Gitee 对公开仓库的 Release 附件匿名下载会强制跳登录页（返回 HTML 而非 DLL），客户端会 SHA256/验签失败。请统一使用 Gitee Raw 直链。
- 发布后自检：`curl -I <Gitee Raw DLL 地址>` 应返回 `HTTP 200` 且 `Content-Type` 不是 `text/html`；清单地址同样应返回 200 与 JSON 内容。
- 安全边界：镜像只承担「分发通道」，不承担信任；清单与插件仍由 Ed25519 验签，镜像被篡改会导致校验失败而不是被加载。

## 热更新发布检查清单（红线）

1. **单 DLL 依赖限制**：客户端热更新只落盘 `plugin.dll`，不携带第三方依赖。斗鱼/抖音依赖的 `Jint` 使用主程序自带副本；不要在纯热更新里新增主程序没有的 NuGet 包或跨大版本升级 `Jint`，否则必须随主程序发版并抬高 `minAppVersion`。
2. **插件版本必须动态读取**：6 个 `*Plugin.cs` 的 `Version` 均为 `PluginVersionInfo.Resolve(typeof(...))`（读程序集元数据），`build-plugins.ps1 -Version` 通过 `-p:Version` 注入；不要写死版本号，否则客户端会判定不高于内置版本而拒载。
3. **不改 `LiveMonitor.Core` 接口二进制兼容性**：`IPlatformPlugin` / `IPlatformProvider` / `IDanmakuClient` 与核心模型签名变更需随主程序发版，并抬高清单 `minAppVersion`。
4. **Ed25519 私钥不进仓库**：私钥离线保存（建议 U 盘/密码管理器加密备份）；泄露或丢失只能换密钥对并发新版客户端。
5. **只发布单个平台 DLL**：`build-plugins.ps1` 已按单主 DLL 打包；手工发布时切记不要混入 `LiveMonitor.Core.dll` 等共享程序集。

## 签名私钥（重要）

- 生产私钥是 32 字节 Ed25519 seed 的 Base64，**绝不能提交到仓库**，也不要写进脚本/配置文件。
- 推荐通过环境变量提供给签名脚本（当前 PowerShell 会话有效）：

```powershell
$env:LIVEMONITOR_PLUGIN_KEY = '<私钥 Base64>'
```

- 也可以把私钥放到仓库外的本地文件（例如 `%USERPROFILE%\.limoclip\plugin-signing.key`），用 `-PrivateKeyPath` 传入；
  该文件不要放进 `plugins/` 或任何会被提交的目录。
- 对应公钥已内嵌在客户端 `PluginTrust.PublicKeyBase64`：
  `lYn7fkrnrtAHz4fplCQ3R5yC3pPCrG3QLk5atHiSE24=`。
  丢失私钥后无法再签发可被当前客户端接受的更新（只能发布新版客户端换公钥）。

## 构建并签名插件

```powershell
# 1) 构建插件工程 → 签名 → 更新本目录清单（同时复制一份发布用 DLL 到 artifacts/plugins/<pluginId>-<version>/）
.\scripts\build-plugins.ps1 `
  -ProjectPath .\src\LiveMonitor.Plugins.YouTube\LiveMonitor.Plugins.YouTube.csproj `
  -PluginId youtube-dlp `
  -Platform YouTube `
  -Version 1.0.0 `
  -MinAppVersion 0.5.0

# 2) 把 artifacts/plugins/youtube-dlp-1.0.0/plugin.dll 上传到 GitHub Release 资产，
#    下载地址需与清单里的 downloadUrl 一致；然后提交 plugins/providers-manifest.json。
#    若启用大陆镜像：同一 DLL 也提交到 Gitee 仓库，并用 -MirrorDownloadUrl <Gitee Raw 直链> 重新写入清单。
```

只想给已有 DLL 签名、不重新构建：

```powershell
.\scripts\sign-plugin.ps1 -DllPath .\some\plugin.dll -PluginId youtube-dlp -Platform YouTube `
  -Version 1.0.0 -MinAppVersion 0.5.0 -ManifestPath .\plugins\providers-manifest.json
```

签名工具源码在 `scripts/PluginSigner/`（.NET 8 控制台 + BouncyCastle），由脚本自动还原与运行，不需要加入解决方案。

## 客户端本地插件目录

```
%LocalAppData%\LiveMonitor\plugins\
  {pluginId}\
    plugin.dll
    plugin.json   # 与清单条目同结构的 JSON
```

- 启动时扫描该目录：逐个读取 `plugin.json`，验证 `sha256` + 签名后加载；版本不高于内置实现时保留内置。
- 更新安装时先写临时目录、校验通过后再原子替换；有正在进行的录制/弹幕任务时提示「下次启动生效」。
- 平台不可用时客户端自动回退内置实现（B 站、Twitch 内置；YouTube 依赖插件）。
