# syntax=docker/dockerfile:1.7
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH=arm64
ARG SERVICE
WORKDIR /src
COPY . .
RUN test -n "$SERVICE" \
    && dotnet restore "src/Services/$SERVICE/$SERVICE.csproj" --configfile NuGet.Config \
    && dotnet publish "src/Services/$SERVICE/$SERVICE.csproj" -c Release -a "$TARGETARCH" \
       --self-contained false --no-restore -o /out

FROM --platform=$TARGETPLATFORM mcr.microsoft.com/dotnet/aspnet:10.0-noble
ARG SERVICE
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    APP_DLL=$SERVICE.dll
RUN apt-get update \
    && apt-get install --no-install-recommends -y curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out .
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl --fail --silent http://127.0.0.1:8080/health >/dev/null || exit 1
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"$APP_DLL\""]
