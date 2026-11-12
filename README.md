# screenX

by [funwkwk](https://funwkwk.my.id)

screenX is an open-source screen capture tool. It works like the Windows Snipping Tool and adds screen recording,
a colour picker, a text extractor (OCR), in-place markup and Google Lens image search.

- **Windows 10 / 11**: available now (this repository)
- **macOS**: planned

The source code is free under the [MIT License](LICENSE). Ready-to-install builds of screenX for Windows
(and later macOS) will be offered as a paid download. Buying the installer supports development; you can
always build screenX yourself from this source.

## Features

- Rectangle, window, full screen and freeform snips
- In-place markup: pen, highlighter, eraser, shapes, emoji, undo/redo
- Resizable selection after snipping, with corner and edge handles
- Screen recording to MP4
- Colour picker with magnifier (HEX, RGB, HSL)
- Text extractor using the built-in Windows OCR
- Google Lens image search and the Windows share sheet
- Snips copied to the clipboard and auto-saved to `Pictures\screenX`

## Usage

Open screenX with **Ctrl + Shift + S**. Windows keeps its own **Win + Shift + S**, so both can be used side by side.

### Top toolbar

`[ snip | record ]  [ snip mode ▾ ]  [ quick markup ]  |  [ colour picker ]  [ text extractor ]  |  [ × ]`

| Mode | How | Result |
|---|---|---|
| Rectangle | Drag an area | Markup bar (when quick markup is on), otherwise copied and saved to `Pictures\screenX` |
| Window | Hover a window and click | The whole window is captured |
| Full screen | Pick it from the snip mode menu | All monitors are captured |
| Freeform | Draw any shape | PNG, transparent outside the shape |
| Record | Drag an area, press **Start**, wait for the 3-2-1 countdown | MP4 (H.264) in `Videos\screenX`, file copied to the clipboard |
| Colour picker | Click a pixel (arrows = 1 px, Shift + arrows = 10 px, Enter = pick) | HEX copied; HEX, RGB and HSL can be copied from the notification |
| Text extractor | Drag over text | Text copied to the clipboard |

In record mode the snip mode and quick markup buttons stay in place but are disabled, so the toolbar keeps its width.

### Markup bar (rectangle snips with quick markup on)

The selection stays adjustable: drag any of the four corner brackets or the four edge bars to resize it.
Anything already drawn stays where it is, and the capture uses the new size.

`[ Capture ▾ ]  [ Share ]  [ Google Lens ]  |  [ Pen ] [ Highlighter ] [ Eraser ] [ Shapes ]  |  [ Undo ] [ Redo ]  |  [ Discard ]`

- **Capture ▾**: Capture (Ctrl + Enter), Save to (Ctrl + S), Copy only (Ctrl + C)
- Click the active pen or highlighter to open **Colours** (30 colours) and **Size**
- Click the active eraser for **Erase all mark-ups**
- **Shapes** opens a second bar: emoji, Rectangle, Ellipse, Line, Arrow, **Fill** (or No fill) and **Outline** (colour and size). Hold Shift for a square or circle
- Placed emoji can be moved, resized from the corners and rotated with the round button above them; Delete removes the selected emoji
- **Share** opens the Windows share sheet
- **Google Lens** opens Google's "Search any image with Google Lens" dialog in your browser and pastes the snip into it for you, so the results page shows your image. If the Google tab cannot be brought to the front, a notification lets you paste (Ctrl + V) or drag the image file in yourself
- **Discard** (trash icon) or Esc closes the snip without saving
- Ctrl + Z / Ctrl + Y undo and redo

Clicking the "Snip copied" notification opens the **editor**: pen, highlighter, eraser, colours, size, undo,
copy (Ctrl + C), save as PNG/JPG/BMP (Ctrl + S) and extract text.

### Tray icon

Left click starts a new snip. Right click opens the menu: full screen snip, screen record, colour picker,
text extractor, screenshots and recordings folders, **Settings** (auto-save, cursor in recordings,
recording frame rate, start with Windows) and Exit.

Command line (also works while screenX is already running):
`screenX.exe --snip | --fullscreen | --record | --color | --text | --background`

## Build from source

Requirements: the .NET 10 SDK and Windows 10 version 2004 or later.

```bash
dotnet build -c Release
```

```bash
bin/Release/net10.0-windows10.0.19041.0/screenX.exe
```

Screen recording needs `ffmpeg`, either on `PATH` or as `ffmpeg.exe` next to `screenX.exe`:

```bash
winget install Gyan.FFmpeg
```

The text extractor uses the Windows OCR languages installed on the PC (your profile languages, or English when none match).

Self-test (capture, OCR, colours, UI renders and a 2-second recording; writes images and a log to the folder):
`screenX.exe --selftest <output-folder>`

## Notes

- Recordings have no audio yet (microphone or system sound).
- Settings are stored in `%AppData%\screenX\settings.json`; Google Lens attempts are logged to `%AppData%\screenX\lens.log` (window titles only).

## Third-party software

- [Vortice.Direct2D1](https://github.com/amerkoleci/Vortice.Windows) (MIT License) renders colour emoji with Direct2D.
- [FFmpeg](https://ffmpeg.org/) is used for screen recording. It is **not** included in this repository; screenX runs
  the copy installed on the PC. FFmpeg is licensed under the LGPL or GPL depending on the build, so any installer
  that bundles `ffmpeg.exe` must follow the terms of that build's licence.

## License

screenX is released under the [MIT License](LICENSE). Copyright (c) 2026 [funwkwk](https://funwkwk.my.id).
