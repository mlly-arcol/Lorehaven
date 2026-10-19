# Feature preview

## Lorehaven themes

`--themes-only --style starlit|harbor --theme light|dark` renders the current
conversation at 1440/960 widths, the settings selector at 1280/960 widths, and the
welcome page. Each run uses isolated data and blocks model and HTTP requests.
For example, after building this project:

```powershell
& .\Tests\FeaturePreview\bin\Release\net10.0-windows\FeaturePreview.exe artifacts\LorehavenTheme\Preview\starlit-light --style starlit --theme light --themes-only --verify-switch
```

`--verify-switch` also selects both cards and each color mode through the real
settings bindings, waits for automatic saves, and checks that an already-created
conversation updates its artwork and opacity for all four style/mode pairs.
The current verified screenshots and Image Gen prompts are in
`artifacts/LorehavenTheme/`.

## Full feature set

This isolated WPF renderer creates 46 PNGs per theme (92 total):

- Group performance, story branches, and character memory dialogs at minimum and normal widths.
- ChatView at 800/1200 widths in idle and paused states.
- The actual MainWindow shell at 1280×820, its minimum 960×640 size, and a
  conservative 960×600 content viewport allowing for native window chrome, showing
  chat, characters, knowledge, all four Prompt Studio tabs, plugins marketplace
  and installed views, settings, and prompt inspector.
- An empty chat in the actual shell at all three sizes.

Run after the main application's build/tests finish:

```powershell
dotnet build artifacts/FeaturePreview/FeaturePreview.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false
& .\Tests\FeaturePreview\bin\Release\net10.0-windows\FeaturePreview.exe E:\NativeTavern\artifacts\FeaturePreview\Images --theme light
& .\Tests\FeaturePreview\bin\Release\net10.0-windows\FeaturePreview.exe E:\NativeTavern\artifacts\FeaturePreview\Images --theme dark
```

The preview loads application resources without starting the application, showing
the welcome screen, opening windows, or contacting a model. Data lives in a unique sandbox under
the output folder and is deleted by the existing test harness when rendering ends.
It never calls the legacy-data migration method. `Program.cs.txt` is compiled only
by the explicit preview project, so no main-project exclusion is needed.
The local `Directory.Build.props` also places generated source and binaries under
`Tests/FeaturePreview/obj` and `Tests/FeaturePreview/bin`, which the main
project already excludes.
Each theme uses a fresh process so static template resources cannot retain brushes
from the previous palette during offscreen rendering.

The shell uses production service registrations with isolated database, fixture
provider, and plugin directory overrides. Settings and other pages receive explicit
sample objects rather than running startup initialization, local model discovery,
plugin downloads, or reading real UserData. Both typed model clients and the plugin
client have HTTP handlers that reject requests. The program asserts zero model
calls and zero HTTP attempts before exiting. A configured provider in a preview
is fixture state and does not represent a live connection.

Shell rendering keeps content attached to the actual MainWindow (preserving
Window-ancestor navigation bindings), sets its DataContext explicitly, resets its
startup transform, and sets Window opacity to 1 without showing a window or
running App.Run/OnStartup. Prompt Studio tabs are selected only after
their real visual tree has been laid out.

For the warm paper journal redesign, `--journal-only` creates 13 PNGs per theme
without running modal interaction checks:

```powershell
& .\Tests\FeaturePreview\bin\Release\net10.0-windows\FeaturePreview.exe E:\NativeTavern\artifacts\JournalPreview --theme light --journal-only
& .\Tests\FeaturePreview\bin\Release\net10.0-windows\FeaturePreview.exe E:\NativeTavern\artifacts\JournalPreview --theme dark --journal-only
```

This fast mode captures actual MainWindow chat at 1487×1058 (the supplied reference
image size), 1440×860, 1280×820, 960×640, and a conservative 960×600 content viewport.
Additional shell captures cover explicitly closed and opened notes at 960×640,
paused group performance, and expanded conversation tools at 1280×820
and 960×640. The actual WelcomeWindow is also captured at its three established
sizes. Sidebar widths match production (240 normally, 220 in compact windows).
Journal branch items come from the real branch refresh method; memory is reloaded
from the isolated fixture repository without opening its native popup.
The narrow notes captures raise the actual open and close buttons' click events
and assert panel visibility after each change, without creating an OS window.
Expanded tool captures also raise the real header search button's click event to
verify open, close, and reopen states before the final expanded screenshot.

Expanded tool captures assert that the real search popup's placement target is the
actual conversation search field and that its open-state binding remains intact.
They deliberately render with native popups closed; these assertions verify the
association rather than claiming a live popup interaction or OS-window capture.

For a quick branch-dialog recheck after changing its TreeViewItem template, use
`--branches-only` with each theme. It renders the 480 and 690 widths and verifies
the real template's expander two-way binding, child visibility, header presenter,
and selection switching/restoration. These are static WPF template checks; they do
not claim physical keyboard or mouse-input certification.

The selected visual references are kept in `artifacts/JournalTheme/Reference`,
and final captures in `artifacts/JournalTheme/Preview`. Earlier UI refresh capture
sets and generated build outputs were removed during workspace cleanup. Run the
build command above to recreate the preview executable when needed.

For the account-free welcome flow, use `--welcome-only` to render the actual
WelcomeWindow in a fresh process for each theme:

```powershell
& .\Tests\FeaturePreview\bin\Release\net10.0-windows\FeaturePreview.exe E:\NativeTavern\artifacts\WelcomePreview --theme light --welcome-only
& .\Tests\FeaturePreview\bin\Release\net10.0-windows\FeaturePreview.exe E:\NativeTavern\artifacts\WelcomePreview --theme dark --welcome-only
```

This mode captures the normal 1020×700 viewport, the minimum 900×620 size, and a
conservative 884×580 content viewport allowing for native window chrome. It also
runs real modal interactions for the primary entry button and closing the screen.
These two temporary windows are invisible, positioned offscreen, omitted from the
taskbar, and request no activation. The modal checks never start the application
or open the actual workspace, and the existing isolated fixture/provider checks
still require zero model calls and zero HTTP attempts.

Append `--render-only` to `--welcome-only` for visual rendering without these modal
checks. Offscreen modal checks can hang in restricted desktop environments; use
render-only captures there and report that interaction was not verified.
