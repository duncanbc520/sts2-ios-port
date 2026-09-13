# iOS diagnostic IL weaver

`IosTraceWeaver` adds six bounded, synchronous `Log.Info` calls to the matched
`OneTimeInitialization.ExecuteDeferred()` method in a staged copy of `sts2.dll`:

```text
PHYS stage=before_atlas_load / after_atlas_load
PHYS stage=before_model_preload / after_model_preload
PHYS stage=before_prewarm_jit / after_prewarm_jit
```

It validates the exact game and logging signatures, refuses missing, ambiguous,
partial, or duplicated instrumentation, and is idempotent. The diagnostic
workflow also passes `--asset-trace`, which adds dynamic path-bearing begin/end
markers around synchronous `AssetCache` loads, threaded asset requests and
finalization, the threaded synchronous fallback, and `FontManager` font loads.
It also brackets `ConditionalFormatter` construction, deferred common/main-menu
loading, and the language dropdown/item boundaries. The root `lib/sts2.dll` is
never used as the weaver output.

```text
dotnet run --project scripts/IosTraceWeaver/IosTraceWeaver.csproj -c Release -- \
  --input godot/lib/sts2.dll --output godot/lib/sts2.dll
dotnet run --project scripts/IosTraceWeaver/IosTraceWeaver.csproj -c Release -- \
  --input godot/lib/sts2.dll --output godot/lib/sts2.dll --asset-trace
```
