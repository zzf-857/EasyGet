# EasyGet 设计效果图归档

本目录集中保存项目中的界面设计参考、版本截图、评审图和视觉验证图。目录名采用
`vX.Y/YYYY-MM-DD-topic`，先按发布大版本线，再按素材生成日期排序。

## 目录

```text
Designer/
├─ v1.0/
│  ├─ 2026-06-09-stitch-reference/
│  ├─ 2026-06-09-qa/
│  ├─ 2026-06-10-legacy-screenshots/
│  ├─ 2026-06-10-visual-check/
│  ├─ 2026-06-11-readme-screenshots/
│  └─ 2026-06-11-uiux-v2/
├─ v1.2/
│  └─ 2026-07-14-smart-cookie/
├─ v1.3/
   └─ 2026-07-27-v1.3.10-desktop-redesign/
      ├─ prototype/
      ├─ runtime/
      └─ review/
└─ v1.4/
   └─ 2026-08-30-ui-redesign/
      └─ EasyGet-全界面升级1.4.zip
```

## 版本依据

- `v1.0`：2026-06-09 至 2026-06-11 的 Stitch 参考、旧版界面、UI/UX 过程图、README 图和视觉检查图。
- `v1.2`：2026-07-14 的智能 Cookie 验收图。截图当时显示 v1.1.8，功能随后进入 v1.2.0-v1.2.2，因此按功能迭代线归档。
- `v1.3`：2026-07-27 的桌面端重设计素材。原型截图基于 v1.3.9，次日的发布提交完成 v1.3.10 重构。
- `v1.4`：2026-08-30 的全界面升级交付包，包含 HTML 原型、品牌规格和配套效果图。

版本判断以提交时间、发布记录和素材来源为准，不以 Stitch 图中的占位版本文字为准。

## 归档说明

- `Assets/app.png`、`Assets/app.ico` 和 `Assets/app-icon-source.svg` 是运行时品牌图标，不属于效果图，保留在 `Assets`。
- `.worktrees` 下的图片属于其他工作树副本，不重复归档。
- `2026-06-09-qa`、`2026-06-10-visual-check` 和 `v1.3` 重设计目录来自原先被忽略的本地 QA/原型输出，仍在 `.gitignore` 中保持本地归档属性。
- `UX-001-download-status.png` 与 `UX-101-theme-tokens.png` 内容重复，但保留两份以对应各自的任务记录。
- `UX-202` 及之后的部分 UI/UX 图是概念或非真实产品截图，只用于历史过程记录，不代表产品当前界面。
- `easyget-current-window-test.png` 含有窗口遮挡，仅作为 QA 记录，不应当用作对外展示图。
- 部分历史文件虽使用 `.png` 扩展名，实际编码为 JPEG；为保持历史路径和哈希不变，本次未改动扩展名。

Stitch 的 `.txt` 源稿、v1.3 的 HTML 原型和 v1.4 的设计交付包均与对应版本归档，后续新增素材请沿用上述目录格式。
