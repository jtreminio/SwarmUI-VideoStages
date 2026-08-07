# Git repo

This extension is in a subdirectory that is gitignored by the SwarmUI project. This extension has its own repo. Do not look in the SwarmUI project for any git changes related to this extension.

# Javascript files

`Assets/video-stages.js` (and its `.js.map`) is COMPILED BUILD OUTPUT — never read it and never modify it. Reading it wastes context and tells you nothing the sources don't. All Javascript modifications should be applied to the Typescript files in the frontend directory (or scripts), then rebuilt with `npm run build`. It is OK to modify the CSS file(s) within this project directly.

# Run Tests

You are explicitly required to run unit tests for this extension when your changes affect this extension’s code or tests.

**See every test fail before you trust it.** Break the code a new or changed test guards, watch it
go red, restore. A test that has never failed is not evidence. Before deleting a test, prove by the
same method that its coverage exists elsewhere — a claim that it does is not proof, and has been
wrong, in both directions: a reviewer's reasoned argument that coverage exists is not proof either.
Measure it.

Restore from a copy, not `git checkout --`: on an uncommitted file, that reverts your own work, and
every result after it is noise. Take the copy immediately before each mutation — a snapshot from
earlier in the session restores the same way. Confirm the mutation actually applied — a pattern that
silently missed leaves you reading a green run as evidence.

A mutation that breaks compilation is not a red test, and `Test Suites:` cannot tell you which you
got — both print `1 failed`. The `Tests:` line is the tell:

| | `Test Suites:` | `Tests:` |
|---|---|---|
| the test genuinely failed | `1 failed, 1 total` | `1 failed, 1 total` |
| the suite failed to compile | `1 failed, 1 total` | `0 total` |

Over a whole run the compile break hides completely — `Test Suites: 1 failed, 67 passed` sits beside
`Tests: 1183 passed, 1183 total`, no failure on the `Tests:` line at all, only a total that quietly
dropped. A real red carries a non-zero *failed* count there and does not shrink the total.
`run-tests` is `dotnet test && npm run test`, so a C# build failure means jest never ran at all.

Failure detail is suppressed by default, for a plain failed assertion as much as for a compile
break: run `JEST_VERBOSE=1 npm run test` to see why. Passing `--reporters=default` works too, but the
flag is variadic — put it after any file path or it swallows the path as a second reporter.

Drift tests over generated artifacts count as red-able: the artifact drifting from its generator
*is* the break, and the way to see it fail is to change the generator.

A test that cannot be made red by breaking production code is not exempt — it is the thing this
rule is aimed at. Negative reflection guards — no member named X, no parameter or property of
type Y — only fail when someone puts it back, which review catches. Delete them rather than keep
them as decoration. This is the one case where the coverage proof above does not apply: they guard
nothing, so there is no coverage to relocate.

## Where `run-tests` is (working directory matters)

The `run-tests` script lives in **this extension’s root directory**: the folder that contains `SwarmUI-VideoStages.csproj`, `SwarmUI-VideoStages.Tests.sln`, and `run-tests`. It is **not** at the main SwarmUI repository root and not inside `src/`, `Tests/`, or `frontend/` unless that is already the extension root.

Before running it:

1. **Confirm cwd**: your shell (or tool `working_directory`) must be that extension root, **or**
2. **Call it by path from the SwarmUI repo root**:

   `src/Extensions/SwarmUI-VideoStages/run-tests`

   Example from SwarmUI root:

   `./src/Extensions/SwarmUI-VideoStages/run-tests`

If `./run-tests` fails with “No such file or directory”, you are in the wrong directory—use the path above or `cd` to the extension root (the directory that contains this `AGENTS.md` file).

## Worktrees

Provision one with `scripts/worktree add <name>`, tear it down with `scripts/worktree rm <name>`. `<name>` is the suffix only — letters, digits and dashes — and always lands at `SwarmUI-VideoStages-<name>` next to the main checkout. Both subcommands only run from the main checkout — if you are working inside a worktree, ask whoever provisioned it to add or remove one for you. Never `git worktree add` by hand: the worktree has to sit at `src/Extensions/<dir>` for the project imports to resolve, and it needs `node_modules` plus a `Directory.Build.rsp` that neither git nor `npm` will put there — without them `./run-tests` cannot run at all.

Provisioning itself is automatic: `scripts/worktree-post-checkout`, installed into `.git/hooks` by `add`, fires on any new worktree — including one a bare `git worktree add` or an agent harness creates — and links `node_modules`, links `nonversioned`, copies `AGENTS.dev.md` and writes `Directory.Build.rsp`. What it cannot fix is location. A worktree anywhere but `src/Extensions/<dir>` cannot build at all, because MSBuild resolves `../../SwarmUI.extension.props` against the project file's real path; symlinking one into place fails with `MSB4019` for `/SwarmUI.extension.props`. The hook says so and names the command to use instead.

`rm` deletes the branch too, once every commit on it is already in HEAD — by patch id, so a branch harvested with `cherry-pick` still counts. If anything on it is nowhere else it keeps the branch and says how many commits that is. `add` refuses a name whose branch already exists, because a worktree that silently starts with someone else's commits applied is work an agent cannot see; pass `--resume` when continuing it is what you meant.

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
