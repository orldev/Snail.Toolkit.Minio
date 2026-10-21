using BenchmarkDotNet.Running;

namespace Snail.Toolkit.Minio.Benchmarks;

/// <summary>Runs the benchmarks named on the command line, or offers all of them.</summary>
public static class Program
{
    /// <summary>Hands the command line to BenchmarkDotNet.</summary>
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
