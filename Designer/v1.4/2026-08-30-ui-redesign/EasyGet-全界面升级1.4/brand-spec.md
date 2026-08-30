# EasyGet 品牌规格

来源：`F:\AI\AIMadeupTools\01_DesktopApps\EasyGet` 中的真实实现，取值自
`Themes/Generic.xaml` 的语义色板与 `Assets/app-icon-source.svg` 的图标配色，
非推测值。原始 hex 已转换为 OKLch。

## 六个基础令牌

```css
--bg:      oklch(18.5% 0.012 285);  /* BgChrome  #121218 */
--surface: oklch(23.9% 0.017 285);  /* BgSurface #1E1E27 */
--fg:      oklch(95.3% 0.007 277);  /* TextPrimary #EEEFF4 */
--muted:   oklch(66%   0.018 275);  /* TextSecondary/Muted 区间，提亮至 4.5:1 */
--border:  oklch(29%   0.020 285);  /* BorderPrimary #2A2A35 */
--accent:  oklch(69.5% 0.161 259);  /* Accent #5B9CFF */
```

状态色同样来自 XAML：成功 `#4BB966` → `oklch(70.2% 0.156 149)`，
警告 `#E0B54F` → `oklch(79.3% 0.129 86)`，错误 `#E5605A` → `oklch(65.6% 0.167 25)`，
危险 `#C0392F` → `oklch(54.4% 0.173 28)`。

## 字体栈

- Display：`'Segoe UI Variable Display'` → 系统中文黑体。Windows 原生显示字重，
  标题用 -0.02em 字距。
- Body：`'Segoe UI Variable Text'` → 同族正文光学尺寸。
- Mono：`'Cascadia Code'` / `JetBrains Mono`，用于速度、大小、进度、版本号。

数据密集型桌面工具，因此显示与正文使用同族的两个光学尺寸而非两个字族。

## 观察到的视觉规则

1. **深色工作台**：应用只有深色主题，chrome 比内容面板更暗（侧栏 `#14141A` <
   窗口 `#121218` < 面板 `#1E1E27`），层级靠明度而非阴影建立。
2. **强调色只承担主操作**：`AccentBrush` 只用于主按钮、选中态、活动进度；
   其余状态交给成功/警告/错误三色。
3. **主题色可由用户切换**：`App.xaml.cs` 调用 `ThemeManager.ApplyTheme(ThemeColor)`，
   因此 `--accent` 必须是运行时可替换的单一变量。
4. **危险操作独立配色**：`Destructive` 与 `Error` 分开定义，删除类操作二次确认。
5. **圆润几何**：应用图标为 54/256 圆角的 squircle，托盘与箭头均为全圆端，
   界面沿用同一圆润语言（容器 13–24px，控件 10–999px）。
