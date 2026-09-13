using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IosTraceWeaver;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace IosTraceWeaver.Tests;

public sealed class TraceWeaverTests
{
    [Fact]
    public void WeavesAllStagesAndSecondPassIsAByteNoOp()
    {
        using var fixture = Fixture.Create();
        var inputPath = fixture.Path("input.dll");
        var outputPath = fixture.Path("output.dll");
        fixture.Write(inputPath);

        var first = TraceWeaver.Weave(inputPath, outputPath);
        var firstBytes = File.ReadAllBytes(outputPath);
        var second = TraceWeaver.Weave(outputPath, outputPath);
        var secondBytes = File.ReadAllBytes(outputPath);

        Assert.True(first.Changed);
        Assert.Equal(6, first.MarkerCount);
        Assert.False(second.Changed);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(
            new[]
            {
                "PHYS stage=before_atlas_load",
                "PHYS stage=after_atlas_load",
                "PHYS stage=before_model_preload",
                "PHYS stage=after_model_preload",
                "PHYS stage=before_prewarm_jit",
                "PHYS stage=after_prewarm_jit",
            },
            ReadMarkers(outputPath));
    }

    [Fact]
    public void RejectsPartialInstrumentationWithoutWritingOutput()
    {
        using var fixture = Fixture.Create(orphanMarker: true);
        var inputPath = fixture.Path("input.dll");
        var outputPath = fixture.Path("output.dll");
        fixture.Write(inputPath);

        var error = Assert.Throws<TraceWeaverException>(() => TraceWeaver.Weave(inputPath, outputPath));

        Assert.Contains("incomplete or duplicated", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void RejectsAmbiguousTargetCallWithoutWritingOutput()
    {
        using var fixture = Fixture.Create(duplicateAtlasCall: true);
        var inputPath = fixture.Path("input.dll");
        var outputPath = fixture.Path("output.dll");
        fixture.Write(inputPath);

        var error = Assert.Throws<TraceWeaverException>(() => TraceWeaver.Weave(inputPath, outputPath));

        Assert.Contains("exactly one", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void RejectsWrongLogSignatureWithoutWritingOutput()
    {
        using var fixture = Fixture.Create(wrongLogSignature: true);
        var inputPath = fixture.Path("input.dll");
        var outputPath = fixture.Path("output.dll");
        fixture.Write(inputPath);

        var error = Assert.Throws<TraceWeaverException>(() => TraceWeaver.Weave(inputPath, outputPath));

        Assert.Contains("Log::Info(System.String,System.Int32)", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public void AssetTraceWeavesPathBoundariesAndSecondPassIsAByteNoOp()
    {
        using var fixture = Fixture.Create(assetTraceFixture: true);
        var inputPath = fixture.Path("input.dll");
        var outputPath = fixture.Path("output.dll");
        fixture.Write(inputPath);

        var first = TraceWeaver.WeaveAssetTrace(inputPath, outputPath);
        var firstBytes = File.ReadAllBytes(outputPath);
        var second = TraceWeaver.WeaveAssetTrace(outputPath, outputPath);
        var secondBytes = File.ReadAllBytes(outputPath);

        Assert.True(first.Changed);
        Assert.Equal(20, first.MarkerCount);
        Assert.False(second.Changed);
        Assert.Equal(firstBytes, secondBytes);
        Assert.Equal(20, ReadAssetMarkers(outputPath).Count);
    }

    private static IReadOnlyList<string> ReadMarkers(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var method = assembly.MainModule
            .GetType("MegaCrit.Sts2.Core.Helpers.OneTimeInitialization")!
            .Methods.Single(candidate => candidate.Name == "ExecuteDeferred");
        return method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand as string)
            .Where(value => value is not null && value.StartsWith("PHYS stage=", StringComparison.Ordinal))
            .Cast<string>()
            .ToArray();
    }

    private static IReadOnlyList<string> ReadAssetMarkers(string path)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        return AllTypes(assembly.MainModule.Types)
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions)
            .Where(instruction => instruction.OpCode.Code == Code.Ldstr)
            .Select(instruction => instruction.Operand as string)
            .Where(value => value is not null && value.StartsWith("PHYS stage=", StringComparison.Ordinal))
            .Cast<string>()
            .ToArray();
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types)
        {
            yield return type;
            foreach (var nested in AllTypes(type.NestedTypes))
            {
                yield return nested;
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory;
        private readonly AssemblyDefinition _assembly;
        private Fixture(AssemblyDefinition assembly, string directory)
        {
            _assembly = assembly;
            _directory = directory;
        }

        public static Fixture Create(
            bool orphanMarker = false,
            bool duplicateAtlasCall = false,
            bool wrongLogSignature = false,
            bool assetTraceFixture = false)
        {
            var assembly = AssemblyDefinition.CreateAssembly(
                new AssemblyNameDefinition("sts2", new Version(1, 0, 0, 0)),
                "sts2",
                ModuleKind.Dll);
            var module = assembly.MainModule;

            var logType = AddStaticType(module, "MegaCrit.Sts2.Core.Logging", "Log");
            if (wrongLogSignature)
            {
                AddMethod(logType, "Info", module.TypeSystem.Void, module.TypeSystem.String);
            }
            else
            {
                AddMethod(logType, "Info", module.TypeSystem.Void, module.TypeSystem.String, module.TypeSystem.Int32);
            }

            var atlasType = AddStaticType(module, "MegaCrit.Sts2.Core.Assets", "AtlasManager");
            var atlasMethod = AddMethod(atlasType, "LoadAllAtlases", module.TypeSystem.Void);
            var modelType = AddStaticType(module, "MegaCrit.Sts2.Core.Models", "ModelDb");
            var modelMethod = AddMethod(modelType, "Preload", module.TypeSystem.Void);
            var initializationType = AddStaticType(module, "MegaCrit.Sts2.Core.Helpers", "OneTimeInitialization");
            var prewarmMethod = AddMethod(
                initializationType,
                "PrewarmJit",
                module.TypeSystem.Void,
                attributes: MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig);
            var executeDeferred = AddMethod(initializationType, "ExecuteDeferred", module.TypeSystem.Void);
            var il = executeDeferred.Body.GetILProcessor();
            il.Append(il.Create(OpCodes.Call, atlasMethod));
            if (duplicateAtlasCall)
            {
                il.Append(il.Create(OpCodes.Call, atlasMethod));
            }

            il.Append(il.Create(OpCodes.Call, modelMethod));
            il.Append(il.Create(OpCodes.Call, prewarmMethod));
            if (orphanMarker)
            {
                il.Append(il.Create(OpCodes.Ldstr, "PHYS stage=before_atlas_load"));
            }

            il.Append(il.Create(OpCodes.Ret));
            if (assetTraceFixture)
            {
                AddAssetTraceFixture(module);
            }
            var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "IosTraceWeaverTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return new Fixture(assembly, directory);
        }

        public string Path(string fileName) => System.IO.Path.Combine(_directory, fileName);

        public void Write(string path) => _assembly.Write(path);

        public void Dispose()
        {
            _assembly.Dispose();
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch
            {
                // The test fixture is temporary; preserve the original assertion if cleanup is delayed.
            }
        }

        private static TypeDefinition AddStaticType(ModuleDefinition module, string @namespace, string name)
        {
            var type = new TypeDefinition(
                @namespace,
                name,
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
                module.TypeSystem.Object);
            module.Types.Add(type);
            return type;
        }

        private static TypeDefinition AddClassType(ModuleDefinition module, string @namespace, string name)
        {
            var type = new TypeDefinition(
                @namespace,
                name,
                TypeAttributes.Public | TypeAttributes.Class,
                module.TypeSystem.Object);
            module.Types.Add(type);
            return type;
        }

        private static void AddAssetTraceFixture(ModuleDefinition module)
        {
            var conditionalFormatterType = AddClassType(module, "SmartFormat.Extensions", "ConditionalFormatter");
            var conditionalFormatterCtor = AddInstanceMethod(conditionalFormatterType, ".ctor", module.TypeSystem.Void);
            var initializationType = module.GetType("MegaCrit.Sts2.Core.Helpers.OneTimeInitialization")!;
            var executeDeferred = initializationType.Methods.Single(method => method.Name == "ExecuteDeferred");
            var executeIl = executeDeferred.Body.GetILProcessor();
            var executeReturn = executeDeferred.Body.Instructions.Last();
            executeIl.InsertBefore(executeReturn, executeIl.Create(OpCodes.Newobj, conditionalFormatterCtor));
            executeIl.InsertBefore(executeReturn, executeIl.Create(OpCodes.Pop));

            var loaderType = AddStaticType(module, "Godot", "ResourceLoader");
            var load = AddMethod(loaderType, "Load", module.TypeSystem.Object, module.TypeSystem.String);
            var threadedRequest = AddMethod(
                loaderType,
                "LoadThreadedRequest",
                module.TypeSystem.Int32,
                module.TypeSystem.String,
                module.TypeSystem.String,
                module.TypeSystem.Boolean,
                module.TypeSystem.Int64);
            var threadedGet = AddMethod(loaderType, "LoadThreadedGet", module.TypeSystem.Object, module.TypeSystem.String);

            var assetCacheType = AddStaticType(module, "MegaCrit.Sts2.Core.Assets", "AssetCache");
            var assetLoad = AddInstanceMethod(assetCacheType, "LoadAsset", module.TypeSystem.Object, module.TypeSystem.String);
            SetBody(assetLoad, module, il =>
            {
                il.Append(il.Create(OpCodes.Ldarg_1));
                il.Append(il.Create(OpCodes.Call, load));
                il.Append(il.Create(OpCodes.Ret));
            });

            var loadingSessionType = AddStaticType(module, "MegaCrit.Sts2.Core.Assets", "AssetLoadingSession");
            var process = AddInstanceMethod(loadingSessionType, "ProcessLoadingQueue", module.TypeSystem.Void);
            AddLocal(process, module.TypeSystem.String);
            SetBody(process, module, il =>
            {
                il.Append(il.Create(OpCodes.Ldstr, "res://threaded.ctex"));
                il.Append(il.Create(OpCodes.Stloc_0));
                il.Append(il.Create(OpCodes.Ldloc_0));
                il.Append(il.Create(OpCodes.Ldstr, ""));
                il.Append(il.Create(OpCodes.Ldc_I4_0));
                il.Append(il.Create(OpCodes.Ldc_I4_1));
                il.Append(il.Create(OpCodes.Conv_I8));
                il.Append(il.Create(OpCodes.Call, threadedRequest));
                il.Append(il.Create(OpCodes.Pop));
                il.Append(il.Create(OpCodes.Ret));
            });

            var finalize = AddInstanceMethod(loadingSessionType, "FinalizeLoading", module.TypeSystem.Void);
            AddLocal(finalize, module.TypeSystem.String);
            SetBody(finalize, module, il =>
            {
                il.Append(il.Create(OpCodes.Ldstr, "res://finalize.ctex"));
                il.Append(il.Create(OpCodes.Stloc_0));
                il.Append(il.Create(OpCodes.Ldloc_0));
                il.Append(il.Create(OpCodes.Call, threadedGet));
                il.Append(il.Create(OpCodes.Pop));
                il.Append(il.Create(OpCodes.Ret));
            });

            var check = AddInstanceMethod(loadingSessionType, "CheckLoadingStatus", module.TypeSystem.Void);
            AddLocal(check, module.TypeSystem.String);
            AddLocal(check, module.TypeSystem.String);
            AddLocal(check, module.TypeSystem.String);
            SetBody(check, module, il =>
            {
                il.Append(il.Create(OpCodes.Ldstr, "res://fallback.ctex"));
                il.Append(il.Create(OpCodes.Stloc_2));
                il.Append(il.Create(OpCodes.Ldloc_2));
                il.Append(il.Create(OpCodes.Call, load));
                il.Append(il.Create(OpCodes.Pop));
                il.Append(il.Create(OpCodes.Ret));
            });

            var fontManagerType = AddStaticType(module, "MegaCrit.Sts2.Core.Localization.Fonts", "FontManager");
            var fontLoad = AddMethod(fontManagerType, "GetFontForLanguage", module.TypeSystem.Object, module.TypeSystem.String);
            AddLocal(fontLoad, module.TypeSystem.String);
            AddLocal(fontLoad, module.TypeSystem.String);
            SetBody(fontLoad, module, il =>
            {
                il.Append(il.Create(OpCodes.Ldstr, "res://fonts/test.fontdata"));
                il.Append(il.Create(OpCodes.Stloc_1));
                il.Append(il.Create(OpCodes.Ldloc_1));
                il.Append(il.Create(OpCodes.Call, load));
                il.Append(il.Create(OpCodes.Pop));
                il.Append(il.Create(OpCodes.Ldnull));
                il.Append(il.Create(OpCodes.Ret));
            });

            var preloadManagerType = AddStaticType(module, "MegaCrit.Sts2.Core.Assets", "PreloadManager");
            var commonAndMainMenuAssets = AddMethod(
                preloadManagerType,
                "LoadCommonAndMainMenuAssets",
                module.TypeSystem.Object);
            var taskAwaiterType = AddStaticType(module, "System.Runtime.CompilerServices", "TaskAwaiter");
            var taskAwaiterGetResult = AddMethod(taskAwaiterType, "GetResult", module.TypeSystem.Void);
            var nGameType = AddClassType(module, "MegaCrit.Sts2.Core.Nodes", "NGame");
            var stateMachineType = new TypeDefinition(
                "",
                "<LoadDeferredStartupAssetsAsync>d__136",
                TypeAttributes.NestedPrivate | TypeAttributes.Class,
                module.TypeSystem.Object);
            nGameType.NestedTypes.Add(stateMachineType);
            var moveNext = AddInstanceMethod(stateMachineType, "MoveNext", module.TypeSystem.Void);
            SetBody(moveNext, module, il =>
            {
                il.Append(il.Create(OpCodes.Call, commonAndMainMenuAssets));
                il.Append(il.Create(OpCodes.Pop));
                il.Append(il.Create(OpCodes.Call, taskAwaiterGetResult));
                il.Append(il.Create(OpCodes.Ret));
            });

            var languageDropdownType = AddClassType(module, "MegaCrit.Sts2.Core.Nodes.Screens.Settings", "NLanguageDropdown");
            var populateOptions = AddMethod(languageDropdownType, "PopulateOptions", module.TypeSystem.Void);
            populateOptions.Body.GetILProcessor().InsertBefore(
                populateOptions.Body.Instructions.Last(),
                Instruction.Create(OpCodes.Nop));
            var languageItemType = AddClassType(module, "MegaCrit.Sts2.Core.Nodes.Screens.Settings", "NLanguageDropdownItem");
            var init = AddInstanceMethod(languageItemType, "Init", module.TypeSystem.Void, module.TypeSystem.String);
            init.Body.GetILProcessor().InsertBefore(
                init.Body.Instructions.Last(),
                Instruction.Create(OpCodes.Nop));
        }

        private static MethodDefinition AddInstanceMethod(
            TypeDefinition type,
            string name,
            TypeReference returnType,
            params TypeReference[] parameterTypes)
        {
            return AddMethod(
                type,
                name,
                returnType,
                MethodAttributes.Public | MethodAttributes.HideBySig,
                parameterTypes);
        }

        private static void AddLocal(MethodDefinition method, TypeReference type) =>
            method.Body.Variables.Add(new VariableDefinition(type));

        private static void SetBody(
            MethodDefinition method,
            ModuleDefinition module,
            Action<ILProcessor> append)
        {
            method.Body.Instructions.Clear();
            append(method.Body.GetILProcessor());
        }

        private static MethodDefinition AddMethod(
            TypeDefinition type,
            string name,
            TypeReference returnType,
            params TypeReference[] parameterTypes)
        {
            return AddMethod(type, name, returnType, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, parameterTypes);
        }

        private static MethodDefinition AddMethod(
            TypeDefinition type,
            string name,
            TypeReference returnType,
            MethodAttributes attributes,
            params TypeReference[] parameterTypes)
        {
            var method = new MethodDefinition(name, attributes, returnType);
            foreach (var parameterType in parameterTypes)
            {
                method.Parameters.Add(new ParameterDefinition(parameterType));
            }

            method.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
            type.Methods.Add(method);
            return method;
        }
    }
}
