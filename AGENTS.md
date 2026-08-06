# Git repo

This extension is in a subdirectory that is gitignored by the SwarmUI project. This extension has its own repo. Do not look in the SwarmUI project for any git changes related to this extension.

# Javascript files

`Assets/video-stages.js` (and its `.js.map`) is COMPILED BUILD OUTPUT — never read it and never modify it. Reading it wastes context and tells you nothing the sources don't. All Javascript modifications should be applied to the Typescript files in the frontend directory (or scripts), then rebuilt with `npm run build`. It is OK to modify the CSS file(s) within this project directly.

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

# SwarmUI core is king

Use core's own code wherever it can do the job. Build on the node core already made instead of a parallel one beside it: call `g.CreateNode`, `g.CreateKSampler`, `g.CreateModelLoader`, `g.CreateConditioning`, `g.CreateImageToVideo` and their siblings rather than hand-assembling the same graph. Taking core's output and adjusting it afterwards beats forking it — reconciling a small difference is cheap, a snowflake implementation is not.

This is settled direction, not an aspiration. Every architecture family now takes over core's sampler, decode, empty latent and conditioning instead of duplicating them, and a timeline stage loads its model through core's own loader. If you are about to write a node core already builds, stop and take core's.

ComfyTyped and the Comfy graph tests are what make this safe: generated node types catch a wrong shape at compile time, and the graph tests assert the workflow actually emitted. So when core's behaviour is not quite right for a timeline, fix it at the seam — widen the node, retarget the save, override the parameter — instead of replacing core's path with your own.

# How to change this codebase

- **Reduce unnecessary abstraction and misdirection.** Delete wrappers that only carry another type, results objects with one producer, options records with one caller, interfaces with one implementer that buy no layering. A name must not promise something the code does not do.
- **Comment only what the code cannot say.** A non-obvious invariant, a cross-file coupling, a "why not the obvious thing". Never restate the next line, never paraphrase a signature in XML doc, never narrate a past fix.
- **Deslop every comment you pass.** Cut hedging, reassurance, and over-explanation. Shorter and blunter is correct.
- **Touch a file, clean its comments.** Auditing every comment in a file you edit is part of that edit, not a follow-up.
- **Names are for humans.** Files and methods should read as plain English and match what is inside; a filename names the type it holds. Renaming is normal maintenance — do it freely, in its own commit.

Directions the recent history has been going. Keep going that way:

- **One owner per fact.** A rule, a constant, or a piece of arithmetic lives in exactly one place and everyone calls it. Two copies that must agree is a defect even when they currently do.
- **Namespace follows directory, and both say what they do.** Move a type to the layer that owns it rather than reaching across layers to reach it.
- **Fold layers inward instead of adding them.** Prefer giving an existing owner the job over introducing a collaborator to carry it.
- **Split a file that holds unrelated concerns**, and name each half for its half.
- **Match the runtime instead of reimplementing it.** When planning predicts something execution decides, call the same code; don't hand-roll a parallel copy.
- **Prefer a declared capability over a hard-coded family check.** Ask the descriptor what an architecture can do; don't compare its id.
- **Generate and drift-test cross-boundary vocabulary.** C# owns it; hand-written mirrors are a defect.
- **Keep the build and the test output quiet.** No warnings, no noise.
- **One item, one commit, one short imperative subject.** No body, no trailers.
- **Backing a change out is fine** when it did not earn its place.

# Rules override

If `AGENTS.dev.md` exists beside this file, it takes precedence over this one for overlapping instructions. The file will be gitignored, check the filesystem manually.
