# Motion Interaction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add restrained WPF interaction motion to EasyGet and verify the program builds and runs.

**Architecture:** Keep navigation and view-model flow unchanged. Add a small `EasyGet.Behaviors.Motion` attached behavior for page-enter animation, then centralize timing/easing resources and style storyboards in `Themes/Generic.xaml`.

**Tech Stack:** .NET 8 WPF, XAML Storyboards, attached dependency properties, xUnit XML contract tests.

---

## File Structure

- Create `Behaviors/Motion.cs`: attached behavior that animates opacity and translate transform when a page root is loaded.
- Modify `Themes/Generic.xaml`: add motion resources and enhance shared button, nav, toggle, window control, and panel styles.
- Modify `MainWindow.xaml`: add page transition host naming and animated toast visibility style.
- Modify `Views/DownloadView.xaml`, `Views/BatchDownloadView.xaml`, `Views/HistoryView.xaml`, `Views/SettingsView.xaml`: add `behaviors:Motion.PageEnter="True"` on each root `UserControl`.
- Modify `EasyGet.Tests/ThemeStyleTests.cs`: add motion resource/style contract tests.
- Modify `EasyGet.Tests/XamlBindingTests.cs`: add page-enter and toast motion contract tests.

### Task 1: Motion Contract Tests

**Files:**
- Modify: `EasyGet.Tests/ThemeStyleTests.cs`
- Modify: `EasyGet.Tests/XamlBindingTests.cs`

- [ ] **Step 1: Write failing tests**

Add tests requiring:

```csharp
[Fact]
public void ThemeDefinesSharedMotionResources()
{
    var document = XDocument.Load(GetThemePath("Generic.xaml"));
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

    Assert.Contains(document.Descendants(), element =>
        element.Name.LocalName == "CubicEase"
        && element.Attribute(x + "Key")?.Value == "MotionEaseOut"
        && element.Attribute("EasingMode")?.Value == "EaseOut");
    Assert.Contains(document.Descendants(), element =>
        element.Name.LocalName == "Duration"
        && element.Attribute(x + "Key")?.Value == "MotionDurationFast"
        && element.Value.Trim() == "0:0:0.15");
}

[Theory]
[InlineData("AccentButton")]
[InlineData("SurfaceButton")]
[InlineData("NavRadioButton")]
public void InteractiveStylesUseMotionStoryboards(string styleKey)
{
    var document = XDocument.Load(GetThemePath("Generic.xaml"));
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

    var style = document.Descendants().FirstOrDefault(element =>
        element.Name.LocalName == "Style"
        && element.Attribute(x + "Key")?.Value == styleKey);

    Assert.NotNull(style);
    Assert.Contains(style!.Descendants(), element => element.Name.LocalName == "Storyboard");
    Assert.Contains(style.Descendants().Attributes("EasingFunction"), attribute =>
        attribute.Value.Contains("MotionEaseOut", StringComparison.Ordinal));
}

[Fact]
public void ToggleSwitchAnimatesThumbWithTransform()
{
    var document = XDocument.Load(GetThemePath("Generic.xaml"));
    XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

    var style = document.Descendants().FirstOrDefault(element =>
        element.Name.LocalName == "Style"
        && element.Attribute(x + "Key")?.Value == "ToggleSwitch");

    Assert.NotNull(style);
    Assert.Contains(style!.Descendants(), element =>
        element.Name.LocalName == "TranslateTransform"
        && element.Attributes().Any(attribute =>
            attribute.Name.LocalName == "Name"
            && attribute.Value == "ThumbTranslate"));
    Assert.Contains(style.Descendants(), element =>
        element.Name.LocalName == "DoubleAnimation"
        && element.Attribute("Storyboard.TargetName")?.Value == "ThumbTranslate"
        && element.Attribute("Storyboard.TargetProperty")?.Value == "X");
}
```

Add tests requiring all page roots to opt into the attached behavior and the toast to use animated visibility:

```csharp
[Theory]
[InlineData("DownloadView.xaml")]
[InlineData("BatchDownloadView.xaml")]
[InlineData("HistoryView.xaml")]
[InlineData("SettingsView.xaml")]
public void PageRootUsesMotionPageEnterBehavior(string viewFileName)
{
    var document = XDocument.Load(GetViewPath(viewFileName));

    Assert.Equal("UserControl", document.Root?.Name.LocalName);
    Assert.Contains(document.Root!.Attributes(), attribute =>
        attribute.Name.LocalName == "Motion.PageEnter"
        && attribute.Value == "True");
}

[Fact]
public void MainWindowToastUsesAnimatedVisibilityStates()
{
    var document = XDocument.Load(GetRootPath("MainWindow.xaml"));

    var toast = document.Descendants().FirstOrDefault(element =>
        element.Name.LocalName == "Border"
        && element.Attributes().Any(attribute =>
            attribute.Name.LocalName == "Name"
            && attribute.Value == "NotificationToast"));

    Assert.NotNull(toast);
    Assert.DoesNotContain(toast!.Attributes("Visibility"), attribute =>
        attribute.Value.Contains("BoolToVisibility", StringComparison.Ordinal));
    Assert.Contains(toast.Descendants(), element =>
        element.Name.LocalName == "DataTrigger"
        && element.Attribute("Value")?.Value == "True");
    Assert.Contains(toast.Descendants(), element =>
        element.Name.LocalName == "DoubleAnimation"
        && element.Attribute("Storyboard.TargetProperty")?.Value == "Opacity");
}
```

- [ ] **Step 2: Run failing tests**

Run:

```powershell
dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter "ThemeDefinesSharedMotionResources|InteractiveStylesUseMotionStoryboards|ToggleSwitchAnimatesThumbWithTransform|PageRootUsesMotionPageEnterBehavior|MainWindowToastUsesAnimatedVisibilityStates"
```

Expected: failures because resources, page behavior attributes, toggle transform animation, and toast animation are not implemented yet.

### Task 2: Page Enter Behavior

**Files:**
- Create: `Behaviors/Motion.cs`
- Modify: four XAML page roots under `Views/`

- [ ] **Step 1: Implement behavior**

Create `EasyGet.Behaviors.Motion` with attached property `PageEnter`. On `Loaded`, ensure a `TranslateTransform` exists, set opacity to 0 and Y to 10, then animate opacity to 1 and Y to 0 with a 160ms `CubicEase`.

- [ ] **Step 2: Attach behavior to page roots**

Add `xmlns:behaviors="clr-namespace:EasyGet.Behaviors"` and `behaviors:Motion.PageEnter="True"` to `DownloadView`, `BatchDownloadView`, `HistoryView`, and `SettingsView`.

- [ ] **Step 3: Run page behavior tests**

Run:

```powershell
dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter "PageRootUsesMotionPageEnterBehavior"
```

Expected: pass.

### Task 3: Shared Style Motion

**Files:**
- Modify: `Themes/Generic.xaml`

- [ ] **Step 1: Add motion resources**

Add `Duration` resources for fast/medium timing and a `CubicEase` named `MotionEaseOut`.

- [ ] **Step 2: Enhance shared styles**

Update:

- `ToolPanelBorder`: add stable `TranslateTransform` so panel hover lift can be applied where the style is used.
- `AccentButton` and `SurfaceButton`: replace raw durations with shared resources and animate border scale/hover overlay with easing.
- `WindowControlButton` and `WindowCloseButton`: add short hover opacity feedback consistent with the main buttons.
- `NavRadioButton`: animate hover overlay, selected indicator opacity/scale, and pressed scale.
- `ToggleSwitch`: replace `HorizontalAlignment` snapping with `ThumbTranslate.X` animations.

- [ ] **Step 3: Run style tests**

Run:

```powershell
dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter "ThemeDefinesSharedMotionResources|InteractiveStylesUseMotionStoryboards|ToggleSwitchAnimatesThumbWithTransform"
```

Expected: pass.

### Task 4: Toast Motion

**Files:**
- Modify: `MainWindow.xaml`

- [ ] **Step 1: Name and restyle toast**

Name the notification border `NotificationToast`. Keep it in the visual tree with `Opacity="0"`, `IsHitTestVisible="False"`, and a `TranslateTransform` starting at `Y="12"`.

- [ ] **Step 2: Add show/hide animations**

Use a `DataTrigger` on `ShowNotification=True` to animate opacity to 1 and translate Y to 0. Use the default hidden state plus an exit storyboard in `DataTrigger.ExitActions` to fade/slide back to hidden.

- [ ] **Step 3: Run toast test**

Run:

```powershell
dotnet test EasyGet.Tests\EasyGet.Tests.csproj --filter "MainWindowToastUsesAnimatedVisibilityStates"
```

Expected: pass.

### Task 5: Full Verification And Run

**Files:**
- All changed files.

- [ ] **Step 1: Run complete test suite**

Run:

```powershell
dotnet test EasyGet.Tests\EasyGet.Tests.csproj
```

Expected: all tests pass.

- [ ] **Step 2: Run release build**

Run:

```powershell
dotnet build EasyGet.csproj -c Release
```

Expected: exit code 0.

- [ ] **Step 3: Launch application**

Run:

```powershell
Start-Process -FilePath ".\bin\Release\net8.0-windows\EasyGet.exe" -WindowStyle Hidden
```

Expected: process starts without immediate build/runtime crash. Then inspect running process and stop it if needed.

- [ ] **Step 4: Commit implementation**

Stage only code, test, plan, and spec-related files. Do not stage `handoff/` or `stitch_easyget/`.

Commit:

```powershell
git commit -m "增强界面交互动效"
```
