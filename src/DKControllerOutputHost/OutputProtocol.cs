using System.Text.Json;

namespace DKControllerOutputHost;

public static class OutputProtocol
{
    // The same loop is exercised with a fake submitter in CI, without installing a driver.
    public static async Task RunAsync(TextReader input, Action<IReadOnlyDictionary<string, double>> submit,
        Action<object> reply, TimeSpan? watchdog = null)
    {
        var neutral = new Dictionary<string, double>();
        try
        {
            while (true)
            {
                var line = await input.ReadLineAsync().WaitAsync(watchdog ?? TimeSpan.FromSeconds(1));
                if (line is null) break;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type == "stop") break;
                if (type != "state") throw new InvalidDataException("Unknown output message.");
                var controls = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var property in root.GetProperty("controls").EnumerateObject())
                {
                    if (!property.Value.TryGetDouble(out var value) || !double.IsFinite(value))
                        throw new InvalidDataException("Invalid control value.");
                    controls[property.Name] = Math.Clamp(value, -1d, 1d);
                }
                submit(controls);
                reply(new { type = "applied", ok = true });
            }
        }
        finally
        {
            // EOF, malformed state, submission failure and stalled parent all release controls.
            submit(neutral);
        }
    }
}
