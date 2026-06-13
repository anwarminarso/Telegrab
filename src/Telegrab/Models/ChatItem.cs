using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TL;

namespace Telegrab.Models;

/// <summary>Representasi ringkas sebuah chat/channel/grup untuk ditampilkan di daftar.</summary>
public partial class ChatItem : ObservableObject
{
    public long Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty; // Channel / Group / Supergroup

    /// <summary>Peer asli untuk dipakai saat mengambil pesan.</summary>
    public InputPeer Peer { get; init; } = null!;

    /// <summary>Objek chat asli (untuk download foto profil).</summary>
    public IPeerInfo? PeerInfo { get; init; }

    /// <summary>True jika supergroup ini mode forum (punya topik/sub-chat).</summary>
    public bool IsForum { get; init; }

    /// <summary>Huruf awal judul, untuk avatar fallback saat tidak ada foto.</summary>
    public string Initial => string.IsNullOrWhiteSpace(Title) ? "?" : Title.Trim()[..1].ToUpperInvariant();

    /// <summary>Topik (sub-chat) untuk forum. Dimuat lazily saat node di-expand.</summary>
    public ObservableCollection<TopicItem> Topics { get; } = new();

    public bool TopicsLoaded { get; set; }

    /// <summary>Foto profil chat (thumbnail), dimuat lazily.</summary>
    [ObservableProperty] private ImageSource? _thumbnail;

    /// <summary>Apakah daftar topik sedang ditampilkan (di-expand).</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>True jika chat ini yang sedang dibuka di panel pesan (untuk highlight).</summary>
    [ObservableProperty] private bool _isActive;

    public override string ToString() => $"[{Kind}] {Title}";
}
