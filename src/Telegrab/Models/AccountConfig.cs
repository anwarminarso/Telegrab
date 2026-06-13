namespace Telegrab.Models;

/// <summary>
/// Informasi akun yang disimpan ke config.
/// CATATAN: password 2FA TIDAK PERNAH disimpan di sini.
/// </summary>
public class AccountConfig
{
    public int ApiId { get; set; }
    public string ApiHash { get; set; } = string.Empty;
    public string PhoneNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary>Folder default untuk menyimpan file yang di-download.</summary>
    public string DownloadFolder { get; set; } = string.Empty;
}
