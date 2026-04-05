# fd-cs (fast-down C# Port)

This is a C# rewrite of the `fast-down` downloader CLI and its core dependencies.

## Features

- High performance asynchronous multithreaded downloading.
- Work-stealing algorithm (simulating `fast-steal` behavior) to keep all threads busy.
- Resumable downloads with SQLite database.
- Parallel chunk writing to disk using `RandomAccess` (similar to standard file API or mmap in concept but safer in C#).
- Beautiful console output utilizing `Spectre.Console`.

## Requirements

- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later.

## Compilation & Running

1. Open your terminal in this directory (`fd-cs`).
2. Run the build command:
   ```bash
   dotnet build -c Release
   ```
3. To run the application directly:
   ```bash
   dotnet run -c Release -- [command] [options]
   ```
   Alternatively, you can run the built executable in `bin/Release/net8.0/`.

### Native AOT (Optional)
To build a standalone executable with no runtime dependencies and extremely fast startup:
```bash
dotnet publish -c Release -r linux-x64 # or win-x64, osx-arm64
```
This requires platform-specific C++ build tools (e.g. `build-essential` on Linux).

## Usage Examples

Download a file:
```bash
./fd-cs download https://example.com/file.zip -d /path/to/save
```

List database entries:
```bash
./fd-cs list
```
