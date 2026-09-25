using System.Reflection;

namespace EmployeeMonitoring.Tests;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Any(arg => arg.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
        {
            return await DemoMode.RunAsync(args).ConfigureAwait(false);
        }

        return await TestRunner.RunAsync(Assembly.GetExecutingAssembly(), args).ConfigureAwait(false);
    }
}
