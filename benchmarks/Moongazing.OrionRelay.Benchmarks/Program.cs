using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

/// <summary>Entry point marker for the BenchmarkDotNet switcher.</summary>
public partial class Program
{
    private Program()
    {
    }
}
