using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        using var assembly = AssemblyDefinition.ReadAssembly(inputStream, new ReaderParameters
        {
            InMemory = true,
            ReadSymbols = false,
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
