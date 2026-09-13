# iOS diagnostic IL weaver

`IosTraceWeaver` adds six bounded, synchronous `Log.Info` calls to the matched
`OneTimeInitialization.ExecuteDeferred()` method in a staged copy of `sts2.dll`:

```text
PHYS stage=before_atlas_load / after_atlas_load
PHYS stage=before_model_preload / after_model_preload
PHYS stage=before_prewarm_jit / after_prewarm_jit
```

It validates the exact game and logging signatures, refuses missing, ambiguous,
partial, or duplicated instrumentation, and is idempotent. The workflow invokes
it only for a manually dispatched run with `trace_diagnostics` enabled, after
copying `lib/*` into `godot/lib/`. The root `lib/sts2.dll` is never used as the
weaver output.

```text
dotnet run --project scripts/IosTraceWeaver/IosTraceWeaver.csproj -c Release -- \
  --input godot/lib/sts2.dll --output godot/lib/sts2.dll
```
