# screenX

Alat tangkap layar untuk Windows 10/11 yang bekerja seperti Snipping Tool (`Win + Shift + S`), dibuka dengan `Ctrl + Shift + S`, dengan tambahan
perekam layar, color picker, dan text extractor (OCR).

## Fitur

| Mode | Cara pakai | Hasil |
|---|---|---|
| Snip persegi | Seret area | Disalin ke clipboard + disimpan ke `Pictures\screenX` |
| Snip bebas (freeform) | Gambar bentuk bebas | PNG transparan di luar bentuk |
| Snip jendela | Arahkan ke jendela lalu klik | Seluruh jendela tertangkap |
| Snip layar penuh | Klik tombol layar penuh | Semua monitor tertangkap |
| Rekam layar | Seret area → **Mulai** → hitung mundur 3-2-1 | MP4 (H.264) di `Videos\screenX`, file disalin ke clipboard |
| Color picker | Klik piksel (panah = geser 1px, Shift+panah = 10px, Enter = pilih) | HEX disalin; HEX/RGB/HSL bisa disalin dari notifikasi |
| Text extractor | Seret area yang berisi teks | Teks (Windows OCR) disalin ke clipboard |

### Toolbar atas
`[ foto | video ]  [ bentuk snip ▾ ]  [ markup langsung ]  |  [ color picker ]  [ text extractor ]  |  [ × ]`

Menu bentuk snip: Rectangle, Window, Full screen, Freeform.

### Markup langsung (tombol biru aktif)
Setelah memilih area, toolbar muncul di bawah seleksi:
`[ Capture ▾ ]  [ Share ]  [ Google Lens ]  |  [ Pulpen ] [ Highlighter ] [ Penghapus ] [ Bentuk ]  |  [ Undo ] [ Redo ]  |  [ Hapus semua ]`

- **Capture ▾**: Capture (Ctrl+Enter), Save to (Ctrl+S), Copy only (Ctrl+C)
- Klik pulpen/highlighter yang sedang aktif untuk membuka panel **Colours** (30 warna) dan **Size**
- Klik penghapus yang sedang aktif untuk **Erase all mark-ups**
- **Bentuk** membuka bar: emoji, Rectangle, Ellipse, Line, Arrow, **Fill** (atau No fill), **Outline** (warna + ukuran). Tahan Shift untuk persegi/lingkaran
- **Share** membuka jendela berbagi Windows; **Google Lens** mengunggah snip ke Google Lens dan membuka hasil pencarian gambar di browser
- Ctrl+Z / Ctrl+Y untuk undo/redo, Esc untuk batal

Matikan tombol biru bila ingin snip langsung tersalin tanpa langkah markup.

Setelah snip, klik notifikasi untuk membuka **editor**: pulpen, highlighter, penghapus, 6 warna, ukuran,
undo, salin (Ctrl+C), simpan sebagai PNG/JPG/BMP (Ctrl+S), dan ekstrak teks dari gambar.

## Shortcut

| Shortcut | Aksi |
|---|---|
| `Ctrl + Shift + S` | Buka overlay screenX (mode terakhir) |
| `Esc` / klik kanan | Batal |

`Win + Shift + S` tetap membuka Snipping Tool bawaan Windows, jadi keduanya bisa dibandingkan.
Rekam layar, color picker, dan text extractor dipilih dari toolbar overlay atau menu tray.

Ikon tray: klik kiri = snip baru, klik kanan = menu (folder, frame rate rekaman, kursor di rekaman,
simpan otomatis, jalankan saat Windows mulai, keluar).

Baris perintah (juga bekerja bila screenX sudah berjalan):
`screenX.exe --snip | --fullscreen | --record | --color | --text | --background`

## Build & jalankan

Butuh .NET 10 SDK dan Windows 10 versi 2004 atau lebih baru.

```bash
dotnet build -c Release
```

```bash
bin/Release/net10.0-windows10.0.19041.0/screenX.exe
```

Perekaman layar butuh `ffmpeg` (di PATH, atau `ffmpeg.exe` di samping `screenX.exe`):

```bash
winget install Gyan.FFmpeg
```

Text extractor memakai bahasa OCR Windows yang terpasang (bahasa profil pengguna, atau English bila tidak ada).

Tes otomatis (capture, OCR, warna, render UI, rekaman 2 detik):
`screenX.exe --selftest <folder-output>`

## Catatan

- Rekaman belum menyertakan audio (mikrofon/suara sistem).
- Pengaturan disimpan di `%AppData%\screenX\settings.json`.
