# Settings navigation and layout verification

## Changes

- Six persistent categories: interface, connection, generation, privacy, local models, backup.
- Only the selected category is visible; its existing controls retain unsaved values.
- Navigation stays above the content scroller and wraps at smaller window widths.
- Section changes reset content scroll to the top.
- Settings content expands with the window instead of shrinking to its desired width.
- Removed duplicate TextBox/PasswordBox template padding (measured single-line height: 55.78 to 37.78 pixels).
- Long privacy/history options wrap; backup actions use a wrapping row.
- View unload detaches its view-model event subscription.

## Verification

- Release application build and test project build: zero warnings and errors.
- Full test suite: 514 passed, zero failed or skipped (including four new navigation tests).
- Actual WPF views rendered offscreen at 1428 x 858 and 960 x 640.
- 48 settings snapshots cover all six categories in both styles and both color modes; Harbor dark also uses English.
- Real ListBox/PasswordBox bindings verify category selection, exclusive visibility, unsaved URL/API key retention, scroll reset, language refresh preserving the selected category, single-line input heights, and horizontal control bounds.
- Five additional snapshots and real theme-card/mode bindings verify immediate appearance changes and existing conversation artwork updates after the input-template fix.
- Preview uses isolated fixture data and a plain Application with the production App.xaml resources; production startup, welcome dialog, real user data, model calls, and HTTP calls are excluded.
- Self-contained win-x64 publish completed. Root Lorehaven.exe matches publish/Lorehaven.exe by SHA256.

## Representative snapshots

- starlit-light/settings-local-models-starlit-light-zh-1428x858.png
- starlit-light/settings-interface-starlit-light-zh-960x640.png
- starlit-dark/settings-generation-starlit-dark-zh-960x640.png
- harbor-dark-en/settings-local-models-harbor-dark-en-960x640.png
- harbor-dark-en/settings-privacy-harbor-dark-en-960x640.png
