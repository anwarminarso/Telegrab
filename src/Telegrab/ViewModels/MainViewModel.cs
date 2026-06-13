using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Telegrab.Models;
using Telegrab.Services;

namespace Telegrab.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly TelegramService _telegram;
    private readonly ConfigService _config;
    private readonly DownloadQueueService _queue;
    private readonly DownloadManifestService _manifest;
    private CancellationTokenSource? _thumbCts;
    private CancellationTokenSource? _chatThumbCts;
    private readonly List<ChatItem> _allChats = new();

    private ChatItem? _activeChat;
    private TopicItem? _activeTopic;

    private const int PageSize = 50;
    private TL.InputPeer? _currentPeer;
    private int? _currentTopicId;
    private int _oldestId;
    private string? _currentQuery;
    private string _baseTitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(DownloadAllLoadedCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _currentTitle = "Select a chat or topic";
    [ObservableProperty] private string _downloadFolder = string.Empty;
    [ObservableProperty] private string _searchQuery = string.Empty;

    [ObservableProperty] private string _chatSearchQuery = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _hasMore;

    /// <summary>True bila record terakhir sudah tercapai (untuk indikator UI).</summary>
    [ObservableProperty] private bool _allLoaded;

    public ObservableCollection<ChatItem> Chats { get; } = new();
    public ObservableCollection<MessageItem> Messages { get; } = new();

    /// <summary>Antrian unduh batch (di-bind ke panel antrian di UI).</summary>
    public DownloadQueueService Queue => _queue;

    /// <summary>Diminta saat media siap ditampilkan di viewer (dengan navigasi galeri).</summary>
    public event Action<MediaGalleryRequest>? OpenMediaRequested;

    public MainViewModel(TelegramService telegram, ConfigService config,
        DownloadQueueService queue, DownloadManifestService manifest)
    {
        _telegram = telegram;
        _config = config;
        _queue = queue;
        _manifest = manifest;
        _downloadFolder = _config.Load().DownloadFolder;
    }

    [RelayCommand]
    public async Task LoadChatsAsync()
    {
        IsBusy = true;
        Status = "Loading chats...";
        try
        {
            _chatThumbCts?.Cancel();
            _chatThumbCts = new CancellationTokenSource();

            var chats = await _telegram.GetChatsAsync();
            _allChats.Clear();
            _allChats.AddRange(chats);
            ApplyChatFilter();
            Status = $"{_allChats.Count} chats loaded. Signed in as {_telegram.Me?.first_name}.";

            _ = LoadChatThumbnailsAsync(chats, _chatThumbCts.Token);
        }
        catch (Exception ex)
        {
            Status = $"Failed to load chats: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnChatSearchQueryChanged(string value) => ApplyChatFilter();

    private void ApplyChatFilter()
    {
        var q = ChatSearchQuery?.Trim();
        Chats.Clear();
        foreach (var c in _allChats)
        {
            if (string.IsNullOrEmpty(q) ||
                c.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                Chats.Add(c);
            }
        }
    }

    private async Task LoadChatThumbnailsAsync(IReadOnlyList<ChatItem> chats, CancellationToken token)
    {
        foreach (var c in chats)
        {
            if (token.IsCancellationRequested) return;
            if (c.Thumbnail != null || c.PeerInfo == null) continue;

            var bytes = await _telegram.GetChatThumbnailAsync(c.PeerInfo, token);
            if (token.IsCancellationRequested) return;
            if (bytes != null)
                c.Thumbnail = ImageSource.FromStream(() => new MemoryStream(bytes));
        }
    }

    [RelayCommand]
    private async Task SelectChatAsync(ChatItem? chat)
    {
        if (chat == null) return;

        if (chat.IsForum)
        {
            chat.IsExpanded = !chat.IsExpanded;
            if (chat.IsExpanded && !chat.TopicsLoaded)
                await LoadTopicsAsync(chat);
            CurrentTitle = chat.Title;
            return;
        }

        SetTarget(chat.Peer, null, chat.Title);
        SetActiveChat(chat);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task SelectTopicAsync(TopicItem? topic)
    {
        if (topic is not { IsPlaceholder: false }) return;
        SetTarget(topic.ParentPeer, topic.Id, topic.Title);
        SetActiveTopic(topic);
        await ReloadAsync();
    }

    private void SetActiveChat(ChatItem chat)
    {
        if (_activeTopic != null) { _activeTopic.IsActive = false; _activeTopic = null; }
        if (_activeChat != null && !ReferenceEquals(_activeChat, chat)) _activeChat.IsActive = false;
        _activeChat = chat;
        chat.IsActive = true;
    }

    private void SetActiveTopic(TopicItem topic)
    {
        if (_activeChat != null) { _activeChat.IsActive = false; _activeChat = null; }
        if (_activeTopic != null && !ReferenceEquals(_activeTopic, topic)) _activeTopic.IsActive = false;
        _activeTopic = topic;
        topic.IsActive = true;
    }

    private void SetTarget(TL.InputPeer peer, int? topicId, string title)
    {
        _currentPeer = peer;
        _currentTopicId = topicId;
        _baseTitle = title;
        _currentQuery = null;
        SearchQuery = string.Empty;
    }

    /// <summary>Konteks unduhan saat ini (chat/topik aktif) untuk penataan folder & manifest.</summary>
    private DownloadContext CurrentContext()
    {
        if (_activeTopic != null)
            return new DownloadContext(_activeTopic.ParentId, _activeTopic.ParentTitle, _activeTopic.Title);
        if (_activeChat != null)
            return new DownloadContext(_activeChat.Id, _activeChat.Title, null);
        return new DownloadContext(0, "Telegrab", null);
    }

    /// <summary>Tandai media yang sudah ada di manifest sebagai "terunduh" saat pesan dimuat.</summary>
    private void ApplyManifestState(IEnumerable<MessageItem> items, long chatId)
    {
        foreach (var item in items)
        {
            foreach (var part in item.Media)
            {
                if (part.IsDownloaded) continue;
                if (_manifest.IsDownloaded(chatId, part.MessageId, part.MediaId, out var path))
                {
                    part.LocalPath = path;
                    part.IsDownloaded = true;
                }
            }
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (_currentPeer == null)
        {
            await ToastAsync("Select a chat or topic before searching.");
            return;
        }
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            await ClearSearchAsync();
            return;
        }

        _currentQuery = SearchQuery.Trim();
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ClearSearchAsync()
    {
        if (_currentQuery == null && string.IsNullOrEmpty(SearchQuery)) return;
        _currentQuery = null;
        SearchQuery = string.Empty;
        if (_currentPeer != null)
            await ReloadAsync();
    }

    private async Task LoadTopicsAsync(ChatItem chat)
    {
        if (!chat.IsForum || chat.TopicsLoaded) return;
        chat.TopicsLoaded = true;

        try
        {
            var topics = await _telegram.GetTopicsAsync(chat.Peer, chat.Id, chat.Title);
            chat.Topics.Clear();
            foreach (var t in topics)
                chat.Topics.Add(t);
            if (chat.Topics.Count == 0)
                Status = $"Forum '{chat.Title}' has no readable topics.";

            _ = LoadTopicIconsAsync(topics);
        }
        catch (Exception ex)
        {
            chat.TopicsLoaded = false;
            Status = $"Failed to load topics: {ex.Message}";
        }
    }

    private async Task LoadTopicIconsAsync(List<TopicItem> topics)
    {
        var ids = topics.Where(t => t.IconEmojiId != 0).Select(t => t.IconEmojiId).Distinct().ToArray();
        if (ids.Length == 0) return;

        var map = await _telegram.GetCustomEmojiThumbnailsAsync(ids);
        foreach (var t in topics)
        {
            if (t.IconEmojiId != 0 && map.TryGetValue(t.IconEmojiId, out var bytes))
                t.Icon = ImageSource.FromStream(() => new MemoryStream(bytes));
        }
    }

    private Task<MessagePage> FetchAsync(int offsetId)
        => string.IsNullOrWhiteSpace(_currentQuery)
            ? _telegram.GetMessagesAsync(_currentPeer!, _currentTopicId, offsetId, PageSize)
            : _telegram.SearchMessagesAsync(_currentPeer!, _currentQuery!, _currentTopicId, offsetId, PageSize);

    private async Task ReloadAsync()
    {
        if (_currentPeer == null) return;

        _thumbCts?.Cancel();
        _thumbCts = new CancellationTokenSource();
        var token = _thumbCts.Token;

        IsBusy = true;
        CurrentTitle = _currentQuery == null ? _baseTitle : $"{_baseTitle} — search: \"{_currentQuery}\"";
        Status = "Loading messages...";
        Messages.Clear();
        HasMore = false;
        AllLoaded = false;
        try
        {
            var page = await FetchAsync(0);
            foreach (var m in page.Items)
                Messages.Add(m);
            _oldestId = page.OldestId;
            HasMore = page.RawCount == PageSize && page.Items.Count > 0;
            AllLoaded = !HasMore && Messages.Count > 0;
            Status = $"{Messages.Count} messages loaded.";

            ApplyManifestState(page.Items, CurrentContext().ChatId);
            await LoadThumbnailsAsync(page.Items, token);
        }
        catch (Exception ex)
        {
            Status = $"Failed to load messages: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanLoadMore() => HasMore && !IsBusy && _currentPeer != null && Messages.Count > 0;

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (_currentPeer == null || Messages.Count == 0) return;

        var token = _thumbCts?.Token ?? CancellationToken.None;

        IsBusy = true;
        Status = "Loading more messages...";
        try
        {
            var prevOldest = _oldestId;
            var page = await FetchAsync(_oldestId);
            foreach (var m in page.Items)
                Messages.Add(m);
            _oldestId = page.OldestId;

            // Hentikan paginasi bila: halaman tidak penuh, kosong, atau offset tidak maju
            // (jaga2 agar auto-load tidak memicu permintaan berulang di record terakhir).
            HasMore = page.RawCount == PageSize
                      && page.Items.Count > 0
                      && _oldestId != 0
                      && _oldestId != prevOldest;
            AllLoaded = !HasMore && Messages.Count > 0;
            Status = $"{Messages.Count} messages loaded.";

            ApplyManifestState(page.Items, CurrentContext().ChatId);
            await LoadThumbnailsAsync(page.Items, token);
        }
        catch (Exception ex)
        {
            Status = $"Failed to load messages: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefresh() => !IsBusy && _currentPeer != null;

    /// <summary>Muat ulang pesan untuk chat/topik aktif dari awal.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => ReloadAsync();

    private async Task LoadThumbnailsAsync(IEnumerable<MessageItem> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            foreach (var part in item.Media)
            {
                if (token.IsCancellationRequested) return;
                if (part.Thumbnail != null) continue;

                var bytes = await _telegram.GetThumbnailAsync(part, token);
                if (token.IsCancellationRequested) return;
                if (bytes != null)
                    part.Thumbnail = ImageSource.FromStream(() => new MemoryStream(bytes));
            }
        }
    }

    [RelayCommand]
    private async Task DownloadPartAsync(MediaPart? part)
    {
        if (part == null || part.IsDownloading || part.IsDownloaded) return;

        part.IsDownloading = true;
        part.Progress = 0;
        var progress = new Progress<double>(p => part.Progress = p);
        try
        {
            var path = await _queue.DownloadOneAsync(part, CurrentContext(), progress);
            if (path != null)
                await ToastAsync($"Saved: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            await ToastAsync($"Download failed: {ex.Message}");
        }
        finally
        {
            part.IsDownloading = false;
        }
    }

    /// <summary>Antrikan semua media dari satu pesan (album) ke batch download.</summary>
    [RelayCommand]
    private async Task DownloadMessageAsync(MessageItem? message)
    {
        if (message == null || !message.HasMedia) return;
        var added = _queue.Enqueue(message.Media, CurrentContext());
        await ToastAsync(added > 0 ? $"{added} media added to the queue." : "All media already downloaded.");
    }

    private bool CanDownloadAllLoaded() => !IsBusy && _currentPeer != null && Messages.Count > 0;

    /// <summary>Antrikan semua media dari pesan yang sedang termuat ke batch download.</summary>
    [RelayCommand(CanExecute = nameof(CanDownloadAllLoaded))]
    private async Task DownloadAllLoadedAsync()
    {
        if (_currentPeer == null)
        {
            await ToastAsync("Select a chat or topic first.");
            return;
        }
        if (Messages.Count == 0)
        {
            await ToastAsync("No messages loaded yet.");
            return;
        }
        var parts = Messages.SelectMany(m => m.Media);
        var added = _queue.Enqueue(parts, CurrentContext());
        await ToastAsync(added > 0 ? $"{added} media added to the queue." : "All media already downloaded.");
    }

    [RelayCommand]
    private void ClearFinishedJobs() => _queue.ClearFinished();

    [RelayCommand]
    private void CancelAllJobs() => _queue.CancelAll();

    [RelayCommand]
    private void CancelJob(DownloadJob? job)
    {
        if (job != null) _queue.Cancel(job);
    }

    [RelayCommand]
    private async Task OpenPartAsync(MediaPart? part)
    {
        if (part == null) return;

        // Kumpulkan media yang bisa dilihat (foto/video) dari pesan termuat, sesuai urutan.
        var items = Messages
            .SelectMany(m => m.Media)
            .Where(p => p.Kind is MediaKind.Photo or MediaKind.Video)
            .ToList();

        var index = items.IndexOf(part);
        if (index < 0)
        {
            // Bukan foto/video (mis. file lain): buka sendiri tanpa galeri.
            items = new List<MediaPart> { part };
            index = 0;
        }

        OpenMediaRequested?.Invoke(new MediaGalleryRequest
        {
            Items = items,
            StartIndex = index,
            EnsureDownloaded = EnsureDownloadedAsync
        });
    }

    /// <summary>Pastikan sebuah media terunduh; kembalikan path lokal atau null bila gagal.</summary>
    public async Task<string?> EnsureDownloadedAsync(MediaPart part)
    {
        if (part.IsDownloaded && !string.IsNullOrEmpty(part.LocalPath) && File.Exists(part.LocalPath))
            return part.LocalPath;

        part.IsDownloading = true;
        part.Progress = 0;
        var progress = new Progress<double>(p => part.Progress = p);
        try
        {
            return await _queue.DownloadOneAsync(part, CurrentContext(), progress);
        }
        catch (Exception ex)
        {
            await ToastAsync($"Download failed: {ex.Message}");
            return null;
        }
        finally
        {
            part.IsDownloading = false;
        }
    }

    private static async Task ToastAsync(string message)
    {
        try { await Toast.Make(message, ToastDuration.Short).Show(); }
        catch { /* abaikan kegagalan menampilkan toast */ }
    }

    [RelayCommand]
    private async Task ChangeDownloadFolderAsync()
    {
        try
        {
            var result = await FolderPicker.Default.PickAsync(CancellationToken.None);
            if (result.IsSuccessful && result.Folder is not null)
            {
                DownloadFolder = result.Folder.Path;
                _config.SaveDownloadFolder(DownloadFolder);
                await ToastAsync("Download folder changed.");
            }
        }
        catch (Exception ex)
        {
            await ToastAsync($"Failed to pick folder: {ex.Message}");
        }
    }
}
