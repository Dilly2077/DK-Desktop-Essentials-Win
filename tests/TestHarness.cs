using System.Text.Json;

internal static class TestHarness
{
    public static async Task<int> RunAsync(string suite, IEnumerable<(string Name, Func<Task> Run)> tests, string[] args)
    {
        var results = new List<object>();
        var failed = 0;
        foreach (var test in tests)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await test.Run().WaitAsync(TimeSpan.FromSeconds(30));
                results.Add(new { name = test.Name, status = "passed", elapsedMs = started.ElapsedMilliseconds, error = (string?)null });
                Console.WriteLine($"PASS: {test.Name}");
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new { name = test.Name, status = "failed", elapsedMs = started.ElapsedMilliseconds, error = ex.ToString() });
                Console.Error.WriteLine($"FAIL: {test.Name}: {ex.Message}");
            }
        }
        var report = Option(args, "--report");
        if (report is not null)
        {
            var fullPath = Path.GetFullPath(report);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, suite, completedUtc = DateTimeOffset.UtcNow,
                total = results.Count, passed = results.Count - failed, failed,
                physicalHardwareValidated = false, results
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"{suite}: {results.Count - failed}/{results.Count} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    public static string? Option(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        if (index < 0) return null;
        if (index + 1 == args.Length || args[index + 1].StartsWith("--"))
            throw new ArgumentException($"{option} requires a value.");
        return args[index + 1];
    }
}
