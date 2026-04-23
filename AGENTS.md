# Javascript files

Do NOT manually modify Assets/video-stages.js. All Javascript modifications should be applied to the Typescript files in frontend directory (or scripts). It is OK to modify the CSS file(s) within this project directly.

# Run Tests

You are explicitly required to run unit tests for this extension when your changes affect this extension’s code or tests.

## Where `run-tests` is (working directory matters)

The `run-tests` script lives in **this extension’s root directory**: the folder that contains `SwarmUI-VideoStages.csproj`, `SwarmUI-VideoStages.Tests.sln`, and `run-tests`. It is **not** at the main SwarmUI repository root and not inside `src/`, `Tests/`, or `frontend/` unless that is already the extension root.

Before running it:

1. **Confirm cwd**: your shell (or tool `working_directory`) must be that extension root, **or**
2. **Call it by path from the SwarmUI repo root**:

   `src/Extensions/SwarmUI-VideoStages/run-tests`

   Example from SwarmUI root:

   `./src/Extensions/SwarmUI-VideoStages/run-tests`

If `./run-tests` fails with “No such file or directory”, you are in the wrong directory—use the path above or `cd` to the extension root (the directory that contains this `AGENTS.md` file).

# Rules override

If `AGENTS.dev.md` exists beside this file, it takes precedence over this one for overlapping instructions.
