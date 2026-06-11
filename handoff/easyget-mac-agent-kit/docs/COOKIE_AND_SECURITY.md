# Cookie 与安全策略

## 核心原则

Cookie 是登录凭据，必须当成密码处理。

Agent 只能选择 Cookie profile，不能读取、总结、打印、上传或改写 Cookie 原文。

正确的数据流是：

```text
用户授权或预配置 Cookie
  -> Mac 本地配置 / 浏览器 / 本地 cookies.txt
  -> easyget-agent 工具内部选择
  -> yt-dlp 参数
```

错误的数据流是：

```text
Cookie 原文 -> AI 上下文 -> 工具调用参数
```

## Cookie profile 设计

配置文件示例：

```json
{
  "defaultCookieProfile": "auto",
  "cookieProfiles": {
    "auto": {
      "type": "auto"
    },
    "firefox": {
      "type": "browser",
      "browser": "firefox"
    },
    "chrome": {
      "type": "browser",
      "browser": "chrome"
    },
    "safari": {
      "type": "browser",
      "browser": "safari"
    },
    "youtube_private": {
      "type": "file",
      "path": "~/Library/Application Support/EasyGetAgent/cookies/youtube_private.txt"
    },
    "bilibili_main": {
      "type": "file",
      "path": "~/Library/Application Support/EasyGetAgent/cookies/bilibili_main.txt"
    }
  }
}
```

工具调用只允许这样传：

```json
{
  "url": "https://...",
  "cookieProfile": "youtube_private"
}
```

不允许这样传：

```json
{
  "cookies": "SID=...; SAPISID=..."
}
```

## yt-dlp 参数映射

浏览器 profile：

```bash
--cookies-from-browser firefox
--cookies-from-browser chrome
--cookies-from-browser safari
```

文件 profile：

```bash
--cookies "/Users/me/Library/Application Support/EasyGetAgent/cookies/youtube_private.txt"
```

## 自动策略

`auto` profile 建议按顺序尝试：

1. 无 Cookie，适合公开视频，风险最低。
2. Firefox browser cookies。
3. Safari browser cookies。
4. Chrome browser cookies。
5. 平台专用 file profile。

如果第一次失败，不要无限重试。只对明确的登录态错误重试下一种策略，例如：

- 需要登录
- 年龄验证
- 403
- bot / CAPTCHA 提示
- 平台提示 fresh cookies

## YouTube 特别规则

YouTube Cookie 容易轮换。建议：

1. 普通公开视频默认不用 Cookie。
2. 只有私有列表、年龄限制、会员内容、登录校验时才启用 Cookie。
3. 专门建 `youtube_private` profile，不要复用日常浏览器主账号。
4. 如果导出 cookies.txt，建议使用独立/private session 导出后立刻关闭该 session。
5. 控制下载频率，必要时加入 5-10 秒间隔。

## 日志脱敏

任何日志都必须过滤：

- `Cookie:`
- `Authorization:`
- `SID`
- `HSID`
- `SSID`
- `APISID`
- `SAPISID`
- `__Secure-`
- `LOGIN_INFO`
- `VISITOR_INFO1_LIVE`

日志可以显示：

```text
cookieProfile=firefox
cookieSource=browser
```

不要显示：

```text
--cookies /完整敏感路径
Cookie: SID=...
```

## 权限与合规

工具应该明确只用于下载用户有权访问、平台允许或用户已授权保存的内容。

遇到平台风控、验证码或账号风险提示时，工具应该停止并提示用户手动处理，不要自动绕过。
