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
            bool wrongLogSignature = false)
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
