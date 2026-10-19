# Lorehaven design QA

This report records verification before the Lorehaven rename; evidence retains the original executable name.

## Scope and evidence

- Build: `NativeTavern.exe`, Release, win-x64, self-contained single file.
- Viewport: 1280 x 820 at the current Windows display density.
- Source baseline: `baseline/01-chat.png` through `baseline/06-settings.png` and the user-provided character-selection reference.
- Implementation evidence: live final EXE inspected with Windows Graphics Capture on Chat, Characters, Prompt Studio, Knowledge Base, Prompt Inspector, and Settings; Settings was also inspected after scrolling through its lower sections.

## Fidelity review

- Layout: passed. The character list selection outline follows the card edge; character details remain independently scrollable; Settings uses distinct cards with a fixed action footer.
- Typography: passed. Titles, section headings, labels, helper text, and message actions have a consistent hierarchy.
- Color and states: passed. Empty states are visible without competing with primary actions; unavailable destructive actions are disabled; selected cards retain a subtle blue outline.
- Content: passed. Dynamic pages are translated after navigation, including Knowledge Base; missing character images use initials rather than blank blocks.
- Interaction: passed. Navigation, empty-state actions, selection-dependent disabled states, settings scrolling, message swipe controls, and Prompt Inspector refresh controls are exposed and visually coherent.

## Regression checks

- Prompt Inspector now renders its XAML and displays the assembled prompt instead of a blank page.
- Prompt Studio shows empty states for personas, lorebooks, entries, and presets; delete actions remain disabled without a selection.
- Knowledge Base shows a centered empty state and disables delete/toggle actions without a selected document.
- Settings is grouped into Interface, Provider connection, Generation, Context privacy, and Local model runtime; Test Connection and Save remain fixed at the bottom.
- Localization refresh is scheduled after every main-view change, preventing dynamic pages from remaining in English during fast navigation.
- Automated tests: 18 passed, 0 failed, 0 skipped.

## Open severity items

- P0: none.
- P1: none.
- P2: none.

## Final result

passed
