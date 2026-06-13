using Microsoft.Data.Sqlite;

namespace Telegrab.Tests;

/// <summary>
/// Tes sanity untuk task 1: memastikan proyek test (net10.0, tanpa MAUI) terkompilasi
/// dan dependensi <c>Microsoft.Data.Sqlite</c> dapat dimuat & dijalankan. Tes logika
/// sebenarnya (ManifestDbService, CaptionResolver, DocumentationService) ditambahkan
/// pada task berikutnya.
/// </summary>
public class ScaffoldingTests
{
    [Fact]
    public void Sqlite_InMemory_Connection_Works()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        var version = command.ExecuteScalar() as string;

        Assert.False(string.IsNullOrWhiteSpace(version));
    }
}
