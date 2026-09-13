using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace IosTraceWeaver;

public sealed class TraceWeaverException : Exception
{
    public TraceWeaverException(string message)
        : base(message)
    {
    }
}

public sealed record WeaveResult(bool Changed, int MarkerCount);

public static class TraceWeaver
{
    private const string ExecuteDeferredType = "MegaCrit.Sts2.Core.Helpers.OneTimeInitialization";
    private const string LogType = "MegaCrit.Sts2.Core.Logging.Log";

    private static readonly TraceSite[] Sites =
    {
        new(
            "AtlasManager",
            "MegaCrit.Sts2.Core.Assets.AtlasManager",
            "LoadAllAtlases",
            "before_atlas_load",
            "after_atlas_load",
            IsPrivate: false),
        new(
            "ModelDb",
            "MegaCrit.Sts2.Core.Models.ModelDb",
            "Preload",
            "before_model_preload",
            "after_model_preload",
            IsPrivate: false),
        new(
            "PrewarmJit",
            ExecuteDeferredType,
            "PrewarmJit",
            "before_prewarm_jit",
            "after_prewarm_jit",
            IsPrivate: true),
    };

    private static readonly AssetTraceSite[] AssetTraceSites =
    {
        new(
            "AssetCacheLoad",
            "MegaCrit.Sts2.Core.Assets.AssetCache",
            "LoadAsset",
            "Load",
            "asset_load",
            AssetTracePathKind.Argument1),
        new(
            "ThreadedRequest",
            "MegaCrit.Sts2.Core.Assets.AssetLoadingSession",
            "ProcessLoadingQueue",
            "LoadThreadedRequest",
            "threaded_request",
            AssetTracePathKind.Local0),
        new(
            "ThreadedGet",
            "MegaCrit.Sts2.Core.Assets.AssetLoadingSession",
            "FinalizeLoading",
            "LoadThreadedGet",
            "threaded_get",
            AssetTracePathKind.Local0),
        new(
            "ThreadedFallback",
            "MegaCrit.Sts2.Core.Assets.AssetLoadingSession",
            "CheckLoadingStatus",
            "Load",
            "threaded_fallback",
            AssetTracePathKind.Local2),
        new(
            "FontLoad",
            "MegaCrit.Sts2.Core.Localization.Fonts.FontManager",
            "GetFontForLanguage",
            "Load",
            "font_load",
            AssetTracePathKind.Local1),
    };

    private static readonly StaticTraceSite[] StaticTraceSites =
    {
        new(
            "ConditionalFormatter",
            "MegaCrit.Sts2.Core.Helpers.OneTimeInitialization",
            "ExecuteDeferred",
            "SmartFormat.Extensions.ConditionalFormatter",
            ".ctor",
            "conditional_formatter"),
        new(
            "LoadCommonAndMainMenuAssets",
            "MegaCrit.Sts2.Core.Nodes.NGame/<LoadDeferredStartupAssetsAsync>d__136",
            "MoveNext",
            "MegaCrit.Sts2.Core.Assets.PreloadManager",
            "LoadCommonAndMainMenuAssets",
            "load_common_main_menu"),
        new(
            "LoadCommonAndMainMenuComplete",
            "MegaCrit.Sts2.Core.Nodes.NGame/<LoadDeferredStartupAssetsAsync>d__136",
            "MoveNext",
            "System.Runtime.CompilerServices.TaskAwaiter",
            "GetResult",
            "load_common_main_menu_complete"),
        new(
            "LanguageDropdownPopulate",
            "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NLanguageDropdown",
            "PopulateOptions",
            null,
            null,
            "language_dropdown_populate",
            BoundaryOnly: true),
        new(
            "LanguageDropdownItemInit",
            "MegaCrit.Sts2.Core.Nodes.Screens.Settings.NLanguageDropdownItem",
            "Init",
            null,
            null,
            "language_dropdown_item_init",
            BoundaryOnly: true),
    };

    public static WeaveResult Weave(string inputPath, string outputPath)
    {
        var inputFullPath = GetRequiredFullPath(inputPath, "input");
        var outputFullPath = GetRequiredFullPath(outputPath, "output");
        if (!File.Exists(inputFullPath))
        {
            throw new TraceWeaverException($"Input assembly does not exist: {inputFullPath}");
        }

        // Read the complete input before writing anything. This permits the workflow to
        // transform the staged DLL in place while leaving the root lib/sts2.dll untouched.
        var inputBytes = File.ReadAllBytes(inputFullPath);
        using var inputStream = new MemoryStream(inputBytes, writable: false);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(inputFullPath)!);
        using var assembly = AssemblyDefinition.ReadAssembly(inputStream, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false,
            AssemblyResolver = resolver,
        });

        var executeDeferred = FindExecuteDeferred(assembly.MainModule);
        var logInfo = FindLogInfo(assembly.MainModule);
        var calls = ValidateSites(executeDeferred, assembly.MainModule);

        var markerCount = Sites.Length * 2;
        if (HasAnyTraceMarker(executeDeferred))
        {
            ValidateCompleteInstrumentation(executeDeferred, calls, logInfo);
            CopyIfNeeded(inputBytes, inputFullPath, outputFullPath);
            return new WeaveResult(Changed: false, MarkerCount: markerCount);
        }

        var processor = executeDeferred.Body.GetILProcessor();
        foreach (var site in Sites)
        {
            var call = calls[site.Name];
            InsertLogCall(processor, call, site.BeforeMarker, before: true, logInfo);
            InsertLogCall(processor, call, site.AfterMarker, before: false, logInfo);
        }

        using var outputStream = new MemoryStream();
        assembly.Write(outputStream, new WriterParameters { WriteSymbols = false });
        WriteAtomically(outputFullPath, outputStream.ToArray());
        return new WeaveResult(Changed: true, MarkerCount: markerCount);
    }

    /// <summary>
    /// Adds dynamic path-bearing load boundaries to the diagnostic assembly only.
    /// This is intentionally a separate pass so normal production builds retain the
    /// existing six-stage footprint markers and no per-asset logging overhead.
    /// </summary>
    public static WeaveResult WeaveAssetTrace(string inputPath, string outputPath)
    {
        var inputFullPath = GetRequiredFullPath(inputPath, "input");
        var outputFullPath = GetRequiredFullPath(outputPath, "output");
        if (!File.Exists(inputFullPath))
        {
            throw new TraceWeaverException($"Input assembly does not exist: {inputFullPath}");
        }

        var inputBytes = File.ReadAllBytes(inputFullPath);
        using var inputStream = new MemoryStream(inputBytes, writable: false);
        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.GetDirectoryName(inputFullPath)!);
        using var assembly = AssemblyDefinition.ReadAssembly(inputStream, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false,
            AssemblyResolver = resolver,
        });

        var logInfo = FindLogInfo(assembly.MainModule);
        var assetSites = ValidateAssetTraceSites(assembly.MainModule);
        var staticSites = ValidateStaticTraceSites(assembly.MainModule);
        var markerCount = (AssetTraceSites.Length + StaticTraceSites.Length) * 2;
        if (HasAnyDiagnosticTraceMarker(assetSites, staticSites))
        {
            ValidateCompleteAssetTrace(assetSites, staticSites, logInfo);
            CopyIfNeeded(inputBytes, inputFullPath, outputFullPath);
            return new WeaveResult(Changed: false, MarkerCount: markerCount);
        }

        var concat = FindStringConcat(assembly.MainModule);
        foreach (var site in AssetTraceSites)
        {
            var target = assetSites[site.Name];
            InsertDynamicLog(target.Processor, target.PathLoad, site.BeforeMarker, site.PathKind, concat, logInfo, before: true);
            InsertDynamicLog(target.Processor, target.Call, site.AfterMarker, site.PathKind, concat, logInfo, before: false);
        }
        foreach (var site in StaticTraceSites)
        {
            var target = staticSites[site.Name];
            InsertStaticTrace(target, site, logInfo);
        }

        using var outputStream = new MemoryStream();
        assembly.Write(outputStream, new WriterParameters { WriteSymbols = false });
        WriteAtomically(outputFullPath, outputStream.ToArray());
        return new WeaveResult(Changed: true, MarkerCount: markerCount);
    }

    private static Dictionary<string, AssetTraceTarget> ValidateAssetTraceSites(ModuleDefinition module)
    {
        var targets = new Dictionary<string, AssetTraceTarget>(StringComparer.Ordinal);
        foreach (var site in AssetTraceSites)
        {
            var type = FindType(module, site.DeclaringType);
            if (type is null)
            {
                throw new TraceWeaverException($"Required asset-trace type is missing: {site.DeclaringType}");
            }

            var methods = type.Methods.Where(method => method.Name == site.MethodName).ToArray();
            if (methods.Length != 1 || !methods[0].HasBody)
            {
                throw new TraceWeaverException(
                    $"Expected exactly one body-bearing {site.DeclaringType}::{site.MethodName}() method; found {methods.Length}.");
            }

            var method = methods[0];
            var calls = method.Body.Instructions
                .Where(instruction => instruction.Operand is MethodReference reference
                    && reference.Name == site.LoaderMethodName
                    && reference.DeclaringType.FullName == "Godot.ResourceLoader")
                .ToArray();
            if (calls.Length != 1 || calls[0].OpCode.Code != Code.Call)
            {
                throw new TraceWeaverException(
                    $"Expected exactly one direct Godot.ResourceLoader::{site.LoaderMethodName} call in {method.FullName}; found {calls.Length}.");
            }

            var call = calls[0];
            var pathLoad = FindPathLoad(method, call, site.PathKind);
            targets.Add(site.Name, new AssetTraceTarget(method, method.Body.GetILProcessor(), call, pathLoad));
        }

        return targets;
    }

    private static Instruction FindPathLoad(MethodDefinition method, Instruction call, AssetTracePathKind pathKind)
    {
        var instruction = call.Previous;
        for (var distance = 0; instruction is not null && distance < 12; distance++, instruction = instruction.Previous)
        {
            if (IsPathLoad(instruction, pathKind))
            {
                return instruction;
            }
        }

        throw new TraceWeaverException(
            $"Could not find the expected path load for {method.FullName} before {call.Operand}.");
    }

    private static Dictionary<string, StaticTraceTarget> ValidateStaticTraceSites(ModuleDefinition module)
    {
        var targets = new Dictionary<string, StaticTraceTarget>(StringComparer.Ordinal);
        foreach (var site in StaticTraceSites)
        {
            var type = FindType(module, site.DeclaringType);
            if (type is null)
            {
                throw new TraceWeaverException($"Required diagnostic-trace type is missing: {site.DeclaringType}");
            }

            var methods = type.Methods.Where(method => method.Name == site.MethodName).ToArray();
            if (methods.Length != 1 || !methods[0].HasBody)
            {
                throw new TraceWeaverException(
                    $"Expected exactly one body-bearing {site.DeclaringType}::{site.MethodName}() method; found {methods.Length}.");
            }

            var method = methods[0];
            if (site.BoundaryOnly)
            {
                if (method.Body.Instructions.Count == 0)
                {
                    throw new TraceWeaverException($"Diagnostic boundary has no instructions: {method.FullName}");
                }
                var returns = method.Body.Instructions
                    .Where(instruction => instruction.OpCode.Code == Code.Ret)
                    .ToArray();
                if (returns.Length != 1)
                {
                    throw new TraceWeaverException(
                        $"Expected exactly one return in diagnostic boundary {method.FullName}; found {returns.Length}.");
                }

                targets.Add(site.Name, new StaticTraceTarget(
                    method,
                    method.Body.GetILProcessor(),
                    Call: null,
                    Entry: method.Body.Instructions.First(),
                    Exit: returns[0]));
                continue;
            }

            var calls = method.Body.Instructions
                .Where(instruction => instruction.Operand is MethodReference reference
                    && reference.Name == site.TargetMethodName
                    && reference.DeclaringType.FullName == site.TargetDeclaringType)
                .ToArray();
            if (calls.Length != 1
                || (calls[0].OpCode.Code != Code.Call && calls[0].OpCode.Code != Code.Newobj))
            {
                throw new TraceWeaverException(
                    $"Expected exactly one direct {site.TargetDeclaringType}::{site.TargetMethodName} call in {method.FullName}; found {calls.Length}.");
            }

            targets.Add(site.Name, new StaticTraceTarget(
                method,
                method.Body.GetILProcessor(),
                calls[0],
                Entry: null,
                Exit: null));
        }

        return targets;
    }

    private static bool HasAnyDiagnosticTraceMarker(
        IReadOnlyDictionary<string, AssetTraceTarget> assetTargets,
        IReadOnlyDictionary<string, StaticTraceTarget> staticTargets)
    {
        var markers = AssetTraceSites
            .SelectMany(site => new[] { site.BeforeMarker, site.AfterMarker })
            .Concat(StaticTraceSites.SelectMany(site => new[] { site.BeforeMarker, site.AfterMarker }))
            .ToHashSet(StringComparer.Ordinal);
        return assetTargets.Values.Any(target => target.Method.Body.Instructions.Any(instruction =>
            instruction.OpCode.Code == Code.Ldstr
            && instruction.Operand is string marker
            && markers.Contains(marker)))
            || staticTargets.Values.Any(target => target.Method.Body.Instructions.Any(instruction =>
            instruction.OpCode.Code == Code.Ldstr
            && instruction.Operand is string marker
            && markers.Contains(marker)));
    }

    private static void ValidateCompleteAssetTrace(
        IReadOnlyDictionary<string, AssetTraceTarget> assetTargets,
        IReadOnlyDictionary<string, StaticTraceTarget> staticTargets,
        MethodDefinition logInfo)
    {
        foreach (var site in AssetTraceSites)
        {
            var target = assetTargets[site.Name];
            var beforeCount = target.Method.Body.Instructions.Count(instruction =>
                IsMarkerInstruction(instruction, site.BeforeMarker));
            var afterCount = target.Method.Body.Instructions.Count(instruction =>
                IsMarkerInstruction(instruction, site.AfterMarker));
            if (beforeCount != 1 || afterCount != 1
                || !HasDynamicLogBefore(target.PathLoad, site.BeforeMarker, site.PathKind, logInfo)
                || !HasDynamicLogAfter(target.Call, site.AfterMarker, site.PathKind, logInfo))
            {
                throw new TraceWeaverException(
                    $"Existing asset-trace instrumentation for {site.Name} is incomplete or duplicated; refusing to modify the assembly.");
            }
        }
        foreach (var site in StaticTraceSites)
        {
            var target = staticTargets[site.Name];
            var beforeCount = target.Method.Body.Instructions.Count(instruction =>
                IsMarkerInstruction(instruction, site.BeforeMarker));
            var afterCount = target.Method.Body.Instructions.Count(instruction =>
                IsMarkerInstruction(instruction, site.AfterMarker));
            if (beforeCount != 1 || afterCount != 1 || !HasStaticTrace(target, site, logInfo))
            {
                throw new TraceWeaverException(
                    $"Existing diagnostic instrumentation for {site.Name} is incomplete or duplicated; refusing to modify the assembly.");
            }
        }
    }

    private static void InsertStaticTrace(StaticTraceTarget target, StaticTraceSite site, MethodDefinition logInfo)
    {
        if (site.BoundaryOnly)
        {
            InsertLogCall(target.Processor, target.Entry!, site.BeforeMarker, before: true, logInfo);
            InsertLogCall(target.Processor, target.Exit!, site.AfterMarker, before: true, logInfo);
            return;
        }

        InsertLogCall(target.Processor, target.Call!, site.BeforeMarker, before: true, logInfo);
        InsertLogCall(target.Processor, target.Call!, site.AfterMarker, before: false, logInfo);
    }

    private static bool HasStaticTrace(StaticTraceTarget target, StaticTraceSite site, MethodDefinition logInfo)
    {
        if (site.BoundaryOnly)
        {
            return HasLogSequenceBefore(target.Entry!, site.BeforeMarker, logInfo)
                && HasLogSequenceBefore(target.Exit!, site.AfterMarker, logInfo);
        }

        return HasLogSequenceBefore(target.Call!, site.BeforeMarker, logInfo)
            && HasLogSequenceAfter(target.Call!, site.AfterMarker, logInfo);
    }

    private static MethodReference FindStringConcat(ModuleDefinition module)
    {
        var method = typeof(string).GetMethod(
            nameof(string.Concat),
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            new[] { typeof(string), typeof(string) },
            modifiers: null);
        if (method is null)
        {
            throw new TraceWeaverException("Could not resolve System.String.Concat(System.String,System.String).");
        }

        return module.ImportReference(method);
    }

    private static void InsertDynamicLog(
        ILProcessor processor,
        Instruction anchor,
        string marker,
        AssetTracePathKind pathKind,
        MethodReference concat,
        MethodDefinition logInfo,
        bool before)
    {
        var sequence = new[]
        {
            processor.Create(OpCodes.Ldstr, marker),
            CreatePathLoad(processor, pathKind),
            processor.Create(OpCodes.Call, concat),
            processor.Create(OpCodes.Ldc_I4_2),
            processor.Create(OpCodes.Call, processor.Body.Method.Module.ImportReference(logInfo)),
        };

        if (before)
        {
            foreach (var instruction in sequence)
            {
                processor.InsertBefore(anchor, instruction);
            }

            return;
        }

        var afterAnchor = anchor;
        foreach (var instruction in sequence)
        {
            processor.InsertAfter(afterAnchor, instruction);
            afterAnchor = instruction;
        }
    }

    private static bool HasDynamicLogBefore(
        Instruction pathLoad,
        string marker,
        AssetTracePathKind pathKind,
        MethodDefinition logInfo)
    {
        var logCall = pathLoad.Previous;
        var logLevel = logCall?.Previous;
        var concatCall = logLevel?.Previous;
        var pathClone = concatCall?.Previous;
        var markerInstruction = pathClone?.Previous;
        return IsMarkerInstruction(markerInstruction, marker)
            && IsPathLoad(pathClone, pathKind)
            && IsConcatCall(concatCall)
            && IsLdcI4Two(logLevel)
            && IsLogInfoCall(logCall, logInfo);
    }

    private static bool HasDynamicLogAfter(
        Instruction call,
        string marker,
        AssetTracePathKind pathKind,
        MethodDefinition logInfo)
    {
        var markerInstruction = call.Next;
        var pathClone = markerInstruction?.Next;
        var concatCall = pathClone?.Next;
        var logLevel = concatCall?.Next;
        var logCall = logLevel?.Next;
        return IsMarkerInstruction(markerInstruction, marker)
            && IsPathLoad(pathClone, pathKind)
            && IsConcatCall(concatCall)
            && IsLdcI4Two(logLevel)
            && IsLogInfoCall(logCall, logInfo);
    }

    private static bool IsConcatCall(Instruction? instruction) =>
        instruction?.OpCode.Code == Code.Call
        && instruction.Operand is MethodReference method
        && HasSignature(method, "System.String", "Concat", "System.String", "System.String", "System.String");

    private static Instruction CreatePathLoad(ILProcessor processor, AssetTracePathKind pathKind) =>
        pathKind switch
        {
            AssetTracePathKind.Argument1 => processor.Create(OpCodes.Ldarg_1),
            AssetTracePathKind.Local0 => processor.Create(OpCodes.Ldloc_0),
            AssetTracePathKind.Local1 => processor.Create(OpCodes.Ldloc_1),
            AssetTracePathKind.Local2 => processor.Create(OpCodes.Ldloc_2),
            _ => throw new TraceWeaverException($"Unsupported asset-trace path kind: {pathKind}"),
        };

    private static bool IsPathLoad(Instruction? instruction, AssetTracePathKind pathKind)
    {
        if (instruction is null)
        {
            return false;
        }

        return pathKind switch
        {
            AssetTracePathKind.Argument1 => instruction.OpCode.Code == Code.Ldarg_1
                || ((instruction.OpCode.Code == Code.Ldarg || instruction.OpCode.Code == Code.Ldarg_S)
                    && instruction.Operand is ParameterDefinition parameter
                    && parameter.Index == 1),
            AssetTracePathKind.Local0 => instruction.OpCode.Code == Code.Ldloc_0
                || IsLocalLoad(instruction, 0),
            AssetTracePathKind.Local1 => instruction.OpCode.Code == Code.Ldloc_1
                || IsLocalLoad(instruction, 1),
            AssetTracePathKind.Local2 => instruction.OpCode.Code == Code.Ldloc_2
                || IsLocalLoad(instruction, 2),
            _ => false,
        };
    }

    private static bool IsLocalLoad(Instruction instruction, int index) =>
        (instruction.OpCode.Code == Code.Ldloc || instruction.OpCode.Code == Code.Ldloc_S)
        && instruction.Operand is VariableDefinition variable
        && variable.Index == index;

    private static MethodDefinition FindExecuteDeferred(ModuleDefinition module)
    {
        var type = FindType(module, ExecuteDeferredType);
        if (type is null)
        {
            throw new TraceWeaverException($"Required type is missing: {ExecuteDeferredType}");
        }

        var methods = type.Methods.Where(method => method.Name == "ExecuteDeferred").ToArray();
        if (methods.Length != 1 || !HasSignature(methods[0], ExecuteDeferredType, "ExecuteDeferred", "System.Void"))
        {
            throw new TraceWeaverException(
                $"Expected exactly one public static {ExecuteDeferredType}::ExecuteDeferred() returning System.Void.");
        }

        var method = methods[0];
        if (!method.IsPublic || !method.IsStatic)
        {
            throw new TraceWeaverException(
                $"Unexpected attributes on {ExecuteDeferredType}::ExecuteDeferred(); expected public static.");
        }

        if (!method.HasBody)
        {
            throw new TraceWeaverException($"Required method has no body: {method.FullName}");
        }

        return method;
    }

    private static MethodDefinition FindLogInfo(ModuleDefinition module)
    {
        var type = FindType(module, LogType);
        if (type is null)
        {
            throw new TraceWeaverException($"Required type is missing: {LogType}");
        }

        var methods = type.Methods
            .Where(method => method.Name == "Info")
            .Where(method => HasSignature(method, LogType, "Info", "System.Void", "System.String", "System.Int32"))
            .ToArray();
        if (methods.Length != 1)
        {
            throw new TraceWeaverException(
                $"Expected exactly one public static {LogType}::Info(System.String,System.Int32) returning System.Void.");
        }

        var method = methods[0];
        if (!method.IsPublic || !method.IsStatic)
        {
            throw new TraceWeaverException(
                $"Unexpected attributes on {LogType}::Info(System.String,System.Int32); expected public static.");
        }

        return method;
    }

    private static Dictionary<string, Instruction> ValidateSites(
        MethodDefinition executeDeferred,
        ModuleDefinition module)
    {
        var calls = new Dictionary<string, Instruction>(StringComparer.Ordinal);
        foreach (var site in Sites)
        {
            var matchingNameAndType = executeDeferred.Body.Instructions
                .Where(instruction => instruction.Operand is MethodReference method
                    && method.Name == site.MethodName
                    && method.DeclaringType.FullName == site.DeclaringType)
                .ToArray();

            if (matchingNameAndType.Length != 1)
            {
                throw new TraceWeaverException(
                    $"Expected exactly one {site.DeclaringType}::{site.MethodName}() call in {executeDeferred.FullName}; found {matchingNameAndType.Length}.");
            }

            var instruction = matchingNameAndType[0];
            if (instruction.OpCode.Code != Code.Call
                || instruction.Operand is not MethodReference method
                || !HasSignature(method, site.DeclaringType, site.MethodName, "System.Void"))
            {
                throw new TraceWeaverException(
                    $"Unexpected call shape for {site.DeclaringType}::{site.MethodName}(); expected a direct call with no arguments and a void return.");
            }

            var definition = FindType(module, site.DeclaringType)?.Methods.SingleOrDefault(candidate =>
                candidate.MetadataToken == method.MetadataToken);
            if (definition is null
                || !HasSignature(definition, site.DeclaringType, site.MethodName, "System.Void")
                || !definition.IsStatic
                || definition.IsPrivate != site.IsPrivate
                || (!site.IsPrivate && !definition.IsPublic))
            {
                var visibility = site.IsPrivate ? "private static" : "public static";
                throw new TraceWeaverException(
                    $"Unexpected definition for {site.DeclaringType}::{site.MethodName}(); expected {visibility} with no arguments and a void return.");
            }

            calls.Add(site.Name, instruction);
        }

        return calls;
    }

    private static void ValidateCompleteInstrumentation(
        MethodDefinition executeDeferred,
        IReadOnlyDictionary<string, Instruction> calls,
        MethodDefinition logInfo)
    {
        var markerCounts = Sites
            .SelectMany(site => new[] { site.BeforeMarker, site.AfterMarker })
            .ToDictionary(marker => marker, marker => 0, StringComparer.Ordinal);
        foreach (var instruction in executeDeferred.Body.Instructions)
        {
            if (instruction.OpCode.Code == Code.Ldstr
                && instruction.Operand is string marker
                && markerCounts.ContainsKey(marker))
            {
                markerCounts[marker]++;
            }
        }

        if (markerCounts.Values.Any(count => count != 1))
        {
            var details = string.Join(", ", markerCounts.Select(pair => $"{pair.Key}={pair.Value}"));
            throw new TraceWeaverException(
                $"Existing physical-footprint instrumentation is incomplete or duplicated ({details}); refusing to modify the assembly.");
        }

        foreach (var site in Sites)
        {
            var call = calls[site.Name];
            if (!HasLogSequenceBefore(call, site.BeforeMarker, logInfo)
                || !HasLogSequenceAfter(call, site.AfterMarker, logInfo))
            {
                throw new TraceWeaverException(
                    $"Existing physical-footprint marker for {site.Name} is not immediately adjacent to its call; refusing to modify the assembly.");
            }
        }
    }

    private static bool HasAnyTraceMarker(MethodDefinition method)
    {
        var markers = Sites
            .SelectMany(site => new[] { site.BeforeMarker, site.AfterMarker })
            .ToHashSet(StringComparer.Ordinal);
        return method.Body.Instructions.Any(instruction =>
            instruction.OpCode.Code == Code.Ldstr
            && instruction.Operand is string marker
            && markers.Contains(marker));
    }

    private static bool HasLogSequenceBefore(Instruction call, string marker, MethodDefinition logInfo)
    {
        var before = call.Previous?.Previous?.Previous;
        return before is not null
            && IsMarkerInstruction(before, marker)
            && IsLdcI4Two(before.Next)
            && IsLogInfoCall(before.Next?.Next, logInfo);
    }

    private static bool HasLogSequenceAfter(Instruction call, string marker, MethodDefinition logInfo)
    {
        return IsMarkerInstruction(call.Next, marker)
            && IsLdcI4Two(call.Next?.Next)
            && IsLogInfoCall(call.Next?.Next?.Next, logInfo);
    }

    private static void InsertLogCall(
        ILProcessor processor,
        Instruction target,
        string marker,
        bool before,
        MethodDefinition logInfo)
    {
        var sequence = new[]
        {
            processor.Create(OpCodes.Ldstr, marker),
            processor.Create(OpCodes.Ldc_I4_2),
            processor.Create(OpCodes.Call, processor.Body.Method.Module.ImportReference(logInfo)),
        };

        if (before)
        {
            foreach (var instruction in sequence)
            {
                processor.InsertBefore(target, instruction);
            }

            return;
        }

        var anchor = target;
        foreach (var instruction in sequence)
        {
            processor.InsertAfter(anchor, instruction);
            anchor = instruction;
        }
    }

    private static bool IsMarkerInstruction(Instruction? instruction, string marker) =>
        instruction?.OpCode.Code == Code.Ldstr && instruction.Operand is string value && value == marker;

    private static bool IsLdcI4Two(Instruction? instruction) =>
        instruction?.OpCode.Code == Code.Ldc_I4_2
        || (instruction?.OpCode.Code == Code.Ldc_I4 && instruction.Operand is int value && value == 2);

    private static bool IsLogInfoCall(Instruction? instruction, MethodDefinition logInfo) =>
        instruction?.OpCode.Code == Code.Call
        && instruction.Operand is MethodReference method
        && HasSignature(method, LogType, "Info", "System.Void", "System.String", "System.Int32")
        && method.MetadataToken == logInfo.MetadataToken;

    private static bool HasSignature(
        MethodReference method,
        string declaringType,
        string name,
        string returnType,
        params string[] parameterTypes)
    {
        return method.DeclaringType.FullName == declaringType
            && method.Name == name
            && method.ReturnType.FullName == returnType
            && method.Parameters.Count == parameterTypes.Length
            && method.Parameters.Select(parameter => parameter.ParameterType.FullName)
                .SequenceEqual(parameterTypes, StringComparer.Ordinal)
            && !method.HasGenericParameters;
    }

    private static TypeDefinition? FindType(ModuleDefinition module, string fullName)
    {
        foreach (var type in module.Types)
        {
            var found = FindType(type, fullName);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static TypeDefinition? FindType(TypeDefinition type, string fullName)
    {
        if (type.FullName == fullName)
        {
            return type;
        }

        foreach (var nested in type.NestedTypes)
        {
            var found = FindType(nested, fullName);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string GetRequiredFullPath(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new TraceWeaverException($"The {description} assembly path is empty.");
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new TraceWeaverException($"The {description} assembly path is invalid: {path}");
        }
    }

    private static void CopyIfNeeded(byte[] inputBytes, string inputPath, string outputPath)
    {
        if (PathsEqual(inputPath, outputPath))
        {
            return;
        }

        WriteAtomically(outputPath, inputBytes);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static void WriteAtomically(string outputPath, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new TraceWeaverException($"The output path has no parent directory: {outputPath}");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TraceWeaverException($"Could not write output assembly {outputPath}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Preserve the original write error, if any. The temporary name is
                // unique and will be cleaned by the runner's workspace lifecycle.
            }
        }
    }

    private sealed record AssetTraceTarget(
        MethodDefinition Method,
        ILProcessor Processor,
        Instruction Call,
        Instruction PathLoad);

    private sealed record StaticTraceTarget(
        MethodDefinition Method,
        ILProcessor Processor,
        Instruction? Call,
        Instruction? Entry,
        Instruction? Exit);

    private enum AssetTracePathKind
    {
        Argument1,
        Local0,
        Local1,
        Local2,
    }

    private sealed record AssetTraceSite(
        string Name,
        string DeclaringType,
        string MethodName,
        string LoaderMethodName,
        string StageName,
        AssetTracePathKind PathKind)
    {
        public string BeforeMarker => $"PHYS stage={StageName}_begin path=";

        public string AfterMarker => $"PHYS stage={StageName}_end path=";
    }

    private sealed record StaticTraceSite(
        string Name,
        string DeclaringType,
        string MethodName,
        string? TargetDeclaringType,
        string? TargetMethodName,
        string StageName,
        bool BoundaryOnly = false)
    {
        public string BeforeMarker => $"PHYS stage={StageName}_begin";

        public string AfterMarker => $"PHYS stage={StageName}_end";
    }

    private sealed record TraceSite(
        string Name,
        string DeclaringType,
        string MethodName,
        string BeforeName,
        string AfterName,
        bool IsPrivate)
    {
        public string BeforeMarker => $"PHYS stage={BeforeName}";

        public string AfterMarker => $"PHYS stage={AfterName}";
    }
}
