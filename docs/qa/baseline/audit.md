# Lorehaven Current UI Audit

## Overall verdict

Chat and Characters are the strongest screens. The next iteration should prioritize the blank Prompt Inspector, empty-state guidance, Settings structure, and localization consistency.

## Steps

1. Chat - Healthy with minor polish opportunities.
   - Improve message-action discoverability, disabled send/stop states, and mixed-language labels.
   - Accessibility risk: small muted actions and footer text need contrast and keyboard-focus testing.

2. Characters - Healthy.
   - Improve the search placeholder, missing-avatar fallback, and unsaved-change feedback.
   - Accessibility risk: favorite state relies heavily on star color and opacity.

3. Prompt Studio - Needs improvement.
   - The empty library and editable-looking detail form appear together without explaining the current mode.
   - Add an empty state, disable Delete until an item exists, and distinguish create from edit mode.

4. Knowledge Base - Needs improvement.
   - The empty document panel has no central import action and all copy remains English in a Chinese interface.
   - Add a drop-zone empty state, document status, disabled actions, and localized copy.

5. Prompt Inspector - Broken.
   - Navigation changes to the page, but the content surface remains blank after a stable two-second wait.
   - Fix view activation/rendering first, then add loading, no-chat, success, and error states.

6. Settings - Functional but dense.
   - One long form mixes connection, generation, privacy context, and local-model controls.
   - Group fields into sections, use numeric controls, keep Save/status visible, and add helper text.

## Recommended order

1. Repair Prompt Inspector.
2. Add empty, loading, error, and disabled states to Prompt Studio and Knowledge Base.
3. Reorganize Settings into Provider, Generation, Context Privacy, and Local Models sections.
4. Complete Chinese localization.
5. Improve chat actions and character-card accessibility.

## Evidence limits

- Screenshots confirm visible layout and stable empty states only.
- Keyboard order, screen-reader names, contrast ratios, scaling, and destructive-flow behavior need interaction testing.
