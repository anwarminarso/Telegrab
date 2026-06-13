using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
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
    private readonly ManifestDbService _db;
    private readonly DocumentationService _documentation;
    private readonly DbLifecycleCoordinator _dbLifecycle;
    private CancellationTokenSource? _thumbCts;
    private CancellationTokenSource? _chatThumbCts;
    private readonly List<ChatItem> _allChats = new();

    private ChatItem? _activeChat;
    private TopicItem? _activeTopic;

    private const int PageSize = 50;

    /// <summary>Maksimum unduhan thumbnail yang berjalan bersamaan (P3: paralel terbatas).</summary>
    private const int ThumbnailConcurrency = 4;
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

    /// <summary>Diminta saat modal Configuration perlu dibuka. Argumen: true bila WAJIB (mandatory).</summary>
    public event Action<bool>? OpenConfigRequested;

    /// <summary>Diminta saat penampil dokumentasi (README.md) perlu dibuka untuk konteks aktif.</summary>
    public event Action<DocumentationRequest>? OpenDocumentationRequested;

    public MainViewModel(TelegramService telegram, ConfigService config,
        DownloadQueueService queue, ManifestDbService db,
        DocumentationService documentation, DbLifecycleCoordinator dbLifecycle)
    {
        _telegram = telegram;
        _config = config;
        _queue = queue;
        _db = db;
        _documentation = documentation;
        _dbLifecycle = dbLifecycle;

        // Folder bar mencerminkan root download "strict" (sumber kebenaran kini ConfigService.DownloadRoot).
        _downloadFolder = _config.DownloadRoot ?? string.Empty;

        // Saat root diganti (lewat modal Configuration): reload main page agar DB, status unduhan,
        // dan tampilan kembali normal untuk root baru. Berlangganan RootSwitched (BUKAN
        // ConfigService.RootChanged) agar dijamin DB sudah dialihkan DbLifecycleCoordinator
        // sebelum reload berjalan — urutan eksplisit, tidak bergantung urutan langganan (B4).
        _dbLifecycle.RootSwitched += OnRootChanged;
    }

    private void OnRootChanged(string newRoot)
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try { await ReloadForNewRootAsync(newRoot); }
            catch (Exception ex) { Status = $"Reload failed: {ex.Message}"; }
        });
    }

    /// <summary>
    /// Reset penuh tampilan main page lalu muat ulang daftar chat, agar semuanya mengikuti root
    /// download yang baru (DB sudah dialihkan oleh DbLifecycleCoordinator).
    /// </summary>
    private async Task ReloadForNewRootAsync(string newRoot)
    {
        DownloadFolder = newRoot;

        // Bersihkan antrian & pekerjaan lama yang menunjuk root sebelumnya.
        _queue.CancelAll();
        _queue.ClearFinished();

        _thumbCts?.Cancel();

        // Reset state pemilihan & daftar pesan.
        Messages.Clear();
        _currentPeer = null;
        _currentTopicId = null;
        _currentQuery = null;
        SearchQuery = string.Empty;
        if (_activeChat != null) { _activeChat.IsActive = false; _activeChat = null; }
        if (_activeTopic != null) { _activeTopic.IsActive = false; _activeTopic = null; }
        CurrentTitle = "Select a chat or topic";
        HasMore = false;
        AllLoaded = false;
        _oldestId = 0;

        // Muat ulang daftar chat (login tetap aktif).
        await LoadChatsAsync();
    }

    /// <summary>
    /// Dipanggil saat main page muncul: bila root download belum dikonfigurasi, buka modal
    /// Configuration dalam mode WAJIB (mandatory) sehingga pengguna harus memilih folder dulu.
    /// </summary>
    public void RequestConfigIfNeeded()
    {
        if (!_config.IsRootConfigured)
            OpenConfigRequested?.Invoke(true);
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
        var targets = chats.Where(c => c.Thumbnail == null && c.PeerInfo != null).ToList();
        if (targets.Count == 0) return;

        // Paralel terbatas (P3): unduh beberapa thumbnail sekaligus, bukan satu-per-satu.
        using var gate = new SemaphoreSlim(ThumbnailConcurrency);
        var tasks = targets.Select(c => LoadOneChatThumbnailAsync(c, gate, token));
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* dibatalkan: abaikan */ }
    }

    private async Task LoadOneChatThumbnailAsync(ChatItem chat, SemaphoreSlim gate, CancellationToken token)
    {
        try { await gate.WaitAsync(token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (token.IsCancellationRequested) return;
            var bytes = await _telegram.GetChatThumbnailAsync(chat.PeerInfo!, token);
            if (bytes == null || token.IsCancellationRequested) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!token.IsCancellationRequested && chat.Thumbnail == null)
                    chat.Thumbnail = ImageSource.FromStream(() => new MemoryStream(bytes));
            });
        }
        catch (OperationCanceledException) { /* abaikan */ }
        finally { gate.Release(); }
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
        // Catatan: untuk topik, ChatId = ParentId (id supergroup induk), bukan id topik — lihat
        // dokumentasi DownloadContext. Topik dibedakan lewat TopicTitle (subfolder).
        if (_activeTopic != null)
            return new DownloadContext(_activeTopic.ParentId, _activeTopic.ParentTitle, _activeTopic.Title);
        if (_activeChat != null)
            return new DownloadContext(_activeChat.Id, _activeChat.Title, null);
        return new DownloadContext(0, "Telegrab", null);
    }

    /// <summary>
    /// Tandai media yang sudah ada di manifest sebagai "terunduh" saat pesan dimuat. Query DB +
    /// pengecekan <c>File.Exists</c> dijalankan di thread pool (P2) agar tidak memblokir UI thread;
    /// penerapan hasil (properti observable) dilakukan kembali di UI thread.
    /// </summary>
    private async Task ApplyManifestStateAsync(IReadOnlyList<MessageItem> items, long chatId)
    {
        if (!_db.IsReady)
            return;

        var pending = items.SelectMany(m => m.Media).Where(p => !p.IsDownloaded).ToList();
        if (pending.Count == 0)
            return;

        // Kerja DB + disk di luar UI thread.
        var resolved = await Task.Run(() =>
        {
            var hits = new List<(MediaPart Part, string Path)>();
            foreach (var part in pending)
            {
                // Lewati query bila DB belum siap (root belum dikonfigurasi) agar daftar pesan
                // tetap dimuat tanpa crash (Requirement 12.2/12.3).
                if (_db.IsReady && _db.IsDownloaded(chatId, part.MessageId, part.MediaId, out var path))
                    hits.Add((part, path));
            }
            return hits;
        });

        // Kembali di UI thread (continuation): set properti observable.
        foreach (var (part, path) in resolved)
        {
            part.LocalPath = path;
            part.IsDownloaded = true;
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

            await ApplyManifestStateAsync(page.Items, CurrentContext().ChatId);
        }
        catch (Exception ex)
        {
            Status = $"Failed to load messages: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        // Thumbnail dimuat di latar (fire-and-forget): JANGAN ditahan di bawah IsBusy. Sebelumnya
        // fase ini di-await sehingga UI tetap "busy" — dan karena thumbnail di-throttle di belakang
        // unduhan aktif, daftar pesan bisa terasa beku selama mengunduh (P1).
        if (Messages.Count > 0)
            _ = LoadThumbnailsAsync(Messages.ToList(), token);
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

            await ApplyManifestStateAsync(page.Items, CurrentContext().ChatId);
            // Fire-and-forget: jangan tahan IsBusy menunggu thumbnail (P1).
            _ = LoadThumbnailsAsync(page.Items, token);
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

    private bool CanRebuildDocumentation() => _currentPeer != null;

    /// <summary>
    /// Buka penampil dokumentasi (<c>README.md</c>) untuk chat/topik aktif (Requirement 10.1).
    /// Bila belum ada chat/topik dipilih atau root belum dikonfigurasi, beri pesan jelas
    /// (Requirement 10.3); penanganan README yang belum ada ditangani di dalam penampil
    /// (tawarkan rebuild). Folder relatif diturunkan sama seperti tempat media ditulis,
    /// dan root absolut diambil dari DB aktif (fallback ke konfigurasi).
    /// </summary>
    [RelayCommand]
    private async Task OpenDocumentationAsync()
    {
        if (_currentPeer == null)
        {
            await ToastAsync("Select a chat or topic first.");
            return;
        }

        var root = _db.Root ?? _config.DownloadRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            await ToastAsync("Folder download belum dikonfigurasi. Buka Configuration untuk mengatur folder.");
            OpenConfigRequested?.Invoke(!_config.IsRootConfigured);
            return;
        }

        var relativeFolder = _telegram.BuildRelativeFolder(CurrentContext());
        OpenDocumentationRequested?.Invoke(new DocumentationRequest(relativeFolder, root));
    }

    /// <summary>
    /// Bangun ulang <c>README.md</c> untuk folder chat/topik aktif dari DB (Requirement 9.3).
    /// Karena aksi ini menimpa blok ter-generate, minta konfirmasi lebih dulu. Folder relatif
    /// diturunkan dari konteks aktif memakai sanitasi yang sama dengan tempat file ditulis,
    /// sehingga cocok dengan lokasi media di disk.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRebuildDocumentation))]
    private async Task RebuildDocumentationAsync()
    {
        if (_currentPeer == null)
        {
            await ToastAsync("Select a chat or topic first.");
            return;
        }

        var relativeFolder = _telegram.BuildRelativeFolder(CurrentContext());

        var confirmed = await ConfirmAsync(
            "Rebuild documentation",
            $"This will regenerate the README.md for \"{relativeFolder}\" from the database, " +
            "overwriting the generated block. Continue?",
            "Rebuild", "Cancel");
        if (!confirmed)
            return;

        try
        {
            await _documentation.RebuildFolderAsync(relativeFolder);
            await ToastAsync("Documentation rebuilt.");
        }
        catch (Exception ex)
        {
            await ToastAsync($"Rebuild failed: {ex.Message}");
        }
    }

    private async Task LoadThumbnailsAsync(IEnumerable<MessageItem> items, CancellationToken token)
    {
        var parts = items.SelectMany(m => m.Media).Where(p => p.Thumbnail == null).ToList();
        if (parts.Count == 0) return;

        // Paralel terbatas (P3): unduh beberapa thumbnail sekaligus. Throttle thumbnail di
        // TelegramService tetap memprioritaskan unduhan file penuh.
        using var gate = new SemaphoreSlim(ThumbnailConcurrency);
        var tasks = parts.Select(p => LoadOneThumbnailAsync(p, gate, token));
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) { /* dibatalkan: abaikan */ }
    }

    private async Task LoadOneThumbnailAsync(MediaPart part, SemaphoreSlim gate, CancellationToken token)
    {
        try { await gate.WaitAsync(token); }
        catch (OperationCanceledException) { return; }
        try
        {
            if (token.IsCancellationRequested) return;
            var bytes = await _telegram.GetThumbnailAsync(part, token);
            if (bytes == null || token.IsCancellationRequested) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!token.IsCancellationRequested && part.Thumbnail == null)
                    part.Thumbnail = ImageSource.FromStream(() => new MemoryStream(bytes));
            });
        }
        catch (OperationCanceledException) { /* abaikan */ }
        finally { gate.Release(); }
    }

    [RelayCommand]
    private async Task DownloadPartAsync(MediaPart? part)
    {
        if (part == null || part.IsDownloading || part.IsDownloaded) return;
        if (!await EnsureRootConfiguredAsync()) return;

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
        if (!await EnsureRootConfiguredAsync()) return;
        try
        {
            var added = _queue.Enqueue(message.Media, CurrentContext());
            await ToastAsync(added > 0 ? $"{added} media added to the queue." : "All media already downloaded.");
        }
        catch (InvalidOperationException ex)
        {
            await ToastAsync(ex.Message);
        }
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
        if (!await EnsureRootConfiguredAsync()) return;
        var parts = Messages.SelectMany(m => m.Media);
        try
        {
            var added = _queue.Enqueue(parts, CurrentContext());
            await ToastAsync(added > 0 ? $"{added} media added to the queue." : "All media already downloaded.");
        }
        catch (InvalidOperationException ex)
        {
            await ToastAsync(ex.Message);
        }
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

        if (!await EnsureRootConfiguredAsync()) return null;

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

    /// <summary>
    /// Tampilkan dialog konfirmasi (ya/tidak) memakai window aktif aplikasi. Mengembalikan
    /// <c>true</c> bila pengguna menyetujui. Aman bila tidak ada window (mis. saat test).
    /// </summary>
    private static async Task<bool> ConfirmAsync(string title, string message, string accept, string cancel)
    {
        var page = Application.Current?.Windows.Count > 0
            ? Application.Current.Windows[0].Page
            : null;
        if (page == null)
            return false;

        try { return await page.DisplayAlertAsync(title, message, accept, cancel); }
        catch { return false; }
    }

    /// <summary>Buka modal Configuration (ikon header & tombol "Change" folder bar). Lihat ConfigPage/ConfigViewModel.</summary>
    [RelayCommand]
    private void OpenConfiguration() => OpenConfigRequested?.Invoke(false);

    /// <summary>
    /// Gating proaktif di UI (Requirement 1.2/3.4): unduhan hanya boleh berjalan bila DB manifest
    /// SIAP (root sudah dikonfigurasi DAN valid/terbuka). Bila tidak siap:
    /// <list type="bullet">
    ///   <item>root belum dikonfigurasi → buka modal Configuration mode WAJIB;</item>
    ///   <item>root sudah dikonfigurasi tetapi tidak valid (folder hilang/izin) → pesan jelas +
    ///         buka modal agar pengguna memilih ulang folder.</item>
    /// </list>
    /// Mengembalikan <c>false</c> agar pemanggil membatalkan aksi unduh. Service tetap melakukan
    /// strict gating sebagai lapisan kedua.
    /// </summary>
    private async Task<bool> EnsureRootConfiguredAsync()
    {
        if (_db.IsReady) return true;

        if (!_config.IsRootConfigured)
        {
            await ToastAsync("Folder download belum dikonfigurasi. Pilih folder di Configuration untuk mengunduh.");
            OpenConfigRequested?.Invoke(true);
        }
        else
        {
            await ToastAsync("Folder download tidak dapat diakses. Pilih ulang folder di Configuration.");
            OpenConfigRequested?.Invoke(false);
        }
        return false;
    }
}
