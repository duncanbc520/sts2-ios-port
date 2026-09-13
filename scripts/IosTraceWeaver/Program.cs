using System;

namespace IosTraceWeaver;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = WeaverOptions.Parse(args);
            var result = TraceWeaver.Weave(options.InputPath, options.OutputPath);
            Console.WriteLine(result.Changed
                ? $"Wove {result.MarkerCount} physical-footprint markers into {options.OutputPath}."
                : $"Physical-footprint markers already present in {options.InputPath}; no IL changes made.");
            return 0;
        }
        catch (TraceWeaverException ex)
        {
            Console.Error.WriteLine($"IosTraceWeaver: {ex.Message}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"IosTraceWeaver: unexpected failure: {ex.Message}");
            return 1;
        }
    }
}

internal sealed record WeaverOptions(string InputPath, string OutputPath)
{
    public static WeaverOptions Parse(string[] args)
    {
        if (args is null)
        {
            throw new TraceWeaverException(Usage());
        }

        string? inputPath = null;
        string? outputPath = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input":
                    inputPath = ReadValue(args, ref index, "--input");
                    break;
                case "--output":
                    outputPath = ReadValue(args, ref index, "--output");
                    break;
                case "--help":
                case "-h":
                    throw new TraceWeaverException(Usage());
                default:
                    throw new TraceWeaverException($"Unknown argument '{args[index]}'.\n{Usage()}");
            }
        }

        if (string.IsNullOrWhiteSpace(inputPath) || string.IsNullOrWhiteSpace(outputPath))
        {
            throw new TraceWeaverException($"Both --input and --output are required.\n{Usage()}");
        }

        return new WeaverOptions(inputPath, outputPath);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new TraceWeaverException($"{option} requires a path.\n{Usage()}");
        }

        index++;
        return args[index];
    }

    private static string Usage() =>
        "Usage: IosTraceWeaver --input <sts2.dll> --output <sts2.dll>";
}
