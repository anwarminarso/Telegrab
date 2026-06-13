# Planning: Penyelarasan Tujuan Produk ↔ Source Code

Dokumen ini memetakan **tujuan produk** (lihat `.kiro/steering/product.md`) terhadap **kondisi
nyata source code** di `src/Telegrab` (source of truth), mengidentifikasi **gap**, lalu menyusun
**rencana fase berikutnya**.

Dokumen ini melengkapi `Audit.md` (audit teknis tingkat kode). Untuk backlog hardening
teknis yang rinci (integritas data, lifecycle, performa) gunakan `Audit.md` §10. Di sini fokusnya
adalah **apakah tiap janji produk benar-benar terpenuhi oleh kode**.

> Asumsi tetap: **tanpa migrasi** (belum ada data produksi). Perubahan skema folder/DB boleh
> dilakukan langsung.

---

## 1. Matriks Gap: Tujuan Produk vs Implementasi

| # | Tujuan Produk | Status Kode | Gap |
|---|---------------|-------------|-----|
| 1 | Telegram Downloader (read-only) | ✅ Terpenuhi — `TelegramService` (WTelegramClient), login bertahap, daftar chat/topik/pesan | — |
| 2 | **Download semua media pada group/supergroup** ke folder tertentu | ⚠️ **Sebagian** — tombol "Download all" = `DownloadAllLoadedCommand`, hanya media pada pesan yang **sudah termuat** di UI | **GAP A**: tidak ada unduhan "seluruh riwayat" yang otomatis menelusuri semua halaman. Pengguna harus scroll/Load More manual sampai habis dulu |
| 2b | Caching/reload media dari folder | ✅ Terpenuhi — manifest `telegrab.db`, `IsDownloaded` + `File.Exists`, pulih otomatis saat menunjuk root lama | Minor: record DB yatim saat file dihapus manual (Audit 1.8) |
| 2c | Simpan ke folder terorganisir + portabel | ⚠️ **Berisiko** — `BuildTargetPath` memakai **judul saja**, tanpa `chat_id`/`topic_id` | **GAP C**: dua chat/topik berjudul sama → tabrakan folder di disk (Audit 1.1, P0) |
| 3 | Download All **atau** per file | ✅ Terpenuhi — `DownloadAllLoadedCommand`, `DownloadMessageCommand` (album), `DownloadPartCommand` (satu media) | Kejelasan UX antrian vs unduh langsung (Audit 3.4/3.5) |
| 4 | Preview foto/video, zoom in/out, filmstrip | ✅ Terpenuhi — `MediaViewerPage` + `ZoomPanController` (1x–8x), filmstrip, swipe/keyboard | Hanya jalur Windows (`#if WINDOWS`) — sesuai target aktif |
| 5 | Dokumentasi chat → MD: preview in-app, edit, viewer eksternal | ✅ Terpenuhi — `DocumentationRenderer`/`Service`, `MarkdownViewerPage` (preview + editor), blok penanda, tautan media relatif | **GAP D**: `Sender` hampir selalu kosong (hanya `post_author`) → README sering tanpa nama pengirim (Audit 3.7) |
| — | **UI/UX dalam Bahasa Inggris** (steering) | ⚠️ **Tidak konsisten** — Config modal, toast dokumentasi, error validasi root, banner editor masih Bahasa Indonesia | **GAP B**: campuran ID/EN (Audit 3.2) |

Kesimpulan: **fungsi inti ada semua**, tetapi ada **dua gap fungsional yang menyentuh janji
produk secara langsung** (GAP A: cakupan "semua media"; GAP D: pengirim di dokumentasi), **satu
gap integritas** yang mengancam janji "folder rapi & portabel" (GAP C), dan **satu gap
konsistensi** (GAP B: bahasa UI).

---

## 2. Rencana Fase Berikutnya

Prioritas mengikuti dampak ke janji produk + risiko data. Effort: S ≤½ hari · M 1–2 hari ·
L 3+ hari/spike.

### Fase A — Tutup gap fungsional terhadap janji produk

- [ ] **A1. "Download entire chat/topic" (GAP A).** Tambah command yang menelusuri seluruh
  riwayat (`GetMessagesAsync` paginasi sampai habis) sambil meng-enqueue media tiap halaman ke
  `DownloadQueueService`, dengan progres "halaman N, M media diantrikan" dan dapat dibatalkan.
  - Pertahankan unduhan sekuensih ramah rate-limit; jangan paralelkan fetch halaman secara agresif.
  - Bedakan dari "Download all" lama → ganti label jadi **"Download loaded"** vs **"Download
    entire chat"**, atau jadikan satu tombol dengan pilihan.
  - _Effort: M · Risiko: Sedang · Goal: #2_

- [ ] **A2. Isi `Sender` nyata (GAP D).** Resolve `from_id` → nama user/chat (cache peer) di
  `TelegramService.MapMessage`, bukan hanya `post_author`. README menjanjikan "pengirim".
  - _Effort: M–L · Risiko: Sedang · Goal: #5 · Ref Audit 3.7_

### Fase B — Integritas data (prasyarat keandalan janji "folder rapi & portabel")

- [ ] **B1. Skema folder `{id}_{judul}` (GAP C, P0).** Ubah `BuildTargetPath` **dan**
  `BuildRelativeFolder` agar memakai `chatId`/`topicId` sebagai anchor keunikan; judul jadi
  dekoratif (sanitasi agresif non-ASCII/emoji, cap panjang untuk `MAX_PATH`, boleh kosong).
  - Tanpa migrasi. Wajib test: judul duplikat, emoji-only, kosong.
  - _Effort: M · Risiko: Sedang · Ref Audit 1.1_

- [ ] **B2. Test harness `DownloadQueueService` + jalur unduhan langsung.** Prasyarat sebelum
  menyentuh lifecycle/cancel (area nol coverage, paling rawan regresi).
  - _Effort: M · Risiko: Rendah · Ref Audit §10 prasyarat_

- [ ] **B3. Lacak unduhan langsung di `HasActiveWork` + barrier worker-idle sebelum DB close.**
  Cegah file yatim saat ganti root di tengah unduhan langsung (`DownloadPartAsync`/
  `EnsureDownloadedAsync`).
  - _Effort: S–M (counter) lalu M–L (barrier) · Risiko: Tinggi · Ref Audit 1.2/1.4/1.5_

- [ ] **B4. Cancel benar-benar menghentikan transfer (spike).** Selidiki apakah WTelegram
  `DownloadFileAsync` mendukung `CancellationToken`; bila tidak, abort via dispose stream.
  - _Effort: L (spike dulu) · Risiko: Tinggi · Ref Audit 1.3_

### Fase C — Konsistensi UX & performa skala

- [ ] **C1. Unifikasi bahasa UI ke Bahasa Inggris (GAP B).** Sapu seluruh string user-facing:
  `ConfigViewModel`, `MarkdownViewerViewModel` (empty/banner), `MainViewModel`
  (`EnsureRootConfiguredAsync`, `OpenDocumentationAsync`), pesan `DownloadQueueService.NotReadyMessage`.
  - Komentar/dok **kode** tetap Bahasa Indonesia (lihat `structure.md`).
  - _Effort: M · Risiko: Rendah · Ref Audit 3.2_

- [ ] **C2. Throttle progress ke UI (≤10 update/detik)** di `ProgressTo.Report`.
  - _Effort: S · Risiko: Rendah · Ref Audit 2.2_

- [ ] **C3. Batch manifest lookup per halaman** (`WHERE chat_id=? AND message_id IN (...)`),
  ganti N query di `ApplyManifestStateAsync`.
  - _Effort: M · Risiko: Sedang · Ref Audit 2.3_

- [ ] **C4. Forum UX: empty state "Select a topic".** Jangan set `CurrentTitle` seolah chat
  aktif saat expand forum.
  - _Effort: S–M · Risiko: Rendah · Ref Audit 3.1_

- [ ] **C5. `QueryFolder` SQL prefix + index; batasi/virtualize `Messages`; auto-clear antrian
  yang mempertahankan ringkasan.**
  - _Effort: M masing-masing · Risiko: Sedang · Ref Audit 2.4/2.6/3.5_

---

## 3. Urutan Eksekusi yang Disarankan

```
A1 (download entire chat)        ← gap fungsional paling terlihat pengguna
 └─ C1 (unifikasi bahasa)         ← cepat, langsung menaikkan kualitas rilis
B2 (test harness)  ← prasyarat
 └─ B3 → B4 (lifecycle & cancel)  ← integritas saat unduhan besar berjalan
B1 (skema folder)                 ← integritas file di disk (P0)
A2 (sender) → C2/C3/C4/C5         ← kualitas dokumentasi + performa + polish
```

Rasional: A1 & C1 memberi dampak produk paling cepat. B2 wajib sebelum B3/B4 (lifecycle paling
rawan regresi). B1 sebaiknya digabung dengan B-series karena sama-sama menyentuh
`TelegramService`/`DownloadQueueService` dan butuh test baru.

---

## 4. Definisi Selesai (Definition of Done)

- Setiap item: `dotnet build` bersih + unit test terkait lulus + file/temporary uji dibersihkan.
- Perubahan pada `DownloadQueueService`/`TelegramService`/`DbLifecycleCoordinator` wajib disertai
  test perilaku (bukan hanya logika murni).
- String user-facing baru ditulis dalam **Bahasa Inggris**; komentar dalam **Bahasa Indonesia**.
- Tidak ada regresi pada 89 test eksisting.
