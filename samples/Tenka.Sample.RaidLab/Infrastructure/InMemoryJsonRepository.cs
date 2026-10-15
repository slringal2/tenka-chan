using System.Text.Json;
using Tenka.Sample.RaidLab.Domain;

namespace Tenka.Sample.RaidLab.Infrastructure;

// All "storage" is bounded in memory. No file, database or network access.
public sealed class InMemoryJsonRepository : IRaidRepository
{
    private readonly byte[] _json;
    public InMemoryJsonRepository(RaidDataset data)
    {
        DatasetContract.Validate(data, new());
        _json = JsonSerializer.SerializeToUtf8Bytes(data);
    }
    public InMemoryJsonRepository(byte[] json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 16000000) throw new ArgumentException("JSON exceeds sample budget.");
        _json = (byte[])json.Clone();
    }
    public async Task<RaidDataset> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(_json, writable: false);
        var result = await JsonSerializer.DeserializeAsync<RaidDataset>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("JSON did not contain a dataset.");
        DatasetContract.Validate(result, new(), cancellationToken);
        return result;
    }
}
