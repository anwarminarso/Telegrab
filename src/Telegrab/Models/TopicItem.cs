using CommunityToolkit.Mvvm.ComponentModel;
using TL;

namespace Telegrab.Models;

/// <summary>Sebuah topik forum (sub-chat) di dalam supergroup.</summary>
public partial class TopicItem : ObservableObject
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;

    /// <summary>Peer supergroup induk topik ini.</summary>
    public InputPeer ParentPeer { get; init; } = null!;

    /// <summary>Id chat induk (supergroup) topik ini.</summary>
    public long ParentId { get; init; }

    /// <summary>Judul chat induk (untuk penataan folder unduhan).</summary>
    public string ParentTitle { get; init; } = string.Empty;

    /// <summary>Penanda node sementara ("memuat...") sebelum topik asli dimuat.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>Id custom emoji ikon topik (0 jika pakai ikon default berwarna).</summary>
    public long IconEmojiId { get; init; }

    /// <summary>Warna ikon default topik (RGB).</summary>
    public int IconColor { get; init; }

    /// <summary>Gambar ikon topik (dari custom emoji Telegram), dimuat lazily.</summary>
    [ObservableProperty] private ImageSource? _icon;

    /// <summary>True jika topik ini yang sedang dibuka di panel pesan (untuk highlight).</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Warna untuk ikon default (saat tidak ada custom emoji).</summary>
    public Color IconColorValue =>
        IconColor != 0
            ? Color.FromRgb((IconColor >> 16) & 0xFF, (IconColor >> 8) & 0xFF, IconColor & 0xFF)
            : Color.FromArgb("#3A6EA5");

    public override string ToString() => Title;
}
