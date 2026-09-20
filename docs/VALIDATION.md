# Validation record

Validated for 1.4.1-preview.4 on 2026-09-20, based on upstream commit
`a90af38` and .NET SDK 8.0.425 on macOS arm64.

- Windows x64 self-contained publish completed successfully:
  `dotnet publish IsaacOnlineModded/IsaacOnlineModded.csproj -r win-x64 -p:EnableWindowsTargeting=true -c Release -o artifacts/win-x64`
- 217 C# regression checks passed (`dotnet run --project tests/Regression`).
- 12 Lua 5.4 tests passed with Lupa 2.8, including syntax checks and behavior
  against patched **copies** of installed CuerLib, EID, MCM Impure, emote-binds and
  Specialist Lua files. The installed source files were not changed.
- These copies cover all ten files selected by the safety patcher. Running the
  planner after patching returned no further changes.
- The CuerLib restart behavior test fails when the new input-history reset is
  removed and passes with it present. It runs the complete netcoop module with
  mocked callbacks, including local-player identification before/after restart.
- EID recipe browsing preserves player ControlsCooldown in co-op while retaining
  its single-player behavior. Coming Down XML parses, uses version 5 and retains
  the same entity attributes.
- The EID upstream reference inspected was commit
  `3b5010128a8b38c2ca02c66441ddf8b8d8f10de3`.

Limits: no Windows GUI execution, no actual game-executable signature validation,
no two-client session, no reproduction or confirmed repair of the user's hold-R
network disconnect. Gameplay mods outside the six supported safety profiles
remain unverified. CI's Lua run executes the bundled music tests; tests requiring
installed third-party mod copies are explicitly skipped there.
