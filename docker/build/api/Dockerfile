# Use the ASP.NET runtime image as base
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

# Install Docker CLI
RUN apt-get update && apt-get install -y \
    curl \
    docker.io && \
    rm -rf /var/lib/apt/lists/*
    
# Use SDK image to build the app
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the project files
COPY ["GuideAntsApi/GuideAntsApi.csproj", "GuideAntsApi/"]
COPY ["GuideAntsApi.DataModel/GuideAntsApi.DataModel.csproj", "GuideAntsApi.DataModel/"]

# Restore dependencies
WORKDIR /src/GuideAntsApi
RUN dotnet restore "GuideAntsApi.csproj"

# Copy the rest of the source code
COPY . ..

# Build the application
RUN dotnet build "GuideAntsApi.csproj" -c Debug -o /app/build

FROM build AS publish
RUN dotnet publish "GuideAntsApi.csproj" -c Debug -o /app/publish /p:UseAppHost=false

# Final stage
FROM base AS final
WORKDIR /app

# Install vsdbg for remote debugging
RUN apt-get update && apt-get install -y unzip && \
    curl -sSL https://aka.ms/getvsdbgsh | /bin/sh /dev/stdin -v latest -l /vsdbg

COPY --from=publish /app/publish .

EXPOSE 8080
EXPOSE 8081

ENTRYPOINT ["dotnet", "GuideAntsApi.dll"]
