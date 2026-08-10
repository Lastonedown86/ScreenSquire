# Media list: single tick-based selection model

Date: 2026-08-10
Status: approved

## Problem

The "Media on this Pi" list has two parallel selection models that clash:

- Row **checkboxes** feed only "Delete selected" (and "Select all").
- Row **highlight** (ListBox single selection) feeds "Add to playlist →" and
  "Show now".

Both button groups say "selected" while meaning different things. This
confused the builder during the real-Pi acceptance run; it will confuse the
client.

## Design

One selection model: **ticks**.

- Clicking anywhere on a media row (except the per-row ✎/✕ buttons) toggles
  its checkbox. Row highlight is removed entirely.
- **Add to playlist →** adds every ticked file in list order, then clears the
  ticks (action consumes selection). Enabled when ≥1 file is ticked.
- **Show now** requires exactly one ticked file; otherwise a toast explains
  "Tick exactly one file to show it." Enabled only at exactly one tick.
- **Delete selected** is unchanged (already tick-driven). **Select all** and
  the per-row ✎ rename / ✕ delete buttons are unchanged — explicit per-row
  actions, not selection.
- Tooltips and labels updated so "selected" consistently means ticked.

## Scope

`windows-app/MainWindow.xaml` (row template, list style, button states) and
`windows-app/MainWindow.xaml.cs` (handlers). No agent, signage-core, or API
changes. Ships as an app-only release after merge.

## Testing

The action logic is `Where(IsChecked)` one-liners over the existing
ObservableCollection; the .NET suite covers signage-core, not WPF
code-behind. Approved TDD exception: existing .NET suite must stay green,
and behavior is verified on hardware by re-running the acceptance
checklist's daily-ops items (upload → tick → add to playlist → play;
show-now single-tick rule) on the release build.
