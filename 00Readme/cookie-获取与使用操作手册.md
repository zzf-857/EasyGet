# Cookie 获取与使用操作手册

## 1. 这份笔记是干什么的

很多流媒体平台在下载时都会校验登录状态、风控状态、年龄限制或地区限制。  
这时只靠视频链接往往不够，还需要配合对应平台的 `cookie` 文件。

最适合长期使用的方式就是：

- 一个平台准备一个独立的 `cookie.txt`
- 下载哪个平台，就用哪个平台的 `cookie.txt`
- 过期了就只更新对应平台的 cookie，不影响别的平台

例如：

- `youtubeCookie.txt`
- `douyinCookie.txt`
- `bilibiliCookie.txt`
- `xiaohongshuCookie.txt`

---

## 2. 推荐的文件管理方式

建议单独准备一个 Cookie 文件夹，例如：

```text
D:\Videos\02流媒体视频下载\Cookies
```

推荐这样命名：

```text
youtubeCookie.txt
douyinCookie.txt
bilibiliCookie.txt
xiaohongshuCookie.txt
```

这样做的好处：

- 一眼就知道每个文件属于哪个平台
- 某个平台失效时，只更新那个文件
- 后面程序也更容易做“按平台选择 cookie 文件”

---

## 3. 通用原则

不管哪个平台，获取 Cookie 时都尽量遵守下面这些规则：

1. 先登录目标平台账号
2. 尽量使用无痕模式 / 隐私模式 / InPrivate 模式
3. 在同一个窗口里完成登录和导出
4. 导出后尽量关闭这个无痕窗口
5. 优先导出 `Netscape` 格式的 `cookies.txt`
6. 不要只复制一长串 `SID=xxx; token=xxx` 这种文本
7. 最好保存成独立文件，不要和别的平台混在一起

---

## 4. 获取 Cookie 之前的准备

以 Chrome 或 Edge 为例，建议先做一次基础设置。

### 4.1 安装导出插件

推荐使用：

```text
Get cookies.txt LOCALLY
```

注意：

- 要找带 `LOCALLY` 的版本
- 它导出的格式更适合 `yt-dlp`

### 4.2 允许插件在无痕模式中使用

Chrome：

1. 打开 `chrome://extensions/`
2. 找到 `Get cookies.txt LOCALLY`
3. 点“详细信息”
4. 打开 `允许在无痕模式中使用`

Edge：

1. 打开 `edge://extensions/`
2. 找到 `Get cookies.txt LOCALLY`
3. 点“详细信息”
4. 打开 `在 InPrivate 中允许`

如果这一步没开，无痕窗口里就看不到插件。

---

## 5. YouTube 获取 Cookie 详细流程

这是目前最容易忘、也最容易出错的平台，所以单独记录。

### 5.1 目标

最终拿到一个真正可用的：

```text
youtubeCookie.txt
```

### 5.2 正确操作步骤

1. 先关闭所有已经打开的 YouTube 页面
2. 打开一个新的无痕窗口
3. 在这个无痕窗口里登录你的 YouTube 账号
4. 登录成功后，不要切到别的网站
5. 在同一个窗口、同一个标签页输入：

```text
https://www.youtube.com/robots.txt
```

6. 打开这个页面后，不要下载 `robots.txt` 文件本身
7. 这一步的真正目的，是让浏览器停留在一个更稳定的 YouTube 会话页面
8. 点击右上角插件 `Get cookies.txt LOCALLY`
9. 如果插件有多个选项，优先这样选：

- 先选 `Export`
- 如果还要继续选择格式，就选 `Export As`
- 然后选 `Netscape`
- 不要优先选 `Export All Cookies`

10. 保存文件到：

```text
D:\Videos\02流媒体视频下载\Cookies\youtubeCookie.txt
```

11. 导出完成后，关闭整个无痕窗口

### 5.3 为什么不能只下载 robots.txt

因为 `robots.txt` 文件本身没有用。  
真正有用的是：

- 你在无痕窗口里的登录状态
- 你在这个状态下导出的 `youtubeCookie.txt`

所以关键不是“下载 robots.txt”，而是“访问 robots.txt 后导出 cookie”。

### 5.4 怎么确认导出的文件基本正常

用记事本打开 `youtubeCookie.txt`，第一行通常应该像下面这样：

```text
# Netscape HTTP Cookie File
```

如果不是这个格式，后续工具大概率不能直接识别。

---

## 6. 其他平台如何套用

除了 YouTube，其他平台大多数也能按类似思路处理。

通用模板：

1. 打开目标平台
2. 登录账号
3. 用无痕窗口完成整个过程更稳
4. 在目标平台的页面里导出当前站点 Cookie
5. 保存成独立文件

例如：

### 6.1 抖音

建议文件名：

```text
douyinCookie.txt
```

建议流程：

1. 打开无痕窗口
2. 登录抖音网页版
3. 停留在 `douyin.com` 相关页面
4. 用插件导出当前站点 Cookie
5. 保存为 `douyinCookie.txt`

### 6.2 B 站

建议文件名：

```text
bilibiliCookie.txt
```

建议流程：

1. 打开无痕窗口
2. 登录 B 站
3. 停留在 `bilibili.com` 视频页面
4. 导出当前站点 Cookie
5. 保存为 `bilibiliCookie.txt`

### 6.3 小红书 / 其他平台

思路也是一样：

1. 登录
2. 打开目标平台页面
3. 导出当前站点 Cookie
4. 单独保存

---

## 7. 实际使用建议

以后建议你固定按下面流程操作：

### 方案 A：先准备 Cookie，再下载

适合经常下载的人。

1. 先把常用平台的 Cookie 都准备好
2. 分类保存到：

```text
D:\Videos\02流媒体视频下载\Cookies
```

3. 需要下载时，直接选择对应平台的 Cookie 文件

例如：

- 下载 YouTube 时用 `youtubeCookie.txt`
- 下载抖音时用 `douyinCookie.txt`
- 下载 B 站时用 `bilibiliCookie.txt`

### 方案 B：报错时再补 Cookie

适合偶尔下载的人。

1. 先直接尝试下载
2. 如果平台报需要登录、年龄限制、风控校验
3. 再去补导出该平台 Cookie

---

## 8. 常见错误排查

### 8.1 错误：无痕模式里插件不能用

原因：

- 没有开启“允许在无痕模式中使用”

解决：

- 到扩展管理页面打开无痕权限

### 8.2 错误：导出了 Cookie 但还是不能下载

常见原因：

- Cookie 已过期
- 导出的不是当前站点 Cookie
- 导出的不是 Netscape 格式
- 导出后又继续在浏览器里操作，Cookie 被平台轮换
- 平台风控更严格，需要重新登录后再导出

解决：

1. 重新开无痕窗口
2. 重新登录
3. 重新导出
4. 用新的文件替换旧文件

### 8.3 错误：只有一长串 `key=value; key2=value2`

说明：

- 这不一定是标准 `cookies.txt`
- 某些工具不能直接用

更稳的做法：

- 用插件直接导出 `Netscape cookies.txt`

### 8.4 错误：导出时选了 `Export All Cookies`

说明：

- 会把很多无关网站的 Cookie 一起导出来
- 容易杂乱，也不够安全

建议：

- 优先选当前网站的 `Export`

---

## 9. 我的长期使用规范

建议以后固定这样做：

### 9.1 目录规范

```text
D:\Videos\02流媒体视频下载\Cookies
```

### 9.2 命名规范

```text
youtubeCookie.txt
douyinCookie.txt
bilibiliCookie.txt
xiaohongshuCookie.txt
```

### 9.3 使用规范

- 下载前先判断平台
- 按平台选择对应 Cookie 文件
- 如果下载失败，优先怀疑 Cookie 失效
- 某个平台失效时，只重做那个平台的 Cookie

---

## 10. 一句话总结

以后你只要记住这一套就够了：

```text
一个平台一个 cookie.txt
下载哪个平台，就用哪个平台的 cookie.txt
YouTube 要在无痕窗口登录后，访问 robots.txt，再导出当前站点 Cookie
```

---

## 11. 你这次已经验证成功的文件

本次实际验证成功的 YouTube Cookie 文件是：

```text
C:\Users\admin\AppData\Local\EasyGet\www.youtube.com_cookies.txt
```

后面你可以把它整理或复制为：

```text
D:\Videos\02流媒体视频下载\Cookies\youtubeCookie.txt
```

这样后续就更好找。

