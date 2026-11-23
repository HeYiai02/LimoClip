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
      "sha256": "小写十六进制 SHA256（64 位）",
      "ed25519Signature": "对 DLL 原始字节的 Ed25519 签名（Base64，64 字节）",
      "extraScripts": []
    }
  ]
}
```

- `platform` 使用 `LiveMonitor.Core.Enums.PlatformType` 的数字值：Bilibili=1、Douyin=2、Douyu=3、Huya=4、Twitch=5、YouTube=6。
- `downloadUrl` 必须是 HTTPS 绝对地址；`dllName` 必须是单个 `.dll` 文件名。
- `minAppVersion` 高于当前客户端版本时，客户端会跳过该插件（不降级应用）。
- 签名对象是 **DLL 原始字节**；校验时先比 SHA256，再用内置公钥做 Ed25519 验签，任一失败即拒绝加载。

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
