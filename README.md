# screenX

A screen capture tool for Windows 10/11 that works like Snipping Tool, with screen recording,
a colour picker, a text extractor (OCR) and Google Lens image search.

Open it with **Ctrl + Shift + S**. Windows keeps its own **Win + Shift + S**, so both can be compared side by side.

## Top toolbar

`[ snip | record ]  [ snip mode ▾ ]  [ quick markup ]  |  [ colour picker ]  [ text extractor ]  |  [ × ]`

| Mode | How | Result |
|---|---|---|
| Rectangle | Drag an area | Markup bar (when quick markup is on), otherwise copied + saved to `Pictures\screenX` |
| Window | Hover a window and click | The whole window is captured |
| Full screen | Pick it from the snip mode menu | All monitors are captured |
| Freeform | Draw any shape | PNG, transparent outside the shape |
| Record | Drag an area → **Start** → 3-2-1 countdown | MP4 (H.264) in `Videos\screenX`, file copied to the clipboard |
| Colour picker | Click a pixel (arrows = 1 px, Shift+arrows = 10 px, Enter = pick) | HEX copied; HEX/RGB/HSL can be copied from the notification |
| Text extractor | Drag over text | Text (Windows OCR) copied to the clipboard |

In record mode the snip mode and quick markup buttons stay in place but are disabled, so the toolbar keeps its width.

## Markup bar (Rectangle snips with quick markup on)

The selection stays adjustable: drag any of the four corner brackets or the four edge bars to resize it.
Anything already drawn stays where it is, and the capture uses the new size.

`[ Capture ▾ ]  [ Share ]  [ Google Lens ]  |  [ Pen ] [ Highlighter ] [ Eraser ] [ Shapes ]  |  [ Undo ] [ Redo ]  |  [ Discard ]`

- **Capture ▾**: Capture (Ctrl+Enter), Save to (Ctrl+S), Copy only (Ctrl+C)
- Click the active pen or highlighter to open **Colours** (30 colours) and **Size**
- Click the active eraser for **Erase all mark-ups**
- **Shapes** opens a second bar: emoji, Rectangle, Ellipse, Line, Arrow, **Fill** (or No fill) and **Outline** (colour + size). Hold Shift for a square or circle
- **Share** opens the Windows share sheet
- **Google Lens** opens Google's "Search any image with Google Lens" dialog in your browser and pastes the snip into it for you, so the results page shows your image. The paste is only sent once that Google tab is in front; if it cannot be, a notification lets you paste (Ctrl+V) or drag the image file in yourself
- **Discard** (trash icon) or Esc closes the snip without saving
- Ctrl+Z / Ctrl+Y undo and redo

Clicking the "Snip copied" notification opens the **editor**: pen, highlighter, eraser, colours, size, undo,
copy (Ctrl+C), save as PNG/JPG/BMP (Ctrl+S) and extract text.

## Tray icon

Left click starts a new snip. Right click opens the menu: full screen snip, screen record, colour picker,
text extractor, screenshots and recordings folders, and **Settings** (auto-save, cursor in recordings,
recording frame rate, start with Windows), plus Exit.

Command line (also works while screenX is already running):
`screenX.exe --snip | --fullscreen | --record | --color | --text | --background`

## Build and run

Requires the .NET 10 SDK and Windows 10 version 2004 or later.

```bash
dotnet build -c Release
```

```bash
bin/Release/net10.0-windows10.0.19041.0/screenX.exe
```

Screen recording needs `ffmpeg` (on PATH, or `ffmpeg.exe` next to `screenX.exe`):

```bash
winget install Gyan.FFmpeg
```

The text extractor uses the installed Windows OCR languages (your profile languages, or English when none match).
Colour emoji are drawn with Direct2D through the `Vortice.Direct2D1` package, because WPF only renders emoji in monochrome.

Self-test (capture, OCR, colours, UI renders, a 2-second recording, Google Lens upload of a generated sample image):
`screenX.exe --selftest <output-folder>`

## Notes

- Recordings have no audio yet (microphone or system sound).
- Settings are stored in `%AppData%\screenX\settings.json`.
