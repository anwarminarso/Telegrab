using Telegrab.Models;
using Telegrab.Services;

namespace Telegrab.Tests;

/// <summary>
/// Unit test untuk logika root download ConfigService (task 4.1).
///
/// ConfigService sendiri bergantung pada MAUI (<c>FileSystem.AppDataDirectory</c>) sehingga
/// TIDAK dapat dilink ke proyek test (net10.0). Logika murni-nya telah diekstrak ke
/// <see cref="RootValidator"/> (validasi izin) dan <see cref="DownloadRootState"/> (state +
/// event <c>RootChanged</c>) yang dilink ke sini dan diuji dengan direktori sementara nyata.
///
/// Memvalidasi Property 7 (strict gating): root tidak valid → <c>IsValid == false</c>,
/// sehingga pemanggil (gating unduh) tidak melanjutkan. Lihat Requirements 3.1, 3.2, 3.3.
/// </summary>
public sealed class ConfigServiceRootTests : IDisposable
{
    private readonly string _tmp;

    public ConfigServiceRootTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "telegrab_root_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tmp))
                Directory.Delete(_tmp, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    // --- RootValidator: kasus folder tidak ada / valid --------------------

    [Fact]
    public void Validate_FolderDoesNotExist_GetsCreated_AndIsValid()
    {
        var target = Path.Combine(_tmp, "new-root");
        Assert.False(Directory.Exists(target));

        var result = RootValidator.Validate(target);

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
        Assert.True(Directory.Exists(target), "folder seharusnya dibuat oleh validator");
        // Uji-tulis harus membersihkan file sementaranya sendiri.
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void Validate_ExistingWritableFolder_IsValid()
    {
        var result = RootValidator.Validate(_tmp);

        Assert.True(result.IsValid);
        Assert.False(result.CannotCreate);
        Assert.False(result.NotWritable);
        Assert.Empty(Directory.GetFiles(_tmp));
    }

    // --- RootValidator: kasus tidak bisa dibuat ---------------------------

    [Fact]
    public void Validate_PathUnderExistingFile_CannotCreate()
    {
        // Buat file lalu minta root sebagai sub-jalur DI BAWAH file tersebut.
        // CreateDirectory akan melempar IOException karena ada file dengan nama yang sama
        // di tengah jalur — reproducible lintas lingkungan (Windows/Linux/macOS).
        var blocker = Path.Combine(_tmp, "blocker");
        File.WriteAllText(blocker, "x");

        var target = Path.Combine(blocker, "sub-root");
        var result = RootValidator.Validate(target);

        Assert.False(result.IsValid);
        Assert.True(result.CannotCreate);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Validate_RootPathIsAnExistingFile_NotValid()
    {
        // Root menunjuk file biasa: Directory.Exists false → CreateDirectory gagal (IOException).
        var fileRoot = Path.Combine(_tmp, "iam-a-file.txt");
        File.WriteAllText(fileRoot, "x");

        var result = RootValidator.Validate(fileRoot);

        Assert.False(result.IsValid);
        Assert.True(result.CannotCreate);
    }

    // --- RootValidator: kasus path kosong ---------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyPath_IsInvalid(string? path)
    {
        var result = RootValidator.Validate(path);

        Assert.False(result.IsValid);
        Assert.False(result.CannotCreate);
        Assert.False(result.NotWritable);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // --- RootValidator: kasus tidak bisa ditulis --------------------------
    //
    // CATATAN: mensimulasikan folder yang TERBUAT namun TIDAK dapat ditulisi secara andal
    // di semua lingkungan (Windows admin/CI, Linux root) tidak praktis — atribut read-only
    // pada direktori tidak mencegah pembuatan file di Windows, dan test yang berjalan sebagai
    // root pada Linux mem-bypass izin. Jalur penanganan izin yang sama (catch
    // UnauthorizedAccessException/IOException) sudah tervalidasi lewat kasus CannotCreate di
    // atas. Di sini kita memverifikasi BENTUK hasil NotWritable secara langsung agar kontrak
    // (IsValid=false, NotWritable=true) tetap terjaga.

    [Fact]
    public void NotWritableResult_HasExpectedShape()
    {
        var result = RootValidationResult.NotWritableRoot("denied");

        Assert.False(result.IsValid);
        Assert.True(result.NotWritable);
        Assert.False(result.CannotCreate);
        Assert.Equal("denied", result.Error);
    }

    // --- DownloadRootState: event RootChanged & gating --------------------

    [Fact]
    public void SetRoot_RaisesRootChanged_WithNewPath()
    {
        var state = new DownloadRootState();
        string? observed = null;
        var fired = 0;
        state.RootChanged += path => { observed = path; fired++; };

        var newRoot = Path.Combine(_tmp, "root-a");
        state.Set(newRoot);

        Assert.Equal(1, fired);
        Assert.Equal(newRoot, observed);
        Assert.Equal(newRoot, state.DownloadRoot);
        Assert.True(state.IsRootConfigured);
    }

    [Fact]
    public void SetRoot_SameValueTwice_RaisesEventOnce()
    {
        var state = new DownloadRootState();
        var fired = 0;
        state.RootChanged += _ => fired++;

        var root = Path.Combine(_tmp, "root-b");
        state.Set(root);
        state.Set(root); // tidak berubah → tidak memancarkan lagi

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Initialize_DoesNotRaiseRootChanged()
    {
        var state = new DownloadRootState();
        var fired = 0;
        state.RootChanged += _ => fired++;

        state.Initialize(Path.Combine(_tmp, "loaded-root"));

        Assert.Equal(0, fired); // memuat dari config tidak boleh memicu event
        Assert.True(state.IsRootConfigured);
    }

    [Fact]
    public void NotConfigured_WhenRootNullOrEmpty()
    {
        var state = new DownloadRootState();
        Assert.False(state.IsRootConfigured);
        Assert.Null(state.DownloadRoot);

        state.Initialize("   ");
        Assert.False(state.IsRootConfigured); // whitespace dianggap belum dikonfigurasi
        Assert.Null(state.DownloadRoot);
    }
}
