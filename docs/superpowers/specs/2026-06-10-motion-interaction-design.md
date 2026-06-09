# EasyGet Interaction Motion Design

## Goal

Upgrade EasyGet's interaction feel with restrained desktop motion that matches the Stitch redesign: fast, quiet, and functional. Motion should make navigation, state changes, and feedback easier to follow without distracting from download tasks.

## Context

The current WPF app already has a Stitch-inspired dark UI and shared styles in `Themes/Generic.xaml`. Existing motion is mostly limited to button hover overlay opacity and pressed scale. Navigation selection, page replacement, toggle switches, and toast notifications still change abruptly.

The exported Stitch references in `stitch_easyget` consistently use:

- 150-300ms interaction durations.
- Subtle active scale around 0.95-0.97.
- Card hover lift around 1-2px.
- Smooth color, opacity, and transform transitions.
- Toggle thumb movement instead of instant alignment changes.

## Approved Direction

Use a restrained desktop motion system:

- Page transitions: fade in plus slight upward slide, about 140-180ms, applied when a new page is presented.
- Navigation: smooth selected indicator, hover background, foreground, and pressed scale.
- Buttons and cards: subtle hover lift or overlay changes, with smooth pressed feedback.
- Toggle switches: animated thumb movement and softened checked-state color transition.
- Toast notifications: slide/fade entrance and exit cues.
- No looping decorative animation except existing functional status pulses where appropriate.

## Architecture

Add a small shared motion layer instead of scattering one-off storyboards.

- Define reusable motion resources in `Themes/Generic.xaml`, such as short/medium durations and a cubic ease-out curve.
- Enhance existing shared styles (`AccentButton`, `SurfaceButton`, `NavRadioButton`, `ToggleSwitch`, and window controls) in place.
- Add a reusable page-enter animation hook for page root elements, preferably through a lightweight attached behavior so each view does not duplicate storyboard details.
- Keep navigation data flow unchanged: `MainViewModel.CurrentPage` remains the source of truth, and the content host only animates presentation.

## Interaction Details

Page transitions should make route changes feel continuous. A newly selected page starts slightly lower and transparent, then settles into place. The previous page does not need a separate outgoing animation because the current `CurrentPage` binding replaces content immediately; a true old-page-out/new-page-in transition is out of scope unless it can be added without changing the view-model navigation contract.

Navigation items should retain their current layout and binding behavior. The active indicator should fade or scale in, selected surfaces should feel softer, and pressed feedback should be short enough to feel responsive.

Primary and secondary buttons should keep their existing hover overlay and pressed scale but use consistent timing and easing. Any hover lift should be very small so dense controls do not jitter visually.

Toggle switches should use a thumb transform so the thumb glides between left and right positions. The checked state should remain instantly understandable even if the animation is interrupted.

Toast notifications should appear from the lower-right area with a short slide/fade and dismiss with a matching fade/slide out. The toast can stay in the visual tree at zero opacity when hidden, with hit testing disabled, so dismissal remains reliable and does not require delaying the view-model state change.

## Accessibility And Performance

Motion must remain brief and nonessential. The UI must remain usable if animations are skipped by the WPF runtime, interrupted by rapid navigation, or disabled through future settings. Avoid expensive blur, layout animation, or repeated shadow animation.

Animations should target transform and opacity where possible. Do not animate dimensions that can cause layout shifts in the sidebar, top bar, download forms, or history grid.

## Testing

Add focused tests around the motion contract where feasible:

- Shared motion resources exist in `Themes/Generic.xaml`.
- Navigation, toggle, and button styles reference the expected transforms or storyboards.
- Page host or page roots include the page-enter animation hook.

Run the existing test suite and release build after implementation:

- `dotnet test EasyGet.Tests\EasyGet.Tests.csproj`
- `dotnet build EasyGet.csproj -c Release`

Manual verification should cover switching between all four pages, hovering and pressing key buttons, toggling settings switches, and observing a download toast.

## Non-Goals

- No large redesign of layout, navigation architecture, or view-model responsibilities.
- No decorative background animation, particle effects, bounce-heavy motion, or long cinematic transitions.
- No dependency on a new animation framework unless WPF built-ins cannot cover the approved scope.
