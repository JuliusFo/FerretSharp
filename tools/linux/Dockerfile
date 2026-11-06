# Image for tools/linux/Test-Linux.ps1, built locally and never pushed: the .NET 10 SDK plus the .NET 8 runtimes
# the CI's Linux job installs (model host, FakeEfApp, the app the trace tests attach to).
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS net8
FROM mcr.microsoft.com/dotnet/sdk:10.0
COPY --from=net8 /usr/share/dotnet/shared /usr/share/dotnet/shared
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
