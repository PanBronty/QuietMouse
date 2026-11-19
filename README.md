# QuietMouse

QuietMouse keeps the pointer at the same physical height when it crosses between displays with different pixel densities. It runs in the Windows notification area and supports extended desktops arranged in one horizontal row, including portrait displays.

## Using QuietMouse

On Windows 10 or 11 x64, extract the release ZIP and run `QuietMouse.exe`. Open **Display settings** from its tray icon to set the height of each display above or below its neighbor. QuietMouse reads the screen dimensions reported by each monitor. If the size is marked **estimated**, measure the visible screen height and enter it in millimeters.

Press **F1** in the editor for calibration, keyboard shortcuts, and the optional **Cross unmatched edges** setting. [Read the user guide](Getting%20started.txt).

## Building from source

Use 64-bit Windows with Visual Studio Build Tools (Desktop development with C++ and the Windows SDK), the .NET Framework 4.x C# compiler, and Windows `tar.exe`. From the repository directory, run:

```bat
build.cmd
```

The script builds the native C++ tray application and the C# settings editor into `portable\` and creates `QuietMouse-Windows-x64.zip` for distribution.

## License

[MIT License](LICENSE) © 2026 PanBronty.
