FROM node:22-bookworm-slim AS web
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci
COPY web-admin/ web-admin/
RUN npm run build:admin

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY server/LessonCue.Server/LessonCue.Server.csproj server/LessonCue.Server/
RUN dotnet restore server/LessonCue.Server/LessonCue.Server.csproj
COPY server/LessonCue.Server/ server/LessonCue.Server/
COPY --from=web /src/server/LessonCue.Server/wwwroot/ server/LessonCue.Server/wwwroot/
RUN dotnet publish server/LessonCue.Server/LessonCue.Server.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG TARGETARCH
ARG YT_DLP_VERSION=2026.08.19
ARG DENO_VERSION=2.9.7
ARG DENO_LINUX_X64_SHA256=c6527f24f4b16031d3ae4fa9f658d5f11534c8d84ce7dc8502420280919c3490
ARG DENO_LINUX_ARM64_SHA256=c832298b1ad4422481334855f6003e0f54145762c5a134f20a489511d2f65bbf
RUN apt-get update \
    && apt-get install -y --no-install-recommends --only-upgrade apt libapt-pkg6.0t64 \
    && apt-get install -y --no-install-recommends ffmpeg libreoffice-impress libreoffice-writer libreoffice-calc libreoffice-draw poppler-utils ca-certificates curl unzip util-linux coreutils \
        fonts-liberation \
    && optional_packages="" \
    && for candidate in libavcodec-extra libavcodec-extra59 libavcodec-extra60 libavcodec-extra61; do if apt-cache show "$candidate" >/dev/null 2>&1; then optional_packages="$optional_packages $candidate"; break; fi; done \
    && for candidate in libwebp7 libwebp6; do if apt-cache show "$candidate" >/dev/null 2>&1; then optional_packages="$optional_packages $candidate"; break; fi; done \
    && for candidate in libtheora0; do if apt-cache show "$candidate" >/dev/null 2>&1; then optional_packages="$optional_packages $candidate"; break; fi; done \
    && for candidate in libvpx9 libvpx8 libvpx7; do if apt-cache show "$candidate" >/dev/null 2>&1; then optional_packages="$optional_packages $candidate"; break; fi; done \
    && for candidate in libopus0; do if apt-cache show "$candidate" >/dev/null 2>&1; then optional_packages="$optional_packages $candidate"; break; fi; done \
    && for candidate in libvorbis0a; do if apt-cache show "$candidate" >/dev/null 2>&1; then optional_packages="$optional_packages $candidate"; break; fi; done \
    && for candidate in libheif1 libheif2; do if apt-cache show "$candidate" >/dev/null 2>&1; then optional_packages="$optional_packages $candidate"; break; fi; done \
    && for candidate in libavif16 libavif15; do if apt-cache show "$candidate" >/dev/null 2>&1; then optional_packages="$optional_packages $candidate"; break; fi; done \
    && for candidate in libjxl0 libjxl0.7; do if apt-cache show "$candidate" >/dev/null 2>&1; then optional_packages="$optional_packages $candidate"; break; fi; done \
    && for candidate in libopenjp2-8 libopenjp2-7; do if apt-cache show "$candidate" >/dev/null 2>&1; then optional_packages="$optional_packages $candidate"; break; fi; done \
    && if [ -n "$optional_packages" ]; then apt-get install -y --no-install-recommends $optional_packages; fi \
    && if [ "$TARGETARCH" = "amd64" ] && apt-cache show intel-media-va-driver >/dev/null 2>&1; then apt-get install -y --no-install-recommends intel-media-va-driver; fi \
    && if [ "$TARGETARCH" = "amd64" ] && apt-cache show i965-va-driver >/dev/null 2>&1; then apt-get install -y --no-install-recommends i965-va-driver; fi \
    && rm -rf /var/lib/apt/lists/*
RUN case "$TARGETARCH" in \
      arm64) asset=yt-dlp_linux_aarch64; expected=b16e4dab368a816cd05d477d698a605a6ae87ccee1c8ffd38fa21d7254141fcc ;; \
      *) asset=yt-dlp_linux; expected=58162f9bfdc27458ea47bfcb311cf47028f17d8154a8bf7d689861d46399230a ;; \
    esac \
    && curl -fL "https://github.com/yt-dlp/yt-dlp/releases/download/${YT_DLP_VERSION}/${asset}" -o /usr/local/bin/yt-dlp \
    && printf '%s  %s\n' "${expected}" /usr/local/bin/yt-dlp | sha256sum -c - \
    && chmod 0755 /usr/local/bin/yt-dlp \
    && test "$(/usr/local/bin/yt-dlp --version)" = "${YT_DLP_VERSION}"
RUN case "$TARGETARCH" in \
      arm64) deno_asset=deno-aarch64-unknown-linux-gnu.zip; deno_expected="$DENO_LINUX_ARM64_SHA256" ;; \
      amd64) deno_asset=deno-x86_64-unknown-linux-gnu.zip; deno_expected="$DENO_LINUX_X64_SHA256" ;; \
      *) echo "Unsupported Deno target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && curl -fL "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/${deno_asset}" -o /tmp/deno.zip \
    && printf '%s  %s\n' "${deno_expected}" /tmp/deno.zip | sha256sum -c - \
    && unzip -p /tmp/deno.zip deno > /usr/local/bin/deno \
    && rm /tmp/deno.zip \
    && chmod 0755 /usr/local/bin/deno \
    && test "$(/usr/local/bin/deno --version | sed -n '1s/^deno \([^ ]*\).*/\1/p')" = "${DENO_VERSION}"
RUN groupadd --gid 10001 lessoncue \
    && useradd --uid 10001 --gid 10001 --home-dir /data --shell /usr/sbin/nologin lessoncue \
    && install -d -o lessoncue -g lessoncue -m 0750 /data
WORKDIR /app
COPY --from=build /app/publish .
COPY installers/linux/lessoncue-media-worker /usr/local/libexec/lessoncue-media-worker
RUN chmod 0755 /usr/local/libexec/lessoncue-media-worker
ENV LESSONCUE_DATA_PATH=/data \
    LESSONCUE_HTTP_PORT=8080 \
    HOME=/data \
    XDG_CACHE_HOME=/data/.cache
EXPOSE 8080
VOLUME ["/data"]
USER 10001:10001
ENTRYPOINT ["dotnet", "LessonCue.Server.dll"]
