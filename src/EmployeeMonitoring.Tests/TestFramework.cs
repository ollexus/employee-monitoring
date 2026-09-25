using System.Diagnostics;
using System.Reflection;

namespace EmployeeMonitoring.Tests;

/// <summary>Помечает статический метод как тест. Метод может быть void или возвращать Task.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
}

/// <summary>Минимальный набор проверок без внешних зависимостей.</summary>
public static class Assert
{
    public static void True(bool condition, string message = "условие не выполнено")
    {
        if (!condition)
        {
            throw new AssertionException(message);
        }
    }

    public static void False(bool condition, string message = "условие неожиданно выполнено") => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException(message ?? $"ожидалось <{Describe(expected)}>, получено <{Describe(actual)}>");
        }
    }

    public static void NotEqual<T>(T unexpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(unexpected, actual))
        {
            throw new AssertionException(message ?? $"значение не должно быть равным <{Describe(actual)}>");
        }
    }

    public static void NotNull(object? value, string message = "значение не должно быть null")
    {
        if (value is null)
        {
            throw new AssertionException(message);
        }
    }

    public static void Null(object? value, string message = "значение должно быть null")
    {
        if (value is not null)
        {
            throw new AssertionException(message);
        }
    }

    public static void Contains(string haystack, string needle, string? message = null)
    {
        if (!haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            throw new AssertionException(message ?? $"в строке «{Truncate(haystack)}» нет «{needle}»");
        }
    }

    public static void BytesEqual(byte[] expected, byte[] actual, string message = "байты не совпадают")
    {
        if (expected.Length != actual.Length)
        {
            throw new AssertionException($"{message}: длины {expected.Length} и {actual.Length}");
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                throw new AssertionException($"{message}: различие в позиции {i}");
            }
        }
    }

    public static void InRange(long value, long min, long max, string message = "значение вне диапазона")
    {
        if (value < min || value > max)
        {
            throw new AssertionException($"{message}: {value} не в [{min}..{max}]");
        }
    }

    public static TException Throws<TException>(Action action, string? message = null) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new AssertionException(message ?? $"ожидалось {typeof(TException).Name}, получено {ex.GetType().Name}: {ex.Message}");
        }

        throw new AssertionException(message ?? $"ожидалось исключение {typeof(TException).Name}, но его не было");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string? message = null) where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            throw new AssertionException(message ?? $"ожидалось {typeof(TException).Name}, получено {ex.GetType().Name}: {ex.Message}");
        }

        throw new AssertionException(message ?? $"ожидалось исключение {typeof(TException).Name}, но его не было");
    }

    public static void Fail(string message) => throw new AssertionException(message);

    private static string Describe(object? value) => value switch
    {
        null => "null",
        string text => text,
        _ => value.ToString() ?? "?"
    };

    private static string Truncate(string value) => value.Length <= 120 ? value : value[..120] + "…";
}

public sealed class AssertionException : Exception
{
    public AssertionException(string message) : base(message)
    {
    }
}

public sealed record TestOutcome(string Name, bool Passed, double Milliseconds, string? Error, bool Skipped);

public static class TestRunner
{
    public static async Task<int> RunAsync(Assembly assembly, string[] args)
    {
        string? filter = null;
        bool listOnly = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--filter" when i + 1 < args.Length:
                    filter = args[++i];
                    break;
                case "--list":
                    listOnly = true;
                    break;
                case "--help":
                    Console.WriteLine("Использование: EmployeeMonitoring.Tests [--filter <подстрока>] [--list]");
                    return 0;
            }
        }

        List<(MethodInfo Method, string Name)> tests = assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.GetCustomAttribute<TestAttribute>() is not null)
            .Select(method => (Method: method, Name: $"{method.DeclaringType?.Name}.{method.Name}"))
            .Where(test => filter is null || test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(test => test.Name, StringComparer.Ordinal)
            .ToList();

        if (listOnly)
        {
            foreach ((_, string name) in tests)
            {
                Console.WriteLine(name);
            }

            return 0;
        }

        Console.WriteLine($"Тестов: {tests.Count} (фильтр: {filter ?? "нет"})");
        Console.WriteLine(new string('-', 72));

        var outcomes = new List<TestOutcome>(tests.Count);
        foreach ((MethodInfo method, string name) in tests)
        {
            var stopwatch = Stopwatch.StartNew();
            string? error = null;
            bool skipped = false;

            try
            {
                object? result = method.Invoke(null, null);
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                }
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                if (ex.InnerException is SkipException)
                {
                    skipped = true;
                }
                else
                {
                    error = Describe(ex.InnerException);
                }
            }
            catch (Exception ex)
            {
                error = Describe(ex);
            }

            stopwatch.Stop();
            outcomes.Add(new TestOutcome(name, error is null && !skipped, stopwatch.Elapsed.TotalMilliseconds, error, skipped));
            Report(outcomes[^1]);
        }

        Console.WriteLine(new string('-', 72));
        int passed = outcomes.Count(o => o.Passed);
        int skippedCount = outcomes.Count(o => o.Skipped);
        int failed = outcomes.Count(o => !o.Passed && !o.Skipped);

        foreach (TestOutcome outcome in outcomes.Where(o => !o.Passed && !o.Skipped))
        {
            Console.WriteLine($"FAIL  {outcome.Name}: {outcome.Error}");
        }

        Console.WriteLine($"Итог: успешно {passed}, пропущено {skippedCount}, провалено {failed} " +
                          $"({outcomes.Sum(o => o.Milliseconds) / 1000:0.00} с)");

        return failed == 0 ? 0 : 1;
    }

    private static void Report(TestOutcome outcome)
    {
        if (outcome.Skipped)
        {
            Write(ConsoleColor.DarkYellow, $"SKIP  {outcome.Name} ({outcome.Milliseconds,6:0} мс)");
            return;
        }

        if (outcome.Passed)
        {
            Write(ConsoleColor.Green, $"PASS  {outcome.Name} ({outcome.Milliseconds,6:0} мс)");
        }
        else
        {
            Write(ConsoleColor.Red, $"FAIL  {outcome.Name} ({outcome.Milliseconds,6:0} мс)");
            Write(ConsoleColor.DarkRed, $"      {outcome.Error}");
        }
    }

    private static void Write(ConsoleColor color, string text)
    {
        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    private static string Describe(Exception exception) => exception is AssertionException
        ? exception.Message
        : $"{exception.GetType().Name}: {exception.Message}";
}

/// <summary>Тест помечается пропущенным, если среда не позволяет его выполнить.</summary>
public sealed class SkipException : Exception
{
    public SkipException(string reason) : base(reason)
    {
    }

    public static void Because(string reason) => throw new SkipException(reason);
}
