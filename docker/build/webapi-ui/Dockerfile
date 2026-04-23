FROM mcr.microsoft.com/dotnet/sdk:8.0 AS api-build
WORKDIR /src/server
COPY server/ ./
RUN dotnet restore "GuideAntsApi/GuideAntsApi.csproj"
RUN dotnet publish "GuideAntsApi/GuideAntsApi.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y \
    docker.io \
    ffmpeg && \
    rm -rf /var/lib/apt/lists/*

COPY --from=api-build /app/publish/ ./
COPY client/dist-browser/ ./ui/

ENV ASPNETCORE_URLS=http://+:8080
ENV Ui__RootPath=/app/ui
ENV VideoAudioExtraction__FfmpegPath=/usr/bin/ffmpeg

EXPOSE 8080

ENTRYPOINT ["dotnet", "GuideAntsApi.dll"]
