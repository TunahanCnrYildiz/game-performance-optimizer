using Optimizer.Core.Sessions;

namespace Optimizer.Core.Benchmarks;

public sealed class BenchmarkStore(string directory)
{
    public void Save(BenchmarkRun run) =>
        JsonFiles.WriteAtomic(Path.Combine(directory, $"{run.CapturedAt:yyyyMMdd-HHmmss}-{run.Id:N}.json"), run);

    public IReadOnlyList<BenchmarkRun> LoadAll() =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json")
                .Select(JsonFiles.TryRead<BenchmarkRun>)
                .OfType<BenchmarkRun>()
                .OrderByDescending(r => r.CapturedAt)
                .ToList()
            : [];

    public void Delete(Guid id)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, $"*-{id:N}.json"))
            File.Delete(file);
    }
}
