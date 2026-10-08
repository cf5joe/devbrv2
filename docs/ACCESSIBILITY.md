# Accessibility and UI automation

DevBR is required to work with a keyboard only, with a screen reader, in dark, light and Windows
high-contrast themes, and at 100 %, 150 % and 200 % display scaling. This page lists what is checked
automatically and what still needs a person.

## Conventions in the code

- **Names.** Every interactive control has a UI Automation name: from its text, or from
  `AutomationProperties.Name` when its content is an icon plus text, a password box, or a list.
  Lists without item containers (`ItemsControl`) announce each item's `ToString()`, so every row type in
  `src/DevBR.App/ViewModels` overrides it with a readable sentence (never a record dump or type name).
- **Icons.** Icon-font glyphs use `controls:Glyph`, which is hidden from screen readers; the adjacent text
  or the control's name carries the meaning. Status is never conveyed by color alone (text label + glyph).
- **Expanders** with rich headers set `controls:Accessibility.HeaderName` so the header toggle has a name.
- **Keyboard.** The sidebar is one tab stop; Up/Down move between pages, and Ctrl+1 … Ctrl+6 jump to a
  page from anywhere. Focus starts on the current sidebar item. Tab moves into the page and Shift+Tab
  returns (no traps). Buttons, list items and option cards use a 2 px accent focus ring.
- **Live regions.** Progress text (discovery status, backup stage) is `LiveSetting="Polite"`; info bars
  are polite live regions named by their title.
- **Motion.** Page transitions are skipped when Windows animations are off or high contrast is on.
- **High contrast.** When Windows high contrast is on, DevBR uses the system colors and Settings says so.

## Progress

Worker progress is coalesced by `ThrottledProgress<T>` to about ten UI updates per second (the latest
value always wins and is never lost). Throughput and time left come from `ThroughputEstimator`: a
sliding window of measured bytes, shown only after enough samples over at least two seconds and, for the
time left, only for a determinate stage with a known total. Open-ended discovery shows counts and an
indeterminate bar, never a percentage or ETA. Cancelling is acknowledged immediately on screen.

## Automated: `tests/DevBR.UiTests`

FlaUI (UIA3) tests launch the built `DevBR.exe` with `--machine sample` (or `clean`) and an isolated
state folder set through `DEVBR_DATA_ROOT` (development builds only), so they never touch
`%LOCALAPPDATA%\DevBR`. They drive the real desktop and are therefore opt-in:

```powershell
dotnet build DevBR.slnx
$env:DEVBR_UI_TESTS = '1'
dotnet test --project tests/DevBR.UiTests
```

Without `DEVBR_UI_TESTS=1` every test is reported as skipped. Keep the desktop unlocked and avoid using
the mouse or keyboard while they run (one test sends real key presses, another samples screen pixels).
Set `DEVBR_EXE` to test a different build.

| Test | Checks |
|---|---|
| `Startup_does_not_run_discovery` | Launch shows Overview; Discovery is in its not-started state |
| `Sidebar_reaches_every_page_by_keyboard` | Down arrow through all six pages, Ctrl+1…6, Tab out of and Shift+Tab back into the sidebar |
| `Theme_presets_apply_from_settings` | Graphite, Midnight, Plum, Light and Follow Windows are selectable, saved, and change the rendered background |
| `Every_page_has_meaningful_accessible_names` | Audit of all six pages before and after discovery, every discovery tab, and every backup wizard step |
| `Discovery_runs_to_completion_on_the_sample_machine` | Discovery finishes on the simulated workstation and lists inventory |
| `Backup_wizard_reaches_the_review_step` | Wizard walks from selection to the confirm-the-plan step |
| `Backup_then_restore_to_a_clean_target_with_accessible_results` | Real backup, opened on the clean target, preflight, approval, restore; audits the report, plan, results and Recent restores |

The audit (`AccessibilityAudit`) fails on: interactive controls without a name, names that are record
dumps (`Row { X = … }`) or type names (`DevBR.App.…`), and icon-font glyphs exposed to screen readers.

## Manual checks (per release)

| Area | How |
|---|---|
| Screen reader | Narrator (Win+Ctrl+Enter): walk each page with Tab and scan mode; confirm lists, progress and results read sensibly |
| High contrast | Settings → Accessibility → Contrast themes (Aquatic, Desert, Dusk, Night sky): text, focus rings, selection and disabled states stay visible; Settings shows the high-contrast notice |
| Reduced motion | Settings → Accessibility → Visual effects → Animation effects off: page changes are instant |
| Display scaling | 100 %, 150 % and 200 % (Settings → Display → Scale), and text size 150 % (Accessibility → Text size): no clipped text in the sidebar, wizard steps, theme cards or inventory columns; the window still fits at its 880×580 minimum |
| Keyboard only | Complete discovery → backup → restore without a mouse, including file pickers and confirmation dialogs |
