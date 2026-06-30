using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using Xunit;

namespace AutoLog.Tests;

public class LogGeneratorTests
{
    private const string LoggingStub = @"
namespace Microsoft.Extensions.Logging
{
    public enum LogLevel
    {
        Trace,
        Debug,
        Information,
        Warning,
        Error,
        Critical,
        None
    }

    public readonly struct EventId
    {
        public EventId(int id, string? name = null)
        {
            Id = id;
            Name = name;
        }

        public int Id { get; }
        public string? Name { get; }
    }

    public interface ILogger
    {
        void Log<TState>(LogLevel logLevel, EventId eventId, TState state, System.Exception? exception, System.Func<TState, System.Exception?, string> formatter);
    }

    public interface ILogger<TCategoryName> : ILogger
    {
    }

    public static class LoggerMessage
    {
        public static System.Action<ILogger, System.Exception?> Define(LogLevel level, EventId eventId, string formatString) => (_, _) => { };
        public static System.Action<ILogger, T1, System.Exception?> Define<T1>(LogLevel level, EventId eventId, string formatString) => (_, _, _) => { };
        public static System.Action<ILogger, T1, T2, System.Exception?> Define<T1, T2>(LogLevel level, EventId eventId, string formatString) => (_, _, _, _) => { };
        public static System.Action<ILogger, T1, T2, T3, System.Exception?> Define<T1, T2, T3>(LogLevel level, EventId eventId, string formatString) => (_, _, _, _, _) => { };
        public static System.Action<ILogger, T1, T2, T3, T4, System.Exception?> Define<T1, T2, T3, T4>(LogLevel level, EventId eventId, string formatString) => (_, _, _, _, _, _) => { };
        public static System.Action<ILogger, T1, T2, T3, T4, T5, System.Exception?> Define<T1, T2, T3, T4, T5>(LogLevel level, EventId eventId, string formatString) => (_, _, _, _, _, _, _) => { };
        public static System.Action<ILogger, T1, T2, T3, T4, T5, T6, System.Exception?> Define<T1, T2, T3, T4, T5, T6>(LogLevel level, EventId eventId, string formatString) => (_, _, _, _, _, _, _, _) => { };
    }

    public static class LoggerExtensions
    {
        public static void Log(this ILogger logger, LogLevel logLevel, EventId eventId, System.Exception? exception, string message, params object?[] args)
        {
        }
    }
}
";

    private static Dictionary<string, string> RunGenerator(string userSource, out ImmutableArray<Diagnostic> diagnostics)
    {
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        };

        try { refs.Add(MetadataReference.CreateFromFile(Assembly.Load("System.Runtime").Location)); } catch { }
        try { refs.Add(MetadataReference.CreateFromFile(Assembly.Load("netstandard").Location)); } catch { }
        try { refs.Add(MetadataReference.CreateFromFile(Assembly.Load("System.Linq").Location)); } catch { }

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            new[]
            {
                CSharpSyntaxTree.ParseText(LoggingStub),
                CSharpSyntaxTree.ParseText(userSource)
            },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new AutoLogGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out diagnostics);

        return driver.GetRunResult().GeneratedTrees
            .ToDictionary(
                tree => System.IO.Path.GetFileName(tree.FilePath),
                tree => tree.GetText().ToString());
    }

    private static string GetGeneratedClassSource(Dictionary<string, string> sources)
        => sources.Single(source => source.Key.EndsWith(".AutoLog.g.cs", System.StringComparison.Ordinal)).Value;

    [Fact]
    public void Attributes_FileIsGenerated()
    {
        var sources = RunGenerator(string.Empty, out _);
        Assert.True(sources.ContainsKey("AutoLog.Attributes.g.cs"));
    }

    [Fact]
    public void SingleLogMethod_GeneratesDefineField()
    {
        var sources = RunGenerator(@"
using AutoLog;
using Microsoft.Extensions.Logging;

public partial class OrderService
{
    private readonly ILogger<OrderService> _logger;

    [Log(LogLevel.Information, ""Processing order {OrderId}"")]
    partial void LogProcessingOrder(int orderId);
}", out _);

        var source = GetGeneratedClassSource(sources);
        Assert.Contains("LoggerMessage.Define<global::System.Int32>", source);
        Assert.Contains("_logProcessingOrderAction(_logger, orderId, null);", source);
    }

    [Fact]
    public void MultipleLogMethods_GeneratesAllMethods()
    {
        var sources = RunGenerator(@"
using AutoLog;
using Microsoft.Extensions.Logging;

public partial class OrderService
{
    private readonly ILogger _logger;

    [Log(LogLevel.Information, ""Processing order {OrderId}"")]
    partial void LogProcessingOrder(int orderId);

    [Log(LogLevel.Warning, ""Order {OrderId} not found"")]
    partial void LogOrderNotFound(int orderId);

    [Log(LogLevel.Error, ""Failed to process order {OrderId}"")]
    partial void LogProcessingFailed(int orderId, System.Exception ex);
}", out _);

        var source = GetGeneratedClassSource(sources);
        Assert.Contains("_logProcessingOrderAction", source);
        Assert.Contains("_logOrderNotFoundAction", source);
        Assert.Contains("_logProcessingFailedAction", source);
        Assert.Contains("new global::Microsoft.Extensions.Logging.EventId(1, \"LogProcessingOrder\")", source);
        Assert.Contains("new global::Microsoft.Extensions.Logging.EventId(2, \"LogOrderNotFound\")", source);
        Assert.Contains("new global::Microsoft.Extensions.Logging.EventId(3, \"LogProcessingFailed\")", source);
    }

    [Fact]
    public void ExceptionParameter_MappedToExceptionSlot()
    {
        var sources = RunGenerator(@"
using AutoLog;
using Microsoft.Extensions.Logging;

public partial class OrderService
{
    private readonly ILogger<OrderService> _logger;

    [Log(LogLevel.Error, ""Failed to process order {OrderId}"")]
    partial void LogProcessingFailed(int orderId, System.Exception ex);
}", out _);

        var source = GetGeneratedClassSource(sources);
        Assert.Contains("LoggerMessage.Define<global::System.Int32>", source);
        Assert.DoesNotContain("LoggerMessage.Define<global::System.Int32, global::System.Exception>", source);
        Assert.Contains("_logProcessingFailedAction(_logger, orderId, ex);", source);
    }

    [Fact]
    public void NoLogMethods_NoFileGenerated()
    {
        var sources = RunGenerator(@"
public partial class OrderService
{
    public void Process()
    {
    }
}", out _);

        Assert.Equal(new[] { "AutoLog.Attributes.g.cs" }, sources.Keys.OrderBy(static key => key).ToArray());
    }

    [Fact]
    public void AL002_NoLogger_DiagnosticReported()
    {
        RunGenerator(@"
using AutoLog;
using Microsoft.Extensions.Logging;

public partial class OrderService
{
    [Log(LogLevel.Information, ""Processing order {OrderId}"")]
    partial void LogProcessingOrder(int orderId);
}", out var diagnostics);

        var diagnostic = Assert.Single(diagnostics.Where(static diagnostic => diagnostic.Id == "AL002"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }
}
