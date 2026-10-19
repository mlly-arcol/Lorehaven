# Lorehaven story-journal theme — visual QA

This report records verification before the Lorehaven rename; evidence retains the original executable name and paths.

final result: passed

## Visual truth and capture

- Theme source: `artifacts/JournalTheme/Reference/theme.png`, supplied by the user (1487 × 1058).
- Icon source: `artifacts/JournalTheme/Reference/icon.png`, supplied by the user (1254 × 1254).
- Final native capture: `artifacts/JournalTheme/Preview/shell-chat-light-1487x1058.png` (1487 × 1058).
- This is the existing Windows WPF application, rendered with real controls/resources and an isolated local fixture. No browser, HTML recreation, production UserData, network request or model call is involved.
- Full comparison: `artifacts/JournalTheme/comparison-full.png`. Both 1487 × 1058 app-content regions are placed next to each other without resizing. There is no OS window chrome in either comparison. Capture uses 96 DPI / one image pixel per WPF unit; CSS sizing is not applicable.
- Focused comparisons: `comparison-conversation.png`, `comparison-header-notes.png` and `comparison-icon.png` in the same directory. Crops retain native scale; icon comparison normalizes the display-only background/padding while retaining the supplied emblem.
- State: an existing group story, current branch selected, three character fixtures, real persisted per-character memory fixtures. The concept contains two characters and an invented scene narration; production does not inject that narration or replace user character artwork with invented portraits.

## Required fidelity surfaces

### Fonts and typography

Cambria/SimSun titles recreate the reference's editorial hierarchy. Segoe UI Variable/Microsoft YaHei UI body text retains native Chinese readability; conversation text is 16 WPF units with selectable Markdown. Story titles, role names and body text are distinct. Long real character names and session titles truncate with full-text tooltips. Actual user data determines names, titles, portraits and memory text.

### Spacing and layout rhythm

Three regions follow the selected direction: story/navigation rail, illustrated conversation workspace, and a story-notes rail. Portraits sit outside the message bubbles, user replies align right with a 650-unit maximum, and message actions appear on hover or keyboard focus. The composer remains anchored and visible. At widths below the wide-layout threshold, notes become a closable overlay, preserving room for dialogue. Advanced tools remain in a collapsible, vertically scrollable shelf.

The production rail is 240 units (220 in compact windows), notes are 270 units, and the layout follows content rather than fixed mockup positions. Branches are vertical to accommodate real titles; this is an intentional native adaptation of the concept's short horizontal labels.

### Colors and visual tokens

Light palette: warm paper `#F5F1E8`, forest green `#244D47`, terracotta accents and cream reading surfaces. All app palettes use existing dynamic semantic resources. Dark mode uses warm forest/charcoal surfaces and light text. Supporting forms and every dialog use the same resources. Header and decoration opacity are reduced in dark mode; opacity masks remove hard image boundaries.

### Image quality and asset fidelity

Generated assets reproduce the reference's painted harbor, fine paper grain, books, map and compass; they are image resources, not a screenshot used as the app. Native controls remain live above them. The supplied lighthouse/book emblem is used in the app PNG and multi-size Windows ICO (16/24/32/48/64/128/256). The icon exterior has real alpha transparency. Enlarged inspection reveals a few very faint disconnected edge pixels left by the transparency extraction; these are not apparent at the actual 43-unit app-brand size and are recorded as P3 polish.

### Copy and app-specific content

Recent stories, current route, characters and memories are bound to application data. No sample story, fake portrait or fixed memory is added to the user's database. New fixed labels are in the translation table. All previous tools remain reachable, including search/filter, pinned messages, recap, statistics, prompt settings, model settings, plugins, export, rename and delete. The account-free welcome-to-workspace flow is preserved.

## Comparison history and resolved findings

1. **P2 — heavy message cards and left-side user avatar.** The first native pass retained full-row cards and reserved action space. Moved avatars outside bubbles, moved the user's avatar right, added warm user paper, hid idle action rows, and increased body text. Final conversation comparison confirms the revised hierarchy.
2. **P2 — user bubble width overridden by a local value.** Moved the default maximum width into the style so the 650-unit user trigger applies. Final 1487-unit capture confirms right alignment and the smaller bubble.
3. **P2 — dark decoration seams.** Added vertical opacity masks to sidebar/notes artwork and a horizontal mask to the harbor header. Final dark wide and compact captures show smooth blending.
4. **P2 — native white branch selection.** Replaced the TreeViewItem presentation with themed selected/focus surfaces while preserving the native header, expander and nested items. Final `Preview/branches-dark-480.png` and `branches-light-690.png` show terracotta selection and the separate green current-route badge.
5. **P2 — stale permanent memory rail.** Memory saves/deletes/edits/locks and extraction batches publish character-scoped changes. The journal refreshes through the WPF dispatcher and validates the selected character and latest read request before applying results. Regression tests cover switching characters and additions/deletions while the editor is closed.
6. **P2 — possible sidebar focus race during navigation.** User pointer/key events navigate to Chat before the selection can disable the history list during loading. Background list refresh does not trigger navigation.

## Verification

- Main application Release build: 0 warnings, 0 errors.
- Full regression suite: 392 passed, 0 failed, 0 skipped.
- Final focused set: 30 PNGs in `artifacts/JournalTheme/Preview`, including 1487 × 1058, 1440 × 860, 1280 × 820, 960 × 640, 960 × 600, notes overlay open/closed, tools expanded, paused performance, welcome and branch dialogs, in light/dark modes.
- Supporting full-page set: 92 PNGs were used to check characters, knowledge, prompt-studio tabs, plugins, settings and memory/performance dialogs at narrow sizes. These intermediate captures were removed during the user-requested cleanup. The final 30-image Preview set is retained.
- Actual routed button Click events exercised notes open/close and search-tools open/close/reopen. Search popup target and view-model binding were verified. Branch expansion/collapse and selected-item state were exercised in the native preview harness.
- New journal tests cover route scope, branch navigation with outgoing draft retention, per-character private memories, and live rail refresh. Existing tests cover performance pause/interjection, branching, memory locking and persistence.
- Model/network calls in all preview runs: 0.
- Source whitespace and XAML parsing checks passed.

## Limits and follow-up polish

- Offscreen WPF captures verify app content, not Windows title-bar chrome, Explorer icon-cache behavior, screen-reader certification or a physical keyboard/mouse session.
- The real welcome modal was not launched against production UserData; the entry flow was retained and its content rendered.
- P3: the enlarged transparent icon exterior could receive additional edge cleanup in a future asset pass.
- Character portraits use the user's existing local avatar files, with initials when none are configured. The image-generation reference's invented portraits are not distributed as user identities.

## Implementation checklist

- [x] Apply the selected warm-paper visual direction throughout the existing application.
- [x] Install the selected lighthouse/book brand icon and Windows multi-size icon.
- [x] Bind the story rail, branches and memories to real state.
- [x] Preserve advanced tools, startup flow and compact-window access.
- [x] Resolve actionable P0/P1/P2 visual and functional findings.
- [x] Re-render and compare the final production controls.

## Delivery

The verified self-contained Windows package is now `E:/NativeTavern/NativeTavern.exe` (189092253 bytes). During the user-requested cleanup the old launcher was no longer locked, so it was replaced with the hash-verified journal build. The redundant Journal-named launcher and publish directory were removed. The application directory and existing UserData location remain unchanged; no user process was stopped. Cleanup details are in `artifacts/JournalTheme/cleanup.json`.
