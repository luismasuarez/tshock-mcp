# Build stage: restore and publish the Release build.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the build props and the project file first so NuGet restore is cached
# independently of source changes.
COPY Directory.Build.props ./
COPY src/TShock.MCP.Server/TShock.MCP.Server.csproj src/TShock.MCP.Server/

RUN dotnet restore src/TShock.MCP.Server/TShock.MCP.Server.csproj

COPY src/ src/

RUN dotnet publish src/TShock.MCP.Server/TShock.MCP.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Runtime stage: only the published output and the .NET runtime.
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish ./

# The MCP transport is stdio, so the container must be started with `-i --rm`.
# Diagnostics are disabled because nothing listens on a debug port.
ENV DOTNET_EnableDiagnostics=0

# Run as the non-root user provided by the .NET base image. If
# TSHOCK_MCP_AUDIT_LOG points inside the container, mount a writable directory
# and make sure this user can write to it.
USER $APP_UID

ENTRYPOINT ["/app/tshock-mcp"]
