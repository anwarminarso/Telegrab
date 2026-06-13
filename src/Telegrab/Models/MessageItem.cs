using System.Collections.ObjectModel;

namespace Telegrab.Models;

public enum MediaKind { None, Photo, Video, File }

/// <summary>Sebuah pesan untuk ditampilkan (readonly). Bisa berisi 0..n media (album).</summary>
public class MessageItem
{
    public int Id { get; init; }
    public string Text { get; set; } = string.Empty;
    public DateTime Date { get; init; }
    public string DateText => Date.ToLocalTime().ToString("dd MMM yyyy HH:mm");
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>Id album Telegram (0 jika bukan bagian album).</summary>
    public long GroupedId { get; init; }

    public ObservableCollection<MediaPart> Media { get; } = new();
    public bool HasMedia => Media.Count > 0;
    public bool IsAlbum => Media.Count > 1;
}
