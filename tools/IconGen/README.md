# IconGen

Standalone tool (not part of `OptimusVibe.sln`) that draws the Optimus-Vibe save-crystal app icon as WPF vector code (`Program.cs`).
It renders PNGs at 16-256 px, writes `src/OptimusVibe.App/Assets/app.ico` (PNG frames), `app-256.png` and `app-64.png`, then re-reads the ICO and prints its frame sizes.
Regenerate from the repo root: `dotnet run --project tools/IconGen -- . [preview.png]` (the optional second argument also writes a review sheet; it is not committed).
Edit the geometry in `Program.cs` (separate simplified drawings for 16-24 px and 32-40 px) and rerun.
