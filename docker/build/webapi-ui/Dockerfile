FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api-build
WORKDIR /src/server
COPY server/ ./
RUN dotnet restore "GuideAntsApi/GuideAntsApi.csproj"
RUN dotnet publish "GuideAntsApi/GuideAntsApi.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime-base
WORKDIR /app
COPY --from=api-build /app/publish/ ./
COPY client/dist-browser/ ./ui/

ENV ASPNETCORE_URLS=http://+:8080 \
    Ui__RootPath=/app/ui \
    VideoAudioExtraction__FfmpegPath=/usr/bin/ffmpeg

EXPOSE 8080

FROM runtime-base AS runtime
RUN apt-get update && apt-get install -y \
    docker.io \
    ffmpeg && \
    rm -rf /var/lib/apt/lists/*

ENTRYPOINT ["dotnet", "GuideAntsApi.dll"]

FROM mcr.microsoft.com/mssql/server:2025-RTM-ubuntu-22.04 AS runtime-slim
USER root
RUN apt-get update \
    && apt-get install -y curl gnupg ca-certificates \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && curl -fsSL https://packages.microsoft.com/config/ubuntu/22.04/mssql-server-2025.list | tee /etc/apt/sources.list.d/mssql-server-2025.list > /dev/null \
    && curl -fsSL https://packages.microsoft.com/config/ubuntu/22.04/prod.list | tee /etc/apt/sources.list.d/microsoft-prod.list > /dev/null \
    && apt-get update \
    && apt-get install -y \
        aspnetcore-runtime-8.0 \
        ffmpeg \
        mssql-server-fts \
        tini \
        util-linux \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=runtime-base /app/ /app/
COPY --chmod=755 server/GuideAntsApi/Container/entrypoint-slim.sh /usr/local/bin/entrypoint-slim.sh

ENV ACCEPT_EULA=Y \
    MSSQL_PID=Express \
    MSSQL_TCP_PORT=1433 \
    MSSQL_DB_NAME=guideants-dev \
    ASPNETCORE_URLS=http://+:8080 \
    FileStorage__Path=/app/ContentFiles \
    Ui__RootPath=/app/ui \
    VideoAudioExtraction__FfmpegPath=/usr/bin/ffmpeg \
    ALLOWED_ORIGINS=* \
    SettingsSecrets__ActiveKeyId=local-dev \
    SettingsSecrets__Keys__local-dev=MDEyMzQ1Njc4OUFCQ0RFRjAxMjM0NTY3ODlBQkNERUY= \
    LlamaCpp__BaseUrl=http://127.0.0.1:9/llama-cpp \
    ServiceRouting__Containers__guideants-ai__BaseUrl=http://127.0.0.1:9/sandbox \
    SearXngSearch__BaseUrl=http://127.0.0.1:8091 \
    BrowserRendering__BaseUrl=http://127.0.0.1:8092

EXPOSE 8080 1433

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/entrypoint-slim.sh"]
